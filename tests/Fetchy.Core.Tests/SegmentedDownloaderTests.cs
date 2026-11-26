using System.Diagnostics;
using Fetchy.Core.Engine;
using Fetchy.Core.Storage;
using Fetchy.Core.Verification;
using Xunit;

namespace Fetchy.Core.Tests;

public sealed class SegmentedDownloaderTests : IDisposable
{
    private readonly string _tempDir;

    public SegmentedDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fetchy-tests-{Guid.NewGuid():N}");
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
    [InlineData(8)]
    [InlineData(16)]
    public async Task MultiSegmentDownload_WithThrottledSegmentZero_TriggersDynamicWorkStealingAndMatchesSha256(int connections)
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(4 * 1024 * 1024, seed: (uint)(100 + connections));
        await using var server = new TestHttpServer(payload)
        {
            // Artificially throttle requests starting at byte 0 so fast workers finish and steal segment 0's tail.
            PerRequestBytesPerSecond = (_, start, _) => start == 0 ? 120_000 : 0
        };

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, $"steal-{connections}.bin");

        DownloadResult result = await downloader.DownloadAsync(
            server.GetUri("payload.bin"),
            outputPath,
            new DownloadOptions
            {
                MaxConnections = connections,
                MinSplitBytes = 32 * 1024,
                SmallFileThresholdBytes = 64 * 1024,
                Overwrite = true,
                ExpectedSha256 = server.PayloadSha256
            });

        Assert.True(result.UsedSegmentedDownload);
        Assert.Equal(payload.LongLength, result.BytesDownloaded);
        Assert.True(result.WorkStealCount > 0, $"Expected dynamic work-stealing to occur with {connections} connections.");
        Assert.NotNull(result.Sha256Verification);
        Assert.True(result.Sha256Verification!.IsMatch);
        Assert.Equal(payload.LongLength, new FileInfo(result.FilePath).Length);
        Assert.False(File.Exists(DirectDiskWriter.GetPartFilePath(result.FilePath)));
        Assert.False(File.Exists(DownloadStateStore.GetStateFilePath(result.FilePath)));
    }

    [Fact]
    public async Task SingleStreamFallback_WhenAcceptRangesAbsent_DownloadsBitExact()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(700 * 1024, seed: 201);
        await using var server = new TestHttpServer(payload)
        {
            SupportRanges = false,
            SendAcceptRangesHeader = false
        };

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, "no-ranges.bin");

        DownloadResult result = await downloader.DownloadAsync(
            server.GetUri("single.bin"),
            outputPath,
            new DownloadOptions
            {
                MaxConnections = 8,
                ExpectedSha256 = server.PayloadSha256
            });

        Assert.False(result.UsedSegmentedDownload);
        Assert.Equal(1, result.ConnectionsUsed);
        Assert.True(result.Sha256Verification!.IsMatch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleStreamFallback_WhenContentLengthUnknown_DownloadsBitExact(bool chunked)
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(384 * 1024, seed: chunked ? 202u : 203u);
        await using var server = new TestHttpServer(payload)
        {
            SupportRanges = false,
            SendAcceptRangesHeader = false,
            OmitContentLength = true,
            UseChunkedEncoding = chunked
        };

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, chunked ? "chunked.bin" : "close-delimited.bin");

        DownloadResult result = await downloader.DownloadAsync(
            server.GetUri("stream.bin"),
            outputPath,
            new DownloadOptions
            {
                MaxConnections = 8,
                ExpectedSha256 = server.PayloadSha256
            });

        Assert.False(result.UsedSegmentedDownload);
        Assert.Equal(payload.LongLength, result.BytesDownloaded);
        Assert.True(result.Sha256Verification!.IsMatch);
    }

    [Fact]
    public async Task ProbeFallback_WhenHeadReturns405_FallsBackToGetRangeZeroZeroAndDownloadsSegmented()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(1024 * 1024, seed: 204);
        await using var server = new TestHttpServer(payload)
        {
            RejectHeadWith405 = true
        };

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, "head405.bin");

        DownloadResult result = await downloader.DownloadAsync(
            server.GetUri("archive.bin"),
            outputPath,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024,
                ExpectedSha256 = server.PayloadSha256
            });

        Assert.True(result.UsedSegmentedDownload);
        Assert.True(result.Sha256Verification!.IsMatch);
        Assert.Contains(server.Requests, r => r.Method == "HEAD" && r.StatusCode == 405);
        Assert.Contains(server.Requests, r => r.Method == "GET" && r.RangeHeader == "bytes=0-0" && r.StatusCode == 206);
    }

    [Fact]
    public async Task PauseMidDownload_PersistsAtomicStateAndResumesBitExactWithoutRedownloadingCompletedBytes()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(2 * 1024 * 1024, seed: 301);
        await using var server = new TestHttpServer(payload)
        {
            PerRequestBytesPerSecond = (_, _, _) => 600_000
        };

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, "pausable.bin");
        string statePath = DownloadStateStore.GetStateFilePath(outputPath);

        using var pauseCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressSnapshot>(snap =>
        {
            if (snap.DownloadedBytes >= 512 * 1024)
            {
                pauseCts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await downloader.DownloadAsync(
                server.GetUri("pausable.bin"),
                outputPath,
                new DownloadOptions
                {
                    MaxConnections = 4,
                    MinSplitBytes = 64 * 1024,
                    ProgressReportInterval = TimeSpan.FromMilliseconds(25),
                    ExpectedSha256 = server.PayloadSha256
                },
                progress,
                pauseCts.Token);
        });

        Assert.True(File.Exists(statePath), "Expected atomic .fetchy.state file to be persisted on pause.");
        DownloadState savedState = await DownloadStateStore.LoadAsync(statePath);
        long bytesPersistedBeforeResume = savedState.Segments.Sum(s => Math.Max(0, s.Current - s.Start));
        Assert.True(bytesPersistedBeforeResume >= 256 * 1024, $"Expected substantial progress persisted, got {bytesPersistedBeforeResume}.");

        // Clear recorded requests and remove throttling for the resumed run.
        while (server.Requests.TryDequeue(out _)) { }
        server.PerRequestBytesPerSecond = null;

        DownloadResult resumed = await downloader.ResumeAsync(
            statePath,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024
            });

        Assert.False(resumed.WasResetOnResume);
        Assert.True(resumed.Sha256Verification!.IsMatch);
        Assert.False(File.Exists(statePath));

        long bytesTransferredOnResume = server.Requests.Where(r => r.Method == "GET").Sum(r => r.BytesSent);
        Assert.True(
            bytesTransferredOnResume < payload.LongLength,
            $"Expected resumed download to skip already-downloaded bytes ({bytesPersistedBeforeResume}), but transferred {bytesTransferredOnResume} of {payload.LongLength}.");
    }

    [Fact]
    public async Task RefreshUrl_ResumesFromNewEndpointWithoutRedownloadingAndMatchesSha256()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(2 * 1024 * 1024, seed: 302);
        await using var server = new TestHttpServer(payload)
        {
            PerRequestBytesPerSecond = (_, _, _) => 600_000
        };

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, "refreshable.bin");
        string statePath = DownloadStateStore.GetStateFilePath(outputPath);

        using var pauseCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressSnapshot>(snap =>
        {
            if (snap.DownloadedBytes >= 512 * 1024)
            {
                pauseCts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await downloader.DownloadAsync(
                server.GetUri("expired/token1/file.bin"),
                outputPath,
                new DownloadOptions
                {
                    MaxConnections = 4,
                    MinSplitBytes = 64 * 1024,
                    ProgressReportInterval = TimeSpan.FromMilliseconds(25),
                    ExpectedSha256 = server.PayloadSha256
                },
                progress,
                pauseCts.Token);
        });

        while (server.Requests.TryDequeue(out _)) { }
        server.PerRequestBytesPerSecond = null;

        Uri refreshedUri = server.GetUri("fresh/token2/file.bin");
        await SegmentedDownloader.RefreshUrlAsync(statePath, refreshedUri);

        DownloadResult resumed = await downloader.ResumeAsync(
            statePath,
            new DownloadOptions { MaxConnections = 4, MinSplitBytes = 64 * 1024 });

        Assert.False(resumed.WasResetOnResume);
        Assert.Equal(refreshedUri, resumed.FinalUri);
        Assert.True(resumed.Sha256Verification!.IsMatch);
        Assert.All(server.Requests, r => Assert.Equal("/fresh/token2/file.bin", r.Path));

        long resumedBytes = server.Requests.Where(r => r.Method == "GET").Sum(r => r.BytesSent);
        Assert.True(resumedBytes < payload.LongLength);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resume_WhenRemoteResourceChanged_SafelyRestartsFromZeroWithoutCorruption(bool changeLength)
    {
        byte[] oldPayload = TestHttpServer.CreateDeterministicPayload(1536 * 1024, seed: 401);
        byte[] newPayload = TestHttpServer.CreateDeterministicPayload(
            changeLength ? 1280 * 1024 : 1536 * 1024,
            seed: 402);

        await using var server = new TestHttpServer(oldPayload, etag: "\"v1\"")
        {
            PerRequestBytesPerSecond = (_, _, _) => 500_000
        };

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, changeLength ? "changed-len.bin" : "changed-etag.bin");
        string statePath = DownloadStateStore.GetStateFilePath(outputPath);

        using var pauseCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgressSnapshot>(snap =>
        {
            if (snap.DownloadedBytes >= 384 * 1024)
            {
                pauseCts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await downloader.DownloadAsync(
                server.GetUri("changing.bin"),
                outputPath,
                new DownloadOptions
                {
                    MaxConnections = 4,
                    MinSplitBytes = 64 * 1024,
                    ProgressReportInterval = TimeSpan.FromMilliseconds(25)
                },
                progress,
                pauseCts.Token);
        });

        // Swap remote file on server before resuming.
        server.PerRequestBytesPerSecond = null;
        server.UpdatePayload(newPayload, newEtag: changeLength ? "\"v1\"" : "\"v2\"");

        DownloadResult resumed = await downloader.ResumeAsync(
            statePath,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024,
                ExpectedSha256 = TestHttpServer.ComputeSha256(newPayload)
            });

        Assert.True(resumed.WasResetOnResume);
        Assert.Equal(newPayload.LongLength, resumed.BytesDownloaded);
        Assert.True(resumed.Sha256Verification!.IsMatch);
    }

    [Fact]
    public async Task Transient429_503_AndMidStreamTcpDrops_TriggerAdaptiveStepDownAndRecoverBitExact()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(2 * 1024 * 1024, seed: 501);
        await using var server = new TestHttpServer(payload);
        server.InjectFaults(count429: 2, count503: 2, midStreamTcpDrops: 2, retryAfter: "0");

        using var downloader = new SegmentedDownloader();
        string outputPath = Path.Combine(_tempDir, "resilient.bin");

        var fastRetryPolicy = new RetryPolicy
        {
            MaxRetriesPerSegment = 10,
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(200),
            StepDownCooldown = TimeSpan.FromMilliseconds(10)
        };

        DownloadResult result = await downloader.DownloadAsync(
            server.GetUri("flaky.bin"),
            outputPath,
            new DownloadOptions
            {
                MaxConnections = 8,
                MinSplitBytes = 64 * 1024,
                RetryPolicy = fastRetryPolicy,
                ExpectedSha256 = server.PayloadSha256
            });

        Assert.True(result.AdaptiveStepDownCount > 0, "Expected adaptive concurrency step-down under 429/503/TCP drops.");
        Assert.True(result.Sha256Verification!.IsMatch);
        Assert.Contains(server.Requests, r => r.StatusCode == 429);
        Assert.Contains(server.Requests, r => r.StatusCode == 503);
    }

    [Fact]
    public async Task BandwidthLimiter_EnforcesTargetRateAndSupportsDynamicRuntimeAdjustment()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(1024 * 1024, seed: 601);
        await using var server = new TestHttpServer(payload);
        using var downloader = new SegmentedDownloader();

        // 1. Steady rate accuracy test: 1 MiB at 700 KiB/s (~1.46 s)
        const long targetRate = 700 * 1024;
        string steadyPath = Path.Combine(_tempDir, "throttled.bin");
        DownloadResult steadyResult = await downloader.DownloadAsync(
            server.GetUri("rate.bin"),
            steadyPath,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024,
                MaxBytesPerSecond = targetRate,
                ExpectedSha256 = server.PayloadSha256
            });

        Assert.True(steadyResult.Sha256Verification!.IsMatch);
        double actualRate = payload.LongLength / steadyResult.Elapsed.TotalSeconds;
        Assert.InRange(actualRate, targetRate * 0.55, targetRate * 1.35);

        // 2. Dynamic runtime SetLimit adjustment: start slow at 180 KiB/s, then lift limit to unlimited mid-download.
        using var dynamicLimiter = new BandwidthLimiter(180 * 1024);
        string dynamicPath = Path.Combine(_tempDir, "dynamic-rate.bin");
        bool boosted = false;
        long boostTimestamp = 0;
        long bytesAtBoost = 0;

        var progress = new Progress<DownloadProgressSnapshot>(snap =>
        {
            if (!boosted && snap.DownloadedBytes >= 256 * 1024)
            {
                boosted = true;
                bytesAtBoost = snap.DownloadedBytes;
                boostTimestamp = Stopwatch.GetTimestamp();
                dynamicLimiter.SetLimit(0); // Lift limit to unlimited dynamically!
            }
        });

        DownloadResult dynamicResult = await downloader.DownloadAsync(
            server.GetUri("rate.bin"),
            dynamicPath,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024,
                SharedBandwidthLimiter = dynamicLimiter,
                ProgressReportInterval = TimeSpan.FromMilliseconds(30),
                ExpectedSha256 = server.PayloadSha256
            },
            progress);

        Assert.True(dynamicResult.Sha256Verification!.IsMatch);
        Assert.True(boosted, "Expected progress callback to trigger dynamic SetLimit.");
        double postBoostSeconds = (Stopwatch.GetTimestamp() - boostTimestamp) / (double)Stopwatch.Frequency;
        long remainingAfterBoost = payload.LongLength - bytesAtBoost;
        double postBoostRate = remainingAfterBoost / Math.Max(0.001, postBoostSeconds);
        Assert.True(postBoostRate > 500_000, $"Expected fast post-boost throughput after SetLimit(0), got {postBoostRate:F0} B/s.");
    }

    [Fact]
    public async Task DestinationAutoDisambiguation_CreatesNumberedCopyUnlessOverwriteEnabled()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(128 * 1024, seed: 701);
        await using var server = new TestHttpServer(payload);
        using var downloader = new SegmentedDownloader();

        string desiredPath = Path.Combine(_tempDir, "archive.zip");
        await File.WriteAllTextAsync(desiredPath, "existing");

        DownloadResult first = await downloader.DownloadAsync(
            server.GetUri("archive.zip"),
            desiredPath,
            new DownloadOptions { Overwrite = false, ExpectedSha256 = server.PayloadSha256 });

        Assert.Equal(Path.Combine(_tempDir, "archive (1).zip"), first.FilePath);
        Assert.Equal("existing", await File.ReadAllTextAsync(desiredPath));

        DownloadResult overwritten = await downloader.DownloadAsync(
            server.GetUri("archive.zip"),
            desiredPath,
            new DownloadOptions { Overwrite = true, ExpectedSha256 = server.PayloadSha256 });

        Assert.Equal(desiredPath, overwritten.FilePath);
        Assert.True(overwritten.Sha256Verification!.IsMatch);
    }

    [Fact]
    public void RetryPolicy_HonorsRetryAfterDeltaAndHttpDateWithCap()
    {
        var policy = new RetryPolicy
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromSeconds(5)
        };

        using var deltaResp = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        deltaResp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.ComputeDelay(1, deltaResp));

        using var cappedDeltaResp = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        cappedDeltaResp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.ComputeDelay(1, cappedDeltaResp));

        using var dateResp = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        dateResp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(3));
        TimeSpan computedFromDate = policy.ComputeDelay(1, dateResp);
        Assert.InRange(computedFromDate.TotalSeconds, 1.5, 3.5);
    }
}
