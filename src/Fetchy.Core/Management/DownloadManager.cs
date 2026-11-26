using System.Collections.Concurrent;
using System.Text.Json;
using Fetchy.Core.Engine;
using Fetchy.Core.Http;
using Fetchy.Core.Storage;

namespace Fetchy.Core.Management;

/// <summary>
/// Manages a concurrent download queue, lifecycle transitions (enqueue, start, pause, resume, refresh URL, cancel, remove),
/// and crash-safe atomic <c>downloads.json</c> catalog persistence.
/// </summary>
public sealed class DownloadManager : IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class ManagedItem
    {
        public required DownloadCatalogEntry Entry { get; set; }
        public DownloadOptions Options { get; set; } = new();
        public CancellationTokenSource? RunCts { get; set; }
        public Task? RunTask { get; set; }
        public TaskCompletionSource<DownloadCatalogEntry> CompletionSource { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }
        public IProgress<DownloadProgressSnapshot>? Progress { get; set; }
    }

    private readonly string _catalogFilePath;
    private readonly string _defaultOutputDirectory;
    private readonly SegmentedDownloader _downloader;
    private readonly bool _ownsDownloader;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly SemaphoreSlim _catalogSaveGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, ManagedItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _pendingQueue = new();
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _ephemeralHeaders = new(StringComparer.OrdinalIgnoreCase);
    private int _activeCount;
    private bool _disposed;

    public int MaxConcurrentDownloads { get; }
    public string CatalogFilePath => _catalogFilePath;

    public event Action<string, DownloadProgressSnapshot>? ProgressChanged;
    public event Action<DownloadCatalogEntry>? EntryUpdated;

    public DownloadManager(
        string catalogFilePath,
        string defaultOutputDirectory,
        int maxConcurrentDownloads = 3,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(catalogFilePath))
        {
            throw new ArgumentException("Catalog file path must not be empty.", nameof(catalogFilePath));
        }

        if (string.IsNullOrWhiteSpace(defaultOutputDirectory))
        {
            throw new ArgumentException("Default output directory must not be empty.", nameof(defaultOutputDirectory));
        }

        if (maxConcurrentDownloads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentDownloads), "Must be at least 1.");
        }

        _catalogFilePath = Path.GetFullPath(catalogFilePath);
        _defaultOutputDirectory = Path.GetFullPath(defaultOutputDirectory);
        MaxConcurrentDownloads = maxConcurrentDownloads;
        _concurrencyGate = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
        _downloader = httpClient is null
            ? new SegmentedDownloader()
            : new SegmentedDownloader(httpClient, ownsHttpClient: false);
        _ownsDownloader = true;
    }

    /// <summary>
    /// Loads persisted entries from <c>downloads.json</c> if present.
    /// Any entries that were actively downloading before shutdown are safely transitioned to <see cref="DownloadStatus.Paused"/>.
    /// </summary>
    public async Task LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(_catalogFilePath))
        {
            return;
        }

        await using var stream = new FileStream(
            _catalogFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);

        var loaded = await JsonSerializer.DeserializeAsync<List<DownloadCatalogEntry>>(
            stream,
            CatalogJsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (loaded is null)
        {
            return;
        }

        lock (_sync)
        {
            _items.Clear();
            _pendingQueue.Clear();

            foreach (var entry in loaded)
            {
                entry.Headers = UriValidator.FilterPersistableHeaders(entry.Headers);
                if (entry.Status == DownloadStatus.Downloading)
                {
                    entry.Status = DownloadStatus.Paused;
                }

                var options = new DownloadOptions
                {
                    MaxConnections = entry.MaxConnections,
                    MaxBytesPerSecond = entry.MaxBytesPerSecond,
                    Overwrite = entry.Overwrite,
                    ExpectedSha256 = entry.ExpectedSha256,
                    ExpectedMd5 = entry.ExpectedMd5,
                    Headers = entry.Headers
                };

                _items[entry.Id] = new ManagedItem
                {
                    Entry = entry,
                    Options = options
                };
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of all download catalog entries.
    /// </summary>
    public IReadOnlyList<DownloadCatalogEntry> GetAll()
    {
        lock (_sync)
        {
            return _items.Values
                .Select(i => i.Entry.Clone())
                .OrderBy(e => e.CreatedAtUtc)
                .ToList();
        }
    }

    /// <summary>
    /// Returns a snapshot of a single catalog entry, or null if not found.
    /// </summary>
    public DownloadCatalogEntry? Get(string downloadId)
    {
        lock (_sync)
        {
            return _items.TryGetValue(downloadId, out var item) ? item.Entry.Clone() : null;
        }
    }

    /// <summary>
    /// Enqueues a new download, persisting only safe non-sensitive headers to <c>downloads.json</c>.
    /// </summary>
    public async Task<DownloadCatalogEntry> EnqueueAsync(
        Uri uri,
        string? outputPathOrDirectory = null,
        DownloadOptions? options = null,
        bool autoStart = true,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri validated = UriValidator.ValidateHttpUri(uri);
        options ??= new DownloadOptions();

        string outputTarget = string.IsNullOrWhiteSpace(outputPathOrDirectory)
            ? PathSanitizer.ResolveSafeDestinationPath(_defaultOutputDirectory, PathSanitizer.ExtractFileNameFromUri(validated))
            : ResolveManagedOutputPath(outputPathOrDirectory, validated);

        var persistableHeaders = UriValidator.FilterPersistableHeaders(options.Headers);
        var entry = new DownloadCatalogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = validated.AbsoluteUri,
            OutputPath = outputTarget,
            MaxConnections = options.MaxConnections,
            MaxBytesPerSecond = options.MaxBytesPerSecond,
            Overwrite = options.Overwrite,
            ExpectedSha256 = options.ExpectedSha256,
            ExpectedMd5 = options.ExpectedMd5,
            Status = autoStart ? DownloadStatus.Queued : DownloadStatus.Paused,
            Category = CategoryClassifier.Classify(outputTarget),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Headers = persistableHeaders
        };

        var item = new ManagedItem
        {
            Entry = entry,
            Options = options,
            Progress = progress
        };

        if (options.Headers is not null && options.Headers.Count > 0)
        {
            // Keep runtime headers in memory only; never write sensitive headers to disk.
            _ephemeralHeaders[entry.Id] = new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);
        }

        lock (_sync)
        {
            _items[entry.Id] = item;
            if (autoStart)
            {
                _pendingQueue.Enqueue(entry.Id);
            }
        }

        await SaveCatalogAsync(cancellationToken).ConfigureAwait(false);
        NotifyUpdated(entry);

        if (autoStart)
        {
            PumpQueue();
        }

        return entry.Clone();
    }

    /// <summary>
    /// Starts or queues an existing paused/failed download.
    /// </summary>
    public async Task StartAsync(string downloadId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DownloadCatalogEntry snapshot;
        lock (_sync)
        {
            ManagedItem item = GetItemLocked(downloadId);
            if (item.Entry.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Completed)
            {
                return;
            }

            item.PauseRequested = false;
            item.CancelRequested = false;
            item.Entry.ErrorMessage = null;
            item.Entry.Status = DownloadStatus.Queued;
            item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
            item.CompletionSource = new TaskCompletionSource<DownloadCatalogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingQueue.Enqueue(downloadId);
            snapshot = item.Entry.Clone();
        }

        await SaveCatalogAsync(cancellationToken).ConfigureAwait(false);
        NotifyUpdated(snapshot);
        PumpQueue();
    }

    /// <summary>
    /// Pauses an active or queued download and awaits clean state checkpoint flush.
    /// </summary>
    public async Task PauseAsync(string downloadId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task? runningTask;
        CancellationTokenSource? runCts;
        DownloadCatalogEntry? immediateSnapshot = null;

        lock (_sync)
        {
            ManagedItem item = GetItemLocked(downloadId);
            if (item.Entry.Status is DownloadStatus.Completed or DownloadStatus.Paused)
            {
                return;
            }

            item.PauseRequested = true;
            item.CancelRequested = false;
            runCts = item.RunCts;
            runningTask = item.RunTask;

            if (runningTask is null)
            {
                item.Entry.Status = DownloadStatus.Paused;
                item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                immediateSnapshot = item.Entry.Clone();
            }
        }

        if (runCts is not null)
        {
            try
            {
                runCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (runningTask is not null)
        {
            await runningTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (immediateSnapshot is not null)
        {
            await SaveCatalogAsync(cancellationToken).ConfigureAwait(false);
            NotifyUpdated(immediateSnapshot);
        }
    }

    /// <summary>
    /// Resumes a paused or failed download, picking up from its <c>.fetchy.state</c> checkpoint if present.
    /// </summary>
    public Task ResumeAsync(string downloadId, DownloadOptions? overrideOptions = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (overrideOptions is not null)
        {
            lock (_sync)
            {
                ManagedItem item = GetItemLocked(downloadId);
                item.Options = overrideOptions;
                item.Entry.MaxConnections = overrideOptions.MaxConnections;
                item.Entry.MaxBytesPerSecond = overrideOptions.MaxBytesPerSecond;
                item.Entry.Overwrite = overrideOptions.Overwrite;
                item.Entry.ExpectedSha256 = overrideOptions.ExpectedSha256 ?? item.Entry.ExpectedSha256;
                item.Entry.ExpectedMd5 = overrideOptions.ExpectedMd5 ?? item.Entry.ExpectedMd5;
                if (overrideOptions.Headers is not null)
                {
                    item.Entry.Headers = UriValidator.FilterPersistableHeaders(overrideOptions.Headers);
                    _ephemeralHeaders[downloadId] = new Dictionary<string, string>(overrideOptions.Headers, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        return StartAsync(downloadId, cancellationToken);
    }

    /// <summary>
    /// Replaces the download URL (e.g. for an expired signed URL) in both the catalog and any <c>.fetchy.state</c> checkpoint.
    /// </summary>
    public async Task<DownloadCatalogEntry> RefreshUrlAsync(
        string downloadId,
        Uri newUri,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri validated = UriValidator.ValidateHttpUri(newUri);

        string outputPath;
        DownloadCatalogEntry snapshot;
        lock (_sync)
        {
            ManagedItem item = GetItemLocked(downloadId);
            if (item.Entry.Status == DownloadStatus.Downloading)
            {
                throw new InvalidOperationException("Pause the download before refreshing its URL.");
            }

            item.Entry.Url = validated.AbsoluteUri;
            item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
            outputPath = item.Entry.OutputPath;
            snapshot = item.Entry.Clone();
        }

        string stateFile = DownloadStateStore.GetStateFilePath(outputPath);
        if (File.Exists(stateFile))
        {
            await SegmentedDownloader.RefreshUrlAsync(stateFile, validated, cancellationToken).ConfigureAwait(false);
        }

        await SaveCatalogAsync(cancellationToken).ConfigureAwait(false);
        NotifyUpdated(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Cancels an active or queued download.
    /// </summary>
    public async Task CancelAsync(string downloadId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task? runningTask;
        CancellationTokenSource? runCts;
        DownloadCatalogEntry? immediateSnapshot = null;

        lock (_sync)
        {
            ManagedItem item = GetItemLocked(downloadId);
            if (item.Entry.Status is DownloadStatus.Completed or DownloadStatus.Cancelled)
            {
                return;
            }

            item.CancelRequested = true;
            item.PauseRequested = false;
            runCts = item.RunCts;
            runningTask = item.RunTask;

            if (runningTask is null)
            {
                item.Entry.Status = DownloadStatus.Cancelled;
                item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                item.CompletionSource.TrySetResult(item.Entry.Clone());
                immediateSnapshot = item.Entry.Clone();
            }
        }

        if (runCts is not null)
        {
            try
            {
                runCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (runningTask is not null)
        {
            await runningTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (immediateSnapshot is not null)
        {
            await SaveCatalogAsync(cancellationToken).ConfigureAwait(false);
            NotifyUpdated(immediateSnapshot);
        }
    }

    /// <summary>
    /// Cancels (if active), removes the entry from the catalog, and cleans up <c>.fetchy.part</c> and <c>.fetchy.state</c> files.
    /// </summary>
    public async Task RemoveAsync(
        string downloadId,
        bool deleteCompletedFile = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool exists;
        lock (_sync)
        {
            exists = _items.ContainsKey(downloadId);
        }

        if (!exists)
        {
            return;
        }

        await CancelAsync(downloadId, cancellationToken).ConfigureAwait(false);

        string outputPath;
        string? finalPath;
        lock (_sync)
        {
            if (!_items.Remove(downloadId, out var item))
            {
                return;
            }

            outputPath = item.Entry.OutputPath;
            finalPath = item.Entry.FinalFilePath;
        }

        _ephemeralHeaders.TryRemove(downloadId, out _);

        TryDeleteFile(DirectDiskWriter.GetPartFilePath(outputPath));
        TryDeleteFile(DownloadStateStore.GetStateFilePath(outputPath));

        if (deleteCompletedFile)
        {
            if (!string.IsNullOrWhiteSpace(finalPath))
            {
                TryDeleteFile(finalPath);
            }

            TryDeleteFile(outputPath);
        }

        await SaveCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits settlement of a download (Completed, Paused, Cancelled, or Failed).
    /// </summary>
    public Task<DownloadCatalogEntry> WaitForSettlementAsync(string downloadId, CancellationToken cancellationToken = default)
    {
        Task<DownloadCatalogEntry> task;
        lock (_sync)
        {
            ManagedItem item = GetItemLocked(downloadId);
            if (item.Entry.Status is DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Failed)
            {
                return Task.FromResult(item.Entry.Clone());
            }

            task = item.CompletionSource.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    private void PumpQueue()
    {
        while (true)
        {
            ManagedItem? nextItem = null;

            lock (_sync)
            {
                if (_disposed || _activeCount >= MaxConcurrentDownloads)
                {
                    return;
                }

                while (_pendingQueue.Count > 0)
                {
                    string candidateId = _pendingQueue.Dequeue();
                    if (!_items.TryGetValue(candidateId, out var candidate))
                    {
                        continue;
                    }

                    if (candidate.Entry.Status != DownloadStatus.Queued || candidate.PauseRequested || candidate.CancelRequested)
                    {
                        continue;
                    }

                    nextItem = candidate;
                    _activeCount++;
                    nextItem.Entry.Status = DownloadStatus.Downloading;
                    nextItem.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    nextItem.RunCts = new CancellationTokenSource();
                    nextItem.RunTask = Task.Run(() => ExecuteItemAsync(nextItem));
                    break;
                }
            }

            if (nextItem is null)
            {
                return;
            }
        }
    }

    private async Task ExecuteItemAsync(ManagedItem item)
    {
        string id = item.Entry.Id;
        CancellationToken ct = item.RunCts!.Token;

        await _concurrencyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await SaveCatalogAsync(CancellationToken.None).ConfigureAwait(false);
            NotifyUpdated(item.Entry.Clone());

            string outputPath = item.Entry.OutputPath;
            string stateFile = DownloadStateStore.GetStateFilePath(outputPath);
            Uri uri = UriValidator.ValidateHttpUri(item.Entry.Url);

            IReadOnlyDictionary<string, string>? runtimeHeaders =
                _ephemeralHeaders.TryGetValue(id, out var memHeaders) ? memHeaders : item.Entry.Headers;

            var effectiveOptions = new DownloadOptions
            {
                MaxConnections = item.Options.MaxConnections,
                MinSplitBytes = item.Options.MinSplitBytes,
                SmallFileThresholdBytes = item.Options.SmallFileThresholdBytes,
                MaxBytesPerSecond = item.Options.MaxBytesPerSecond,
                SharedBandwidthLimiter = item.Options.SharedBandwidthLimiter,
                Overwrite = item.Options.Overwrite,
                ExpectedSha256 = item.Options.ExpectedSha256 ?? item.Entry.ExpectedSha256,
                ExpectedMd5 = item.Options.ExpectedMd5 ?? item.Entry.ExpectedMd5,
                Headers = runtimeHeaders,
                RetryPolicy = item.Options.RetryPolicy,
                ProgressReportInterval = item.Options.ProgressReportInterval,
                StateCheckpointInterval = item.Options.StateCheckpointInterval
            };

            var progressRelay = new Progress<DownloadProgressSnapshot>(snapshot =>
            {
                lock (_sync)
                {
                    item.Entry.TotalBytes = snapshot.TotalBytes ?? item.Entry.TotalBytes;
                    item.Entry.DownloadedBytes = snapshot.DownloadedBytes;
                    item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                }

                item.Progress?.Report(snapshot);
                ProgressChanged?.Invoke(id, snapshot);
            });

            DownloadResult result;
            if (File.Exists(stateFile))
            {
                result = await _downloader.ResumeAsync(
                    stateFile,
                    effectiveOptions,
                    refreshedUri: uri,
                    progress: progressRelay,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                result = await _downloader.DownloadAsync(
                    uri,
                    outputPath,
                    effectiveOptions,
                    progress: progressRelay,
                    cancellationToken: ct).ConfigureAwait(false);
            }

            DownloadCatalogEntry completedSnapshot;
            lock (_sync)
            {
                item.Entry.Url = result.FinalUri.AbsoluteUri;
                item.Entry.FinalFilePath = result.FilePath;
                item.Entry.TotalBytes = result.TotalBytes;
                item.Entry.DownloadedBytes = result.BytesDownloaded;
                item.Entry.Category = result.Category;
                item.Entry.Status = DownloadStatus.Completed;
                item.Entry.ErrorMessage = null;
                item.Entry.CompletedAtUtc = DateTimeOffset.UtcNow;
                item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                completedSnapshot = item.Entry.Clone();
            }

            await SaveCatalogAsync(CancellationToken.None).ConfigureAwait(false);
            NotifyUpdated(completedSnapshot);
            item.CompletionSource.TrySetResult(completedSnapshot);
        }
        catch (OperationCanceledException)
        {
            DownloadCatalogEntry settledSnapshot;
            lock (_sync)
            {
                item.Entry.Status = item.CancelRequested ? DownloadStatus.Cancelled : DownloadStatus.Paused;
                item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                settledSnapshot = item.Entry.Clone();
            }

            await SaveCatalogAsync(CancellationToken.None).ConfigureAwait(false);
            NotifyUpdated(settledSnapshot);
            item.CompletionSource.TrySetResult(settledSnapshot);
        }
        catch (Exception ex)
        {
            DownloadCatalogEntry failedSnapshot;
            lock (_sync)
            {
                item.Entry.Status = DownloadStatus.Failed;
                item.Entry.ErrorMessage = ex.Message;
                item.Entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
                failedSnapshot = item.Entry.Clone();
            }

            await SaveCatalogAsync(CancellationToken.None).ConfigureAwait(false);
            NotifyUpdated(failedSnapshot);
            item.CompletionSource.TrySetResult(failedSnapshot);
        }
        finally
        {
            _concurrencyGate.Release();

            CancellationTokenSource? ctsToDispose;
            lock (_sync)
            {
                ctsToDispose = item.RunCts;
                item.RunCts = null;
                item.RunTask = null;
                _activeCount = Math.Max(0, _activeCount - 1);
            }

            ctsToDispose?.Dispose();
            PumpQueue();
        }
    }

    private async Task SaveCatalogAsync(CancellationToken cancellationToken)
    {
        List<DownloadCatalogEntry> entries;
        lock (_sync)
        {
            entries = _items.Values
                .Select(i =>
                {
                    var clone = i.Entry.Clone();
                    clone.Headers = UriValidator.FilterPersistableHeaders(clone.Headers);
                    return clone;
                })
                .OrderBy(e => e.CreatedAtUtc)
                .ToList();
        }

        await _catalogSaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DownloadStateStore.WriteJsonAtomicallyAsync(_catalogFilePath, entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _catalogSaveGate.Release();
        }
    }

    private string ResolveManagedOutputPath(string outputPathOrDirectory, Uri uri)
    {
        bool endsWithSeparator = outputPathOrDirectory.EndsWith(Path.DirectorySeparatorChar) ||
                                 outputPathOrDirectory.EndsWith(Path.AltDirectorySeparatorChar);

        if (endsWithSeparator || Directory.Exists(outputPathOrDirectory))
        {
            return PathSanitizer.ResolveSafeDestinationPath(outputPathOrDirectory, PathSanitizer.ExtractFileNameFromUri(uri));
        }

        string fullPath = Path.GetFullPath(outputPathOrDirectory);
        string parentDir = Path.GetDirectoryName(fullPath) ?? _defaultOutputDirectory;
        string fileName = Path.GetFileName(fullPath);
        return PathSanitizer.ResolveSafeDestinationPath(parentDir, fileName);
    }

    private ManagedItem GetItemLocked(string downloadId)
    {
        if (!_items.TryGetValue(downloadId, out var item))
        {
            throw new KeyNotFoundException($"Download '{downloadId}' not found.");
        }

        return item;
    }

    private void NotifyUpdated(DownloadCatalogEntry entry)
    {
        EntryUpdated?.Invoke(entry);
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Task> activeTasks = [];
        lock (_sync)
        {
            foreach (var item in _items.Values)
            {
                item.PauseRequested = true;
                try
                {
                    item.RunCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                if (item.RunTask is not null)
                {
                    activeTasks.Add(item.RunTask);
                }
            }
        }

        if (activeTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(activeTasks).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation/errors during shutdown.
            }
        }

        if (_ownsDownloader)
        {
            _downloader.Dispose();
        }

        _concurrencyGate.Dispose();
        _catalogSaveGate.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
