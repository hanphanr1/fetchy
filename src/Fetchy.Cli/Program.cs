using System.Reflection;
using Fetchy.Core.Engine;
using Fetchy.Core.Verification;

namespace Fetchy.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            CliCommand command = CliParser.Parse(args);
            return command switch
            {
                HelpCommand => PrintHelp(),
                VersionCommand => PrintVersion(),
                DownloadCommand dl => await RunDownloadAsync(dl, cts.Token).ConfigureAwait(false),
                ResumeCommand res => await RunResumeAsync(res, cts.Token).ConfigureAwait(false),
                VerifyCommand ver => await RunVerifyAsync(ver, cts.Token).ConfigureAwait(false),
                BenchmarkOptionsCommand bench => await BenchmarkCommand.RunAsync(bench.SizeMb, bench.Connections, cts.Token).ConfigureAwait(false),
                _ => 1
            };
        }
        catch (CliParseException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        catch (HashMismatchException ex)
        {
            Console.Error.WriteLine($"Verification failed: {ex.Message}");
            return 3;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Download paused/cancelled. Checkpoint state saved if applicable.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine(CliParser.GetUsageText());
        return 0;
    }

    private static int PrintVersion()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        Console.WriteLine($"fetchy {version}");
        return 0;
    }

    private static async Task<int> RunDownloadAsync(DownloadCommand cmd, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(cmd.Referer))
        {
            headers["Referer"] = cmd.Referer;
        }

        if (!string.IsNullOrWhiteSpace(cmd.UserAgent))
        {
            headers["User-Agent"] = cmd.UserAgent;
        }

        var options = new DownloadOptions
        {
            MaxConnections = cmd.Connections,
            MaxBytesPerSecond = cmd.MaxBytesPerSecond,
            ExpectedSha256 = cmd.ExpectedSha256,
            ExpectedMd5 = cmd.ExpectedMd5,
            Overwrite = cmd.Overwrite,
            Headers = headers.Count > 0 ? headers : null
        };

        var renderer = new ConsoleProgressRenderer();
        using var downloader = new SegmentedDownloader();

        DownloadResult result = await downloader.DownloadAsync(cmd.Url, cmd.OutputPath, options, renderer, ct).ConfigureAwait(false);
        renderer.CompleteLine();
        PrintDownloadSummary(result);
        return 0;
    }

    private static async Task<int> RunResumeAsync(ResumeCommand cmd, CancellationToken ct)
    {
        var options = new DownloadOptions
        {
            MaxConnections = cmd.Connections,
            MaxBytesPerSecond = cmd.MaxBytesPerSecond,
            Overwrite = cmd.Overwrite
        };

        var renderer = new ConsoleProgressRenderer();
        using var downloader = new SegmentedDownloader();

        DownloadResult result = await downloader.ResumeAsync(cmd.StateFilePath, options, cmd.RefreshedUrl, renderer, ct).ConfigureAwait(false);
        renderer.CompleteLine();
        PrintDownloadSummary(result);
        return 0;
    }

    private static async Task<int> RunVerifyAsync(VerifyCommand cmd, CancellationToken ct)
    {
        if (!File.Exists(cmd.FilePath))
        {
            Console.Error.WriteLine($"File not found: {cmd.FilePath}");
            return 1;
        }

        bool allMatch = true;
        if (!string.IsNullOrWhiteSpace(cmd.ExpectedSha256))
        {
            var sha = await HashVerifier.VerifySha256Async(cmd.FilePath, cmd.ExpectedSha256, ct).ConfigureAwait(false);
            Console.WriteLine($"SHA-256: {(sha.IsMatch ? "OK" : "MISMATCH")} (computed: {sha.ComputedHex})");
            allMatch &= sha.IsMatch;
        }

        if (!string.IsNullOrWhiteSpace(cmd.ExpectedMd5))
        {
            var md5 = await HashVerifier.VerifyMd5Async(cmd.FilePath, cmd.ExpectedMd5, ct).ConfigureAwait(false);
            Console.WriteLine($"MD5:     {(md5.IsMatch ? "OK" : "MISMATCH")} (computed: {md5.ComputedHex})");
            allMatch &= md5.IsMatch;
        }

        return allMatch ? 0 : 3;
    }

    private static void PrintDownloadSummary(DownloadResult result)
    {
        Console.WriteLine($"Saved:       {result.FilePath}");
        Console.WriteLine($"Size:        {ConsoleProgressRenderer.FormatBytes(result.BytesDownloaded)}");
        Console.WriteLine($"Elapsed:     {result.Elapsed.TotalSeconds:F2}s ({ConsoleProgressRenderer.FormatSpeed(result.AverageSpeedBps)})");
        Console.WriteLine($"Mode:        {(result.UsedSegmentedDownload ? $"{result.ConnectionsUsed} segments ({result.WorkStealCount} work-steals)" : "Single-stream")}");
        Console.WriteLine($"Category:    {result.Category}");
        if (result.Sha256Verification is not null)
        {
            Console.WriteLine($"SHA-256:     VERIFIED ({result.Sha256Verification.ComputedHex})");
        }

        if (result.Md5Verification is not null)
        {
            Console.WriteLine($"MD5:         VERIFIED ({result.Md5Verification.ComputedHex})");
        }
    }
}
