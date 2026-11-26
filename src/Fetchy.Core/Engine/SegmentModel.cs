namespace Fetchy.Core.Engine;

/// <summary>
/// Execution state of a byte-range segment.
/// </summary>
public enum SegmentState
{
    Pending,
    Active,
    Paused,
    Completed,
    Failed
}

/// <summary>
/// Read-only snapshot of an individual segment's progress.
/// </summary>
public sealed record SegmentProgress(
    int Id,
    long Start,
    long Current,
    long End,
    SegmentState State,
    double SpeedBps)
{
    public long TotalBytes => End >= Start ? End - Start + 1 : 0;
    public long DownloadedBytes => Math.Clamp(Current - Start, 0, TotalBytes);
    public long RemainingBytes => End >= Current ? End - Current + 1 : 0;
}

/// <summary>
/// Read-only snapshot of overall download progress, speeds, ETA, work-steal stats, and segment states.
/// </summary>
public sealed record DownloadProgressSnapshot(
    long? TotalBytes,
    long DownloadedBytes,
    double InstantSpeedBps,
    double SmoothedSpeedBps,
    TimeSpan? Eta,
    int ActiveConnections,
    int WorkStealCount,
    int AdaptiveStepDownCount,
    IReadOnlyList<SegmentProgress> Segments)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes.Value * 100.0, 0.0, 100.0)
        : null;
}
