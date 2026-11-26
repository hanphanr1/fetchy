namespace Fetchy.Core.Engine;

/// <summary>
/// Configuration options controlling concurrency, dynamic work-stealing, bandwidth limits, retries, and integrity verification.
/// </summary>
public sealed class DownloadOptions
{
    public const int DefaultMaxConnections = 16;
    public const long DefaultMinSplitBytes = 256 * 1024;
    public const long DefaultSmallFileThresholdBytes = 512 * 1024;

    private int _maxConnections = DefaultMaxConnections;

    /// <summary>
    /// Maximum concurrent HTTP connections (clamped to 1..32).
    /// </summary>
    public int MaxConnections
    {
        get => _maxConnections;
        init => _maxConnections = Math.Clamp(value, 1, 32);
    }

    /// <summary>
    /// Minimum byte size for each half when dynamically splitting an active segment.
    /// </summary>
    public long MinSplitBytes { get; init; } = DefaultMinSplitBytes;

    /// <summary>
    /// Files smaller than this threshold automatically use a single stream.
    /// </summary>
    public long SmallFileThresholdBytes { get; init; } = DefaultSmallFileThresholdBytes;

    /// <summary>
    /// Maximum aggregate download rate in bytes per second (<c>0</c> = unlimited).
    /// Ignored if <see cref="SharedBandwidthLimiter"/> is supplied.
    /// </summary>
    public long MaxBytesPerSecond { get; init; }

    /// <summary>
    /// Optional externally managed bandwidth limiter enabling runtime rate updates via <see cref="BandwidthLimiter.SetLimit"/>.
    /// </summary>
    public BandwidthLimiter? SharedBandwidthLimiter { get; init; }

    /// <summary>
    /// Overwrites an existing file at the destination path when true; auto-disambiguates (e.g. <c>file (1).ext</c>) when false.
    /// </summary>
    public bool Overwrite { get; init; }

    public string? ExpectedSha256 { get; init; }
    public string? ExpectedMd5 { get; init; }

    /// <summary>
    /// Optional HTTP headers (e.g. Referer, User-Agent). Sensitive headers are never written to disk.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public RetryPolicy RetryPolicy { get; init; } = RetryPolicy.Default;
    public TimeSpan ProgressReportInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan StateCheckpointInterval { get; init; } = TimeSpan.FromSeconds(1);
}
