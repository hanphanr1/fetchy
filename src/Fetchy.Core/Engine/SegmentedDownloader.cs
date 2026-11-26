using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Fetchy.Core.Http;
using Fetchy.Core.Management;
using Fetchy.Core.Storage;
using Fetchy.Core.Verification;

namespace Fetchy.Core.Engine;

/// <summary>
/// High-performance segmented HTTP downloader implementing dynamic work-stealing,
/// zero-merge direct random-access disk I/O, crash-safe resume, and adaptive backoff.
/// </summary>
public sealed class SegmentedDownloader : IDisposable
{
    private const int ReadBufferSize = 128 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly HttpProbeClient _probeClient;
    private bool _disposed;

    public SegmentedDownloader(HttpClient? httpClient = null, bool ownsHttpClient = false)
    {
        if (httpClient is null)
        {
            _httpClient = HttpClientFactory.CreateClient();
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = ownsHttpClient;
        }

        _probeClient = new HttpProbeClient(_httpClient);
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> to <paramref name="outputPathOrDirectory"/>.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        Uri uri,
        string outputPathOrDirectory,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri validatedUri = UriValidator.ValidateHttpUri(uri);
        if (string.IsNullOrWhiteSpace(outputPathOrDirectory))
        {
            throw new ArgumentException("Output path or directory must not be empty.", nameof(outputPathOrDirectory));
        }

        options ??= new DownloadOptions();
        var stopwatch = Stopwatch.StartNew();

        ProbeResult probe = await _probeClient.ProbeAsync(validatedUri, options.Headers, cancellationToken).ConfigureAwait(false);
        string destinationPath = ResolveDestinationPath(outputPathOrDirectory, probe.SuggestedFileName);
        DownloadCategory category = CategoryClassifier.Classify(destinationPath);

        string partFilePath = DirectDiskWriter.GetPartFilePath(destinationPath);
        string stateFilePath = DownloadStateStore.GetStateFilePath(destinationPath);

        var state = new DownloadState
        {
            Url = probe.FinalUri.AbsoluteUri,
            DestinationPath = destinationPath,
            TotalBytes = probe.TotalBytes,
            SupportsRanges = probe.SupportsRanges,
            ETag = probe.ETag,
            LastModified = probe.LastModified,
            ExpectedSha256 = options.ExpectedSha256,
            ExpectedMd5 = options.ExpectedMd5,
            Category = category,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Headers = UriValidator.FilterPersistableHeaders(options.Headers)
        };

        return await ExecuteDownloadAsync(
            probe: probe,
            state: state,
            partFilePath: partFilePath,
            stateFilePath: stateFilePath,
            options: options,
            requestHeaders: options.Headers,
            isResume: false,
            wasResetOnResume: false,
            stopwatch: stopwatch,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the download URL inside an existing <c>.fetchy.state</c> checkpoint (e.g. for expired signed URLs)
    /// while preserving all completed and partial segment progress.
    /// </summary>
    public static async Task<DownloadState> RefreshUrlAsync(
        string stateFilePath,
        Uri newUri,
        CancellationToken cancellationToken = default)
    {
        Uri validated = UriValidator.ValidateHttpUri(newUri);
        DownloadState state = await DownloadStateStore.LoadAsync(stateFilePath, cancellationToken).ConfigureAwait(false);
        state.Url = validated.AbsoluteUri;
        await DownloadStateStore.SaveAsync(stateFilePath, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Resumes an interrupted or paused download from its <c>.fetchy.state</c> checkpoint,
    /// validating remote metadata and safely restarting from byte 0 if the remote resource changed.
    /// </summary>
    public async Task<DownloadResult> ResumeAsync(
        string stateFilePath,
        DownloadOptions? options = null,
        Uri? refreshedUri = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(stateFilePath))
        {
            throw new ArgumentException("State file path must not be empty.", nameof(stateFilePath));
        }

        options ??= new DownloadOptions();
        var stopwatch = Stopwatch.StartNew();

        DownloadState state = await DownloadStateStore.LoadAsync(stateFilePath, cancellationToken).ConfigureAwait(false);

        Uri targetUri = refreshedUri is not null
            ? UriValidator.ValidateHttpUri(refreshedUri)
            : UriValidator.ValidateHttpUri(state.Url);

        state.Url = targetUri.AbsoluteUri;

        // Merge persisted safe headers with any runtime headers supplied in options (runtime overrides persisted).
        var effectiveHeaders = new Dictionary<string, string>(state.Headers, StringComparer.OrdinalIgnoreCase);
        if (options.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
            {
                effectiveHeaders[key] = value;
            }
        }

        // Preserve expected hashes from state if not overridden in options.
        var effectiveOptions = new DownloadOptions
        {
            MaxConnections = options.MaxConnections,
            MinSplitBytes = options.MinSplitBytes,
            SmallFileThresholdBytes = options.SmallFileThresholdBytes,
            MaxBytesPerSecond = options.MaxBytesPerSecond,
            SharedBandwidthLimiter = options.SharedBandwidthLimiter,
            Overwrite = options.Overwrite,
            ExpectedSha256 = options.ExpectedSha256 ?? state.ExpectedSha256,
            ExpectedMd5 = options.ExpectedMd5 ?? state.ExpectedMd5,
            Headers = effectiveHeaders,
            RetryPolicy = options.RetryPolicy,
            ProgressReportInterval = options.ProgressReportInterval,
            StateCheckpointInterval = options.StateCheckpointInterval
        };

        ProbeResult probe = await _probeClient.ProbeAsync(targetUri, effectiveHeaders, cancellationToken).ConfigureAwait(false);

        string destinationPath = state.DestinationPath;
        string partFilePath = DirectDiskWriter.GetPartFilePath(destinationPath);

        bool canResume = CanSafelyResume(state, probe, partFilePath);
        bool wasReset = !canResume;

        if (wasReset)
        {
            state.TotalBytes = probe.TotalBytes;
            state.SupportsRanges = probe.SupportsRanges;
            state.ETag = probe.ETag;
            state.LastModified = probe.LastModified;
            state.Segments = [];
        }
        else
        {
            state.ETag ??= probe.ETag;
            state.LastModified ??= probe.LastModified;
        }

        state.Url = probe.FinalUri.AbsoluteUri;
        state.Headers = UriValidator.FilterPersistableHeaders(effectiveHeaders);
        state.ExpectedSha256 = effectiveOptions.ExpectedSha256;
        state.ExpectedMd5 = effectiveOptions.ExpectedMd5;

        return await ExecuteDownloadAsync(
            probe: probe,
            state: state,
            partFilePath: partFilePath,
            stateFilePath: stateFilePath,
            options: effectiveOptions,
            requestHeaders: effectiveHeaders,
            isResume: canResume,
            wasResetOnResume: wasReset,
            stopwatch: stopwatch,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadResult> ExecuteDownloadAsync(
        ProbeResult probe,
        DownloadState state,
        string partFilePath,
        string stateFilePath,
        DownloadOptions options,
        IReadOnlyDictionary<string, string>? requestHeaders,
        bool isResume,
        bool wasResetOnResume,
        Stopwatch stopwatch,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        BandwidthLimiter? ownedLimiter = options.SharedBandwidthLimiter is null
            ? new BandwidthLimiter(options.MaxBytesPerSecond)
            : null;
        BandwidthLimiter limiter = options.SharedBandwidthLimiter ?? ownedLimiter!;

        try
        {
            // Special case: empty 0-byte file.
            if (probe.TotalBytes == 0)
            {
                using var emptyWriter = DirectDiskWriter.Open(partFilePath, 0, resetExisting: true);
                string emptyFinalPath = emptyWriter.FinalizeToDestination(state.DestinationPath, options.Overwrite, stateFilePath);
                var (sha256, md5) = await VerifyHashesAsync(emptyFinalPath, options, cancellationToken).ConfigureAwait(false);
                progress?.Report(new DownloadProgressSnapshot(0, 0, 0, 0, TimeSpan.Zero, 0, 0, 0, []));
                return new DownloadResult(
                    probe.FinalUri,
                    emptyFinalPath,
                    0,
                    0,
                    stopwatch.Elapsed,
                    0,
                    1,
                    0,
                    0,
                    UsedSegmentedDownload: false,
                    WasResetOnResume: wasResetOnResume,
                    Category: state.Category,
                    Sha256Verification: sha256,
                    Md5Verification: md5);
            }

            bool useSegmented = probe.SupportsRanges && probe.TotalBytes is > 0;
            if (!useSegmented)
            {
                return await ExecuteSingleStreamAsync(
                    probe,
                    state,
                    partFilePath,
                    stateFilePath,
                    options,
                    requestHeaders,
                    limiter,
                    wasResetOnResume,
                    stopwatch,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteSegmentedAsync(
                probe,
                state,
                partFilePath,
                stateFilePath,
                options,
                requestHeaders,
                limiter,
                isResume,
                wasResetOnResume,
                stopwatch,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ownedLimiter?.Dispose();
        }
    }

    private async Task<DownloadResult> ExecuteSegmentedAsync(
        ProbeResult probe,
        DownloadState state,
        string partFilePath,
        string stateFilePath,
        DownloadOptions options,
        IReadOnlyDictionary<string, string>? requestHeaders,
        BandwidthLimiter limiter,
        bool isResume,
        bool wasResetOnResume,
        Stopwatch stopwatch,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        long totalBytes = probe.TotalBytes!.Value;
        int initialConnections = totalBytes < options.SmallFileThresholdBytes
            ? 1
            : Math.Clamp(options.MaxConnections, 1, 32);

        SegmentScheduler scheduler = isResume && state.Segments.Count > 0
            ? SegmentScheduler.FromPersisted(totalBytes, state.Segments, options.MinSplitBytes)
            : new SegmentScheduler(totalBytes, initialConnections, options.MinSplitBytes, options.SmallFileThresholdBytes);

        // If resuming a small file or partial state, honor up to MaxConnections workers.
        int workerCount = totalBytes < options.SmallFileThresholdBytes
            ? 1
            : Math.Clamp(options.MaxConnections, 1, 32);

        using var diskWriter = DirectDiskWriter.Open(partFilePath, totalBytes, resetExisting: !isResume);
        using var stateWriteGate = new SemaphoreSlim(1, 1);

        // Save initial state immediately so crash/pause always has a valid checkpoint.
        await SaveStateSnapshotAsync(stateFilePath, state, scheduler, stateWriteGate, CancellationToken.None).ConfigureAwait(false);

        int activeConcurrencyLimit = workerCount;
        int adaptiveStepDownCount = 0;
        long lastStepDownTimestamp = 0;
        object throttleLock = new();

        void TriggerStepDown()
        {
            long now = Stopwatch.GetTimestamp();
            lock (throttleLock)
            {
                if (activeConcurrencyLimit <= 1)
                {
                    return;
                }

                double elapsedMs = (now - lastStepDownTimestamp) * 1000.0 / Stopwatch.Frequency;
                if (lastStepDownTimestamp != 0 && elapsedMs < options.RetryPolicy.StepDownCooldown.TotalMilliseconds)
                {
                    return;
                }

                lastStepDownTimestamp = now;
                activeConcurrencyLimit = Math.Max(1, activeConcurrencyLimit / 2);
                adaptiveStepDownCount++;
            }
        }

        int GetConcurrencyLimit() => Volatile.Read(ref activeConcurrencyLimit);
        int GetStepDownCount() => Volatile.Read(ref adaptiveStepDownCount);

        using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(failureCts.Token);
        Exception? fatalException = null;

        long initialDownloaded = scheduler.TotalDownloadedBytes;

        Task monitorTask = RunProgressAndCheckpointLoopAsync(
            totalBytes,
            scheduler,
            state,
            stateFilePath,
            stateWriteGate,
            GetStepDownCount,
            options.ProgressReportInterval,
            options.StateCheckpointInterval,
            progress,
            monitorCts.Token);

        var workerTasks = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            workerTasks[w] = Task.Run(async () =>
            {
                await RunSegmentWorkerAsync(
                    probe.FinalUri,
                    scheduler,
                    diskWriter,
                    limiter,
                    options,
                    requestHeaders,
                    GetConcurrencyLimit,
                    TriggerStepDown,
                    ex =>
                    {
                        Interlocked.CompareExchange(ref fatalException, ex, null);
                        try
                        {
                            failureCts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    },
                    failureCts.Token).ConfigureAwait(false);
            }, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                monitorCts.Cancel();
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            diskWriter.FlushToDisk();
            await SaveStateSnapshotAsync(stateFilePath, state, scheduler, stateWriteGate, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (fatalException is not null)
        {
            diskWriter.FlushToDisk();
            await SaveStateSnapshotAsync(stateFilePath, state, scheduler, stateWriteGate, CancellationToken.None).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatalException).Throw();
        }

        if (!scheduler.IsAllCompleted)
        {
            diskWriter.FlushToDisk();
            await SaveStateSnapshotAsync(stateFilePath, state, scheduler, stateWriteGate, CancellationToken.None).ConfigureAwait(false);
            throw new IOException("Download ended before all segments completed.");
        }

        string finalPath = diskWriter.FinalizeToDestination(state.DestinationPath, options.Overwrite, stateFilePath);
        stopwatch.Stop();

        var (sha256, md5) = await VerifyHashesAsync(finalPath, options, cancellationToken).ConfigureAwait(false);

        long sessionDownloaded = Math.Max(0, totalBytes - initialDownloaded);
        double avgSpeed = stopwatch.Elapsed.TotalSeconds > 0
            ? sessionDownloaded / stopwatch.Elapsed.TotalSeconds
            : 0;

        progress?.Report(new DownloadProgressSnapshot(
            TotalBytes: totalBytes,
            DownloadedBytes: totalBytes,
            InstantSpeedBps: avgSpeed,
            SmoothedSpeedBps: avgSpeed,
            Eta: TimeSpan.Zero,
            ActiveConnections: 0,
            WorkStealCount: scheduler.WorkStealCount,
            AdaptiveStepDownCount: GetStepDownCount(),
            Segments: scheduler.GetSegmentSnapshots()));

        return new DownloadResult(
            FinalUri: probe.FinalUri,
            FilePath: finalPath,
            BytesDownloaded: totalBytes,
            TotalBytes: totalBytes,
            Elapsed: stopwatch.Elapsed,
            AverageSpeedBps: avgSpeed,
            ConnectionsUsed: workerCount,
            WorkStealCount: scheduler.WorkStealCount,
            AdaptiveStepDownCount: GetStepDownCount(),
            UsedSegmentedDownload: true,
            WasResetOnResume: wasResetOnResume,
            Category: state.Category,
            Sha256Verification: sha256,
            Md5Verification: md5);
    }

    private async Task RunSegmentWorkerAsync(
        Uri uri,
        SegmentScheduler scheduler,
        DirectDiskWriter diskWriter,
        BandwidthLimiter limiter,
        DownloadOptions options,
        IReadOnlyDictionary<string, string>? requestHeaders,
        Func<int> getConcurrencyLimit,
        Action triggerStepDown,
        Action<Exception> onFatalError,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!scheduler.TryAcquireWork(getConcurrencyLimit(), out int segmentId, out TimeSpan? waitDelay))
                {
                    if (scheduler.IsAllCompleted || !scheduler.HasUnfinishedWork)
                    {
                        break;
                    }

                    if (waitDelay.HasValue)
                    {
                        TimeSpan sleep = waitDelay.Value > TimeSpan.FromMilliseconds(250)
                            ? TimeSpan.FromMilliseconds(250)
                            : waitDelay.Value;
                        if (sleep > TimeSpan.Zero)
                        {
                            await Task.Delay(sleep, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    // If we have other active workers running (e.g., remaining tail < 2*MinSplitBytes or concurrency stepped down),
                    // retire this worker cleanly unless no workers are currently active.
                    if (scheduler.ActiveSegmentCount > 0)
                    {
                        break;
                    }

                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessSegmentAsync(
                    segmentId,
                    uri,
                    scheduler,
                    diskWriter,
                    limiter,
                    buffer,
                    options,
                    requestHeaders,
                    triggerStepDown,
                    onFatalError,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Clean shutdown on pause or peer fatal error.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ProcessSegmentAsync(
        int segmentId,
        Uri uri,
        SegmentScheduler scheduler,
        DirectDiskWriter diskWriter,
        BandwidthLimiter limiter,
        byte[] buffer,
        DownloadOptions options,
        IReadOnlyDictionary<string, string>? requestHeaders,
        Action triggerStepDown,
        Action<Exception> onFatalError,
        CancellationToken cancellationToken)
    {
        if (!scheduler.TryGetCurrentBounds(segmentId, out long rangeStart, out long rangeEnd))
        {
            return;
        }

        RetryPolicy retryPolicy = options.RetryPolicy;

        try
        {
            using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, uri, requestHeaders);
            request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (retryPolicy.IsTransientStatusCode(response.StatusCode))
            {
                if (retryPolicy.ShouldStepDownConcurrency(response.StatusCode))
                {
                    triggerStepDown();
                }

                int attempt = scheduler.GetRetryCount(segmentId) + 1;
                if (attempt > retryPolicy.MaxRetriesPerSegment)
                {
                    scheduler.MarkFailed(segmentId);
                    onFatalError(new HttpRequestException(
                        $"Segment {segmentId} failed after {retryPolicy.MaxRetriesPerSegment} retries (HTTP {(int)response.StatusCode}).",
                        null,
                        response.StatusCode));
                    return;
                }

                TimeSpan delay = retryPolicy.ComputeDelay(attempt, response);
                scheduler.RequeueForRetry(segmentId, delay);
                return;
            }

            if (response.StatusCode != HttpStatusCode.PartialContent &&
                !(response.StatusCode == HttpStatusCode.OK && rangeStart == 0))
            {
                scheduler.MarkFailed(segmentId);
                onFatalError(new HttpRequestException(
                    $"Unexpected HTTP status {(int)response.StatusCode} ({response.StatusCode}) for byte range {rangeStart}-{rangeEnd}.",
                    null,
                    response.StatusCode));
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            long speedWindowStart = Stopwatch.GetTimestamp();
            long speedWindowBytes = 0;
            double segmentSpeedBps = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!scheduler.TryGetCurrentBounds(segmentId, out long currentOffset, out long currentEnd))
                {
                    break;
                }

                long remaining = currentEnd - currentOffset + 1;
                int chunkLimit = limiter.GetRecommendedChunkSize(ReadBufferSize);
                int toRead = (int)Math.Min(remaining, chunkLimit);

                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    if (scheduler.TryGetCurrentBounds(segmentId, out long afterOffset, out long afterEnd) &&
                        afterOffset <= afterEnd)
                    {
                        throw new IOException(
                            $"Premature end of stream on segment {segmentId} at offset {afterOffset} (expected through {afterEnd}).");
                    }

                    break;
                }

                await limiter.ThrottleAsync(bytesRead, cancellationToken).ConfigureAwait(false);

                if (!scheduler.TryReserveWrite(segmentId, bytesRead, out long writeOffset, out int bytesToWrite))
                {
                    // Segment end was shortened by a work-stealer while reading; stop cleanly without writing past End.
                    break;
                }

                try
                {
                    await diskWriter.WriteAsync(buffer.AsMemory(0, bytesToWrite), writeOffset, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    scheduler.RollbackReservation(segmentId);
                    throw;
                }

                speedWindowBytes += bytesToWrite;
                long now = Stopwatch.GetTimestamp();
                double windowSec = (now - speedWindowStart) / (double)Stopwatch.Frequency;
                if (windowSec >= 0.15)
                {
                    segmentSpeedBps = speedWindowBytes / windowSec;
                    speedWindowBytes = 0;
                    speedWindowStart = now;
                }

                bool completed = scheduler.CommitWrite(segmentId, writeOffset, bytesToWrite, segmentSpeedBps);
                if (completed || bytesToWrite < bytesRead)
                {
                    // Reached exact End (or shortened End after work-stealing); close this range stream cleanly.
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            scheduler.MarkPaused(segmentId);
            throw;
        }
        catch (Exception ex) when (retryPolicy.IsTransientException(ex))
        {
            triggerStepDown();

            int attempt = scheduler.GetRetryCount(segmentId) + 1;
            if (attempt > retryPolicy.MaxRetriesPerSegment)
            {
                scheduler.MarkFailed(segmentId);
                onFatalError(ex);
                return;
            }

            TimeSpan delay = retryPolicy.ComputeDelay(attempt);
            scheduler.RequeueForRetry(segmentId, delay);
        }
        catch (Exception ex)
        {
            scheduler.MarkFailed(segmentId);
            onFatalError(ex);
        }
    }

    private async Task<DownloadResult> ExecuteSingleStreamAsync(
        ProbeResult probe,
        DownloadState state,
        string partFilePath,
        string stateFilePath,
        DownloadOptions options,
        IReadOnlyDictionary<string, string>? requestHeaders,
        BandwidthLimiter limiter,
        bool wasResetOnResume,
        Stopwatch stopwatch,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        RetryPolicy retryPolicy = options.RetryPolicy;
        int attempt = 0;
        long totalWritten = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;
                totalWritten = 0;

                try
                {
                    using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, probe.FinalUri, requestHeaders);
                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);

                    if (retryPolicy.IsTransientStatusCode(response.StatusCode))
                    {
                        if (attempt > retryPolicy.MaxRetriesPerSegment)
                        {
                            response.EnsureSuccessStatusCode();
                        }

                        TimeSpan delay = retryPolicy.ComputeDelay(attempt, response);
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    response.EnsureSuccessStatusCode();

                    long? knownTotal = response.Content.Headers.ContentLength ?? probe.TotalBytes;
                    using var diskWriter = DirectDiskWriter.Open(partFilePath, knownTotal, resetExisting: true);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                    long lastReportTs = Stopwatch.GetTimestamp();
                    long lastReportBytes = 0;
                    double smoothedSpeed = 0;

                    while (true)
                    {
                        int chunkLimit = limiter.GetRecommendedChunkSize(ReadBufferSize);
                        int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, chunkLimit), cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        await limiter.ThrottleAsync(bytesRead, cancellationToken).ConfigureAwait(false);
                        await diskWriter.WriteAsync(buffer.AsMemory(0, bytesRead), totalWritten, cancellationToken).ConfigureAwait(false);
                        totalWritten += bytesRead;

                        long now = Stopwatch.GetTimestamp();
                        double elapsedSec = (now - lastReportTs) / (double)Stopwatch.Frequency;
                        if (progress is not null && elapsedSec >= options.ProgressReportInterval.TotalSeconds)
                        {
                            double instant = (totalWritten - lastReportBytes) / elapsedSec;
                            smoothedSpeed = smoothedSpeed <= 0 ? instant : 0.3 * instant + 0.7 * smoothedSpeed;
                            TimeSpan? eta = knownTotal.HasValue && smoothedSpeed > 0
                                ? TimeSpan.FromSeconds(Math.Max(0, knownTotal.Value - totalWritten) / smoothedSpeed)
                                : null;

                            progress.Report(new DownloadProgressSnapshot(
                                TotalBytes: knownTotal,
                                DownloadedBytes: totalWritten,
                                InstantSpeedBps: instant,
                                SmoothedSpeedBps: smoothedSpeed,
                                Eta: eta,
                                ActiveConnections: 1,
                                WorkStealCount: 0,
                                AdaptiveStepDownCount: 0,
                                Segments: [new SegmentProgress(0, 0, totalWritten, knownTotal.HasValue ? knownTotal.Value - 1 : totalWritten - 1, SegmentState.Active, smoothedSpeed)]));

                            lastReportTs = now;
                            lastReportBytes = totalWritten;
                        }
                    }

                    if (knownTotal.HasValue && totalWritten < knownTotal.Value)
                    {
                        throw new IOException($"Premature end of stream: received {totalWritten} of {knownTotal.Value} bytes.");
                    }

                    diskWriter.SetLength(totalWritten);
                    string finalPath = diskWriter.FinalizeToDestination(state.DestinationPath, options.Overwrite, stateFilePath);
                    stopwatch.Stop();

                    var (sha256, md5) = await VerifyHashesAsync(finalPath, options, cancellationToken).ConfigureAwait(false);

                    double avgSpeed = stopwatch.Elapsed.TotalSeconds > 0
                        ? totalWritten / stopwatch.Elapsed.TotalSeconds
                        : 0;

                    progress?.Report(new DownloadProgressSnapshot(
                        TotalBytes: totalWritten,
                        DownloadedBytes: totalWritten,
                        InstantSpeedBps: avgSpeed,
                        SmoothedSpeedBps: avgSpeed,
                        Eta: TimeSpan.Zero,
                        ActiveConnections: 0,
                        WorkStealCount: 0,
                        AdaptiveStepDownCount: 0,
                        Segments: [new SegmentProgress(0, 0, totalWritten, Math.Max(0, totalWritten - 1), SegmentState.Completed, 0)]));

                    return new DownloadResult(
                        FinalUri: probe.FinalUri,
                        FilePath: finalPath,
                        BytesDownloaded: totalWritten,
                        TotalBytes: totalWritten,
                        Elapsed: stopwatch.Elapsed,
                        AverageSpeedBps: avgSpeed,
                        ConnectionsUsed: 1,
                        WorkStealCount: 0,
                        AdaptiveStepDownCount: 0,
                        UsedSegmentedDownload: false,
                        WasResetOnResume: wasResetOnResume,
                        Category: state.Category,
                        Sha256Verification: sha256,
                        Md5Verification: md5);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                           retryPolicy.IsTransientException(ex) &&
                                           attempt <= retryPolicy.MaxRetriesPerSegment)
                {
                    TimeSpan delay = retryPolicy.ComputeDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task RunProgressAndCheckpointLoopAsync(
        long totalBytes,
        SegmentScheduler scheduler,
        DownloadState state,
        string stateFilePath,
        SemaphoreSlim stateWriteGate,
        Func<int> getStepDownCount,
        TimeSpan progressInterval,
        TimeSpan checkpointInterval,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        TimeSpan tick = progressInterval > TimeSpan.FromMilliseconds(25)
            ? progressInterval
            : TimeSpan.FromMilliseconds(25);

        long lastTs = Stopwatch.GetTimestamp();
        long lastBytes = scheduler.TotalDownloadedBytes;
        long lastCheckpointTs = lastTs;
        double smoothedSpeed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(tick, cancellationToken).ConfigureAwait(false);

            long now = Stopwatch.GetTimestamp();
            long currentBytes = scheduler.TotalDownloadedBytes;
            double dt = (now - lastTs) / (double)Stopwatch.Frequency;

            if (dt > 0 && progress is not null)
            {
                double instantSpeed = Math.Max(0, currentBytes - lastBytes) / dt;
                smoothedSpeed = smoothedSpeed <= 0
                    ? instantSpeed
                    : 0.3 * instantSpeed + 0.7 * smoothedSpeed;

                TimeSpan? eta = smoothedSpeed > 1.0
                    ? TimeSpan.FromSeconds(Math.Max(0, totalBytes - currentBytes) / smoothedSpeed)
                    : null;

                progress.Report(new DownloadProgressSnapshot(
                    TotalBytes: totalBytes,
                    DownloadedBytes: currentBytes,
                    InstantSpeedBps: instantSpeed,
                    SmoothedSpeedBps: smoothedSpeed,
                    Eta: eta,
                    ActiveConnections: scheduler.ActiveSegmentCount,
                    WorkStealCount: scheduler.WorkStealCount,
                    AdaptiveStepDownCount: getStepDownCount(),
                    Segments: scheduler.GetSegmentSnapshots()));
            }

            lastTs = now;
            lastBytes = currentBytes;

            double sinceCheckpoint = (now - lastCheckpointTs) / (double)Stopwatch.Frequency;
            if (sinceCheckpoint >= checkpointInterval.TotalSeconds)
            {
                lastCheckpointTs = now;
                try
                {
                    await SaveStateSnapshotAsync(stateFilePath, state, scheduler, stateWriteGate, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // Periodic checkpoint is best-effort; final save on pause/error is authoritative.
                }
            }
        }
    }

    private static async Task SaveStateSnapshotAsync(
        string stateFilePath,
        DownloadState state,
        SegmentScheduler scheduler,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.Segments = scheduler.ExportPersistedSegments();
            await DownloadStateStore.SaveAsync(stateFilePath, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(HashVerificationResult? Sha256, HashVerificationResult? Md5)> VerifyHashesAsync(
        string filePath,
        DownloadOptions options,
        CancellationToken cancellationToken)
    {
        HashVerificationResult? sha256 = null;
        HashVerificationResult? md5 = null;

        if (!string.IsNullOrWhiteSpace(options.ExpectedSha256))
        {
            sha256 = await HashVerifier.VerifySha256Async(filePath, options.ExpectedSha256, cancellationToken).ConfigureAwait(false);
            if (!sha256.IsMatch)
            {
                throw new HashMismatchException(filePath, sha256);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedMd5))
        {
            md5 = await HashVerifier.VerifyMd5Async(filePath, options.ExpectedMd5, cancellationToken).ConfigureAwait(false);
            if (!md5.IsMatch)
            {
                throw new HashMismatchException(filePath, md5);
            }
        }

        return (sha256, md5);
    }

    private static bool CanSafelyResume(DownloadState state, ProbeResult probe, string partFilePath)
    {
        if (!probe.SupportsRanges || !probe.TotalBytes.HasValue || probe.TotalBytes.Value <= 0)
        {
            return false;
        }

        if (!state.TotalBytes.HasValue || state.TotalBytes.Value != probe.TotalBytes.Value)
        {
            return false;
        }

        if (state.Segments.Count == 0 || !File.Exists(partFilePath))
        {
            return false;
        }

        // Validate strong ETag if both are available.
        if (!string.IsNullOrWhiteSpace(state.ETag) && !string.IsNullOrWhiteSpace(probe.ETag))
        {
            if (!string.Equals(state.ETag, probe.ETag, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (state.LastModified.HasValue && probe.LastModified.HasValue)
        {
            if (state.LastModified.Value != probe.LastModified.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveDestinationPath(string outputPathOrDirectory, string suggestedFileName)
    {
        bool endsWithSeparator = outputPathOrDirectory.EndsWith(Path.DirectorySeparatorChar) ||
                                 outputPathOrDirectory.EndsWith(Path.AltDirectorySeparatorChar);

        if (endsWithSeparator || Directory.Exists(outputPathOrDirectory))
        {
            return PathSanitizer.ResolveSafeDestinationPath(outputPathOrDirectory, suggestedFileName);
        }

        string fullPath = Path.GetFullPath(outputPathOrDirectory);
        string parentDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        string fileName = Path.GetFileName(fullPath);
        return PathSanitizer.ResolveSafeDestinationPath(parentDir, fileName);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
