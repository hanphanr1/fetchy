using Fetchy.Core.Engine;
using Fetchy.Core.Verification;
using Xunit;

namespace Fetchy.Core.Tests;

public sealed class HashVerifierTests : IDisposable
{
    private readonly string _tempDir;

    public HashVerifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fetchy-hash-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task Sha256AndMd5_MatchCaseInsensitivelyAndDetectMismatches()
    {
        byte[] data = TestHttpServer.CreateDeterministicPayload(300 * 1024, seed: 901);
        string filePath = Path.Combine(_tempDir, "sample.bin");
        await File.WriteAllBytesAsync(filePath, data);

        string expectedSha256 = TestHttpServer.ComputeSha256(data);
        string expectedMd5 = TestHttpServer.ComputeMd5(data);

        var shaLower = await HashVerifier.VerifySha256Async(filePath, expectedSha256);
        var shaUpper = await HashVerifier.VerifySha256Async(filePath, expectedSha256.ToUpperInvariant());
        var shaWrong = await HashVerifier.VerifySha256Async(filePath, new string('0', 64));

        Assert.True(shaLower.IsMatch);
        Assert.True(shaUpper.IsMatch);
        Assert.False(shaWrong.IsMatch);
        Assert.Equal(expectedSha256, shaWrong.ComputedHex);

        var md5Lower = await HashVerifier.VerifyMd5Async(filePath, expectedMd5);
        var md5Upper = await HashVerifier.VerifyMd5Async(filePath, expectedMd5.ToUpperInvariant());
        var md5Wrong = await HashVerifier.VerifyMd5Async(filePath, new string('f', 32));

        Assert.True(md5Lower.IsMatch);
        Assert.True(md5Upper.IsMatch);
        Assert.False(md5Wrong.IsMatch);
        Assert.Equal(expectedMd5, md5Wrong.ComputedHex);
    }

    [Fact]
    public async Task Downloader_ThrowsHashMismatchException_WhenExpectedSha256DoesNotMatch()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(128 * 1024, seed: 902);
        await using var server = new TestHttpServer(payload);
        using var downloader = new SegmentedDownloader();

        string outputPath = Path.Combine(_tempDir, "mismatch.bin");
        var ex = await Assert.ThrowsAsync<HashMismatchException>(async () =>
        {
            await downloader.DownloadAsync(
                server.GetUri("mismatch.bin"),
                outputPath,
                new DownloadOptions
                {
                    ExpectedSha256 = new string('a', 64)
                });
        });

        Assert.False(ex.VerificationResult.IsMatch);
        Assert.Equal(server.PayloadSha256, ex.VerificationResult.ComputedHex);
    }
}
