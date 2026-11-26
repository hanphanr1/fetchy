using Fetchy.Core.Engine;
using Fetchy.Core.Http;
using Fetchy.Core.Management;
using Fetchy.Core.Storage;
using Xunit;

namespace Fetchy.Core.Tests;

public sealed class SecurityAndSanitizerTests : IDisposable
{
    private readonly string _tempDir;

    public SecurityAndSanitizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fetchy-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\evil.exe", "evil.exe")]
    [InlineData("sub/dir/file.bin", "file.bin")]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "cmd.exe")]
    [InlineData("../../../", PathSanitizer.DefaultFileName)]
    [InlineData("..", PathSanitizer.DefaultFileName)]
    [InlineData(".", PathSanitizer.DefaultFileName)]
    [InlineData("bad\r\nname\0.txt", "badname.txt")]
    public void SanitizeFileName_NeutralizesTraversalAndControlChars(string raw, string expected)
    {
        string sanitized = PathSanitizer.SanitizeFileName(raw);
        Assert.Equal(expected, sanitized);
        Assert.DoesNotContain("/", sanitized);
        Assert.DoesNotContain("\\", sanitized);
        Assert.DoesNotContain("..", sanitized);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("PRN.txt", "_PRN.txt")]
    [InlineData("AUX.tar.gz", "_AUX.tar.gz")]
    [InlineData("con.", "_con")]
    [InlineData("nul   ", "_nul")]
    public void SanitizeFileName_RenamesWindowsReservedDeviceNames(string raw, string expected)
    {
        string sanitized = PathSanitizer.SanitizeFileName(raw);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void EnsurePathContainedInDirectory_RejectsEscapingPath()
    {
        string escaped = Path.GetFullPath(Path.Combine(_tempDir, "..", "escaped.bin"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            PathSanitizer.EnsurePathContainedInDirectory(_tempDir, escaped));

        string safe = PathSanitizer.ResolveSafeDestinationPath(_tempDir, "../../safe.bin");
        Assert.StartsWith(_tempDir, safe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("safe.bin", Path.GetFileName(safe));
    }

    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=\"fallback.txt\"; filename*=UTF-8''my%20report%202026.pdf", "my report 2026.pdf")]
    [InlineData("attachment; filename=\"../../CON.txt\"", "_CON.txt")]
    public void ContentDisposition_ExtractsAndSanitizesFileName(string header, string expected)
    {
        string? name = PathSanitizer.ExtractFileNameFromContentDisposition(header);
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/archive.zip")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/file\r\nInjected: 1")]
    [InlineData("https://example.com/file\0.bin")]
    [InlineData("not-a-uri")]
    [InlineData("")]
    public void UriValidator_RejectsDisallowedSchemesAndControlCharacters(string badUrl)
    {
        Assert.Throws<ArgumentException>(() => UriValidator.ValidateHttpUri(badUrl));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/test.bin")]
    [InlineData("https://example.com/releases/fetchy.zip")]
    public void UriValidator_AcceptsValidHttpAndHttpsUris(string validUrl)
    {
        Uri parsed = UriValidator.ValidateHttpUri(validUrl);
        Assert.NotNull(parsed);
    }

    [Fact]
    public async Task SensitiveHeaders_AreNeverWrittenToStateFileOrDownloadsCatalog()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(1024 * 1024, seed: 801);
        await using var server = new TestHttpServer(payload)
        {
            PerRequestBytesPerSecond = (_, _, _) => 300_000
        };

        var headersWithSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://example.com/downloads",
            ["User-Agent"] = "FetchyTestAgent/1.0",
            ["Cookie"] = "session=SUPER_SECRET_COOKIE_VALUE",
            ["Authorization"] = "Bearer SUPER_SECRET_BEARER_TOKEN",
            ["Proxy-Authorization"] = "Basic SUPER_SECRET_PROXY_CRED",
            ["X-Api-Key"] = "SUPER_SECRET_API_KEY"
        };

        string catalogPath = Path.Combine(_tempDir, "downloads.json");
        string outputPath = Path.Combine(_tempDir, "secret-check.bin");
        string statePath = DownloadStateStore.GetStateFilePath(outputPath);

        await using var manager = new DownloadManager(catalogPath, _tempDir, maxConcurrentDownloads: 2);

        var readyToPause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += (_, snap) =>
        {
            if (snap.DownloadedBytes >= 64 * 1024 && File.Exists(statePath))
            {
                readyToPause.TrySetResult();
            }
        };

        var entry = await manager.EnqueueAsync(
            server.GetUri("secret-check.bin"),
            outputPath,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024,
                ProgressReportInterval = TimeSpan.FromMilliseconds(25),
                Headers = headersWithSecrets
            },
            autoStart: true);

        await readyToPause.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await manager.PauseAsync(entry.Id);

        Assert.True(File.Exists(catalogPath));
        string catalogJson = await File.ReadAllTextAsync(catalogPath);
        AssertNoSecretLeakage(catalogJson);
        Assert.Contains("https://example.com/downloads", catalogJson);
        Assert.Contains("FetchyTestAgent/1.0", catalogJson);

        Assert.True(File.Exists(statePath));
        string stateJson = await File.ReadAllTextAsync(statePath);
        AssertNoSecretLeakage(stateJson);
        Assert.Contains("https://example.com/downloads", stateJson);
        Assert.Contains("FetchyTestAgent/1.0", stateJson);
    }

    private static void AssertNoSecretLeakage(string jsonContent)
    {
        Assert.DoesNotContain("SUPER_SECRET_COOKIE_VALUE", jsonContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUPER_SECRET_BEARER_TOKEN", jsonContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUPER_SECRET_PROXY_CRED", jsonContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUPER_SECRET_API_KEY", jsonContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"cookie\"", jsonContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"authorization\"", jsonContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"proxy-authorization\"", jsonContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"x-api-key\"", jsonContent, StringComparison.OrdinalIgnoreCase);
    }
}
