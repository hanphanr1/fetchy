using System.Globalization;
using Fetchy.Core.Engine;
using Fetchy.Core.Verification;

namespace Fetchy.Cli;

/// <summary>
/// Executes a local loopback benchmark comparing 1-stream vs N-segment dynamic work-stealing downloads
/// with bit-exact SHA-256 verification and automatic temporary file cleanup.
/// </summary>
internal static class BenchmarkCommand
{
    public static async Task<int> RunAsync(int sizeMb, int connections, CancellationToken cancellationToken)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"fetchy-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Console.WriteLine($"Starting local loopback HTTP/1.1 Range benchmark ({sizeMb} MiB payload, 1 vs {connections} connections)...");

            await using var server = BenchmarkServer.Start(sizeMb, perConnectionBytesPerSecond: 3_000_000);
            using var downloader = new SegmentedDownloader();

            string singleFile = Path.Combine(tempDir, "single.bin");
            string multiFile = Path.Combine(tempDir, "multi.bin");

            // 1. Baseline: 1-connection download
            DownloadResult singleResult = await downloader.DownloadAsync(
                server.EndpointUri,
                singleFile,
                new DownloadOptions
                {
                    MaxConnections = 1,
                    Overwrite = true,
                    ExpectedSha256 = server.ExpectedSha256
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 2. Accelerated: N-connection dynamic work-stealing download
            DownloadResult multiResult = await downloader.DownloadAsync(
                server.EndpointUri,
                multiFile,
                new DownloadOptions
                {
                    MaxConnections = connections,
                    MinSplitBytes = 128 * 1024,
                    Overwrite = true,
                    ExpectedSha256 = server.ExpectedSha256
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            HashVerificationResult singleHash = singleResult.Sha256Verification!;
            HashVerificationResult multiHash = multiResult.Sha256Verification!;

            double singleMbPerSec = singleResult.Elapsed.TotalSeconds > 0
                ? (server.PayloadSizeBytes / (1024.0 * 1024.0)) / singleResult.Elapsed.TotalSeconds
                : 0;
            double multiMbPerSec = multiResult.Elapsed.TotalSeconds > 0
                ? (server.PayloadSizeBytes / (1024.0 * 1024.0)) / multiResult.Elapsed.TotalSeconds
                : 0;
            double speedup = multiResult.Elapsed.TotalSeconds > 0
                ? singleResult.Elapsed.TotalSeconds / multiResult.Elapsed.TotalSeconds
                : 0;

            return PrintTable(sizeMb, connections, server.ExpectedSha256, singleResult, multiResult, singleMbPerSec, multiMbPerSec, speedup, singleHash, multiHash);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static int PrintTable(
        int sizeMb,
        int connections,
        string expectedSha256,
        DownloadResult singleResult,
        DownloadResult multiResult,
        double singleMbPerSec,
        double multiMbPerSec,
        double speedup,
        HashVerificationResult singleHash,
        HashVerificationResult multiHash)
    {
        Console.WriteLine();
        Console.WriteLine($"Fetchy Benchmark Results ({sizeMb} MiB deterministic payload)");
        Console.WriteLine(new string('-', 76));
        Console.WriteLine($"{"Mode",-26} {"Time (s)",10} {"Throughput",14} {"Work-Steals",12} {"SHA-256",10}");
        Console.WriteLine(new string('-', 76));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{"1-Stream Baseline",-26} {singleResult.Elapsed.TotalSeconds,10:F3} {singleMbPerSec,10:F2} MB/s {singleResult.WorkStealCount,12} {(singleHash.IsMatch ? "VERIFIED" : "MISMATCH"),10}"));

        string multiLabel = $"{connections}-Segment Work-Stealing";
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{multiLabel,-26} {multiResult.Elapsed.TotalSeconds,10:F3} {multiMbPerSec,10:F2} MB/s {multiResult.WorkStealCount,12} {(multiHash.IsMatch ? "VERIFIED" : "MISMATCH"),10}"));
        Console.WriteLine(new string('-', 76));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Speedup:  {speedup:F2}x"));
        Console.WriteLine($"SHA-256:  {expectedSha256} (both runs bit-exact: {singleHash.IsMatch && multiHash.IsMatch})");

        return singleHash.IsMatch && multiHash.IsMatch ? 0 : 1;
    }
}
