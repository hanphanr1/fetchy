using Fetchy.Core.Storage;

namespace Fetchy.Core.Engine;

/// <summary>
/// Coordinates dynamic work-stealing byte-range segments with lock-protected atomic boundary updates
/// so concurrent workers never overlap writes or leave byte gaps.
/// </summary>
public sealed class SegmentScheduler
{
    private sealed class SegmentEntry
    {
        public int Id { get; init; }
        public long Start { get; init; }
        public long WrittenOffset { get; set; }
        public long ReservedOffset { get; set; }
        public long End { get; set; }
        public SegmentState State { get; set; }
        public double SpeedBps { get; set; }
        public int RetryCount { get; set; }
        public DateTimeOffset NotBeforeUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly object _sync = new();
    private readonly List<SegmentEntry> _segments = [];
    private readonly long _minSplitBytes;
    private int _nextSegmentId;
    private int _workStealCount;

    public long TotalBytes { get; }

    public int WorkStealCount
    {
        get
        {
            lock (_sync)
            {
                return _workStealCount;
            }
        }
    }

    public long TotalDownloadedBytes
    {
        get
        {
            lock (_sync)
            {
                long total = 0;
                foreach (var seg in _segments)
                {
                    if (seg.End >= seg.Start)
                    {
                        total += Math.Clamp(seg.WrittenOffset - seg.Start, 0, seg.End - seg.Start + 1);
                    }
                }

                return total;
            }
        }
    }

    public bool IsAllCompleted
    {
        get
        {
            lock (_sync)
            {
                if (_segments.Count == 0)
                {
                    return TotalBytes == 0;
                }

                foreach (var seg in _segments)
                {
                    if (seg.WrittenOffset <= seg.End)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    public bool HasUnfinishedWork
    {
        get
        {
            lock (_sync)
            {
                foreach (var seg in _segments)
                {
                    if (seg.WrittenOffset <= seg.End && seg.State != SegmentState.Failed)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Initializes a new scheduler partitioning <paramref name="totalBytes"/> across up to <paramref name="initialConnections"/> segments.
    /// </summary>
    public SegmentScheduler(
        long totalBytes,
        int initialConnections,
        long minSplitBytes = 256 * 1024,
        long smallFileThresholdBytes = 512 * 1024)
    {
        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes), "Total bytes must be non-negative.");
        }

        TotalBytes = totalBytes;
        _minSplitBytes = Math.Max(1, minSplitBytes);

        if (totalBytes == 0)
        {
            return;
        }

        int clampedConnections = Math.Clamp(initialConnections, 1, 32);
        int segmentCount;
        if (totalBytes < smallFileThresholdBytes || clampedConnections == 1)
        {
            segmentCount = 1;
        }
        else
        {
            long maxByMinSplit = Math.Max(1, totalBytes / _minSplitBytes);
            segmentCount = (int)Math.Min(clampedConnections, maxByMinSplit);
        }

        long baseSize = totalBytes / segmentCount;
        long remainder = totalBytes % segmentCount;
        long cursor = 0;

        for (int i = 0; i < segmentCount; i++)
        {
            long size = baseSize + (i < remainder ? 1 : 0);
            long start = cursor;
            long end = cursor + size - 1;
            cursor = end + 1;

            _segments.Add(new SegmentEntry
            {
                Id = _nextSegmentId++,
                Start = start,
                WrittenOffset = start,
                ReservedOffset = start,
                End = end,
                State = SegmentState.Pending
            });
        }
    }

    private SegmentScheduler(long totalBytes, long minSplitBytes, List<SegmentEntry> restoredSegments)
    {
        TotalBytes = totalBytes;
        _minSplitBytes = Math.Max(1, minSplitBytes);
        _segments = restoredSegments;
        _nextSegmentId = restoredSegments.Count == 0 ? 0 : restoredSegments.Max(s => s.Id) + 1;
    }

    /// <summary>
    /// Reconstructs a scheduler from previously persisted segment checkpoints.
    /// </summary>
    public static SegmentScheduler FromPersisted(
        long totalBytes,
        IReadOnlyList<PersistedSegment> persisted,
        long minSplitBytes = 256 * 1024)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        var entries = new List<SegmentEntry>(persisted.Count);
        foreach (var seg in persisted)
        {
            long clampedCurrent = Math.Clamp(seg.Current, seg.Start, seg.End + 1);
            bool completed = seg.State == SegmentState.Completed || clampedCurrent > seg.End;

            entries.Add(new SegmentEntry
            {
                Id = seg.Id,
                Start = seg.Start,
                WrittenOffset = completed ? seg.End + 1 : clampedCurrent,
                ReservedOffset = completed ? seg.End + 1 : clampedCurrent,
                End = seg.End,
                State = completed ? SegmentState.Completed : SegmentState.Pending
            });
        }

        return new SegmentScheduler(totalBytes, minSplitBytes, entries);
    }

    /// <summary>
    /// Acquires the next pending segment or dynamically steals the trailing half of the largest active segment.
    /// Returns false if no work is immediately available (with optional <paramref name="waitDelay"/> for backed-off segments).
    /// </summary>
    public bool TryAcquireWork(
        int maxAllowedActiveWorkers,
        out int segmentId,
        out TimeSpan? waitDelay)
    {
        segmentId = -1;
        waitDelay = null;

        lock (_sync)
        {
            int currentActive = 0;
            foreach (var seg in _segments)
            {
                if (seg.State == SegmentState.Active)
                {
                    currentActive++;
                }
            }

            if (currentActive >= Math.Max(1, maxAllowedActiveWorkers))
            {
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan? earliestDelay = null;

            // 1. Prefer any immediately runnable pending/paused segment.
            foreach (var seg in _segments)
            {
                if (seg.WrittenOffset > seg.End)
                {
                    seg.State = SegmentState.Completed;
                    continue;
                }

                if (seg.State is SegmentState.Pending or SegmentState.Paused)
                {
                    if (seg.NotBeforeUtc <= now)
                    {
                        seg.State = SegmentState.Active;
                        seg.ReservedOffset = seg.WrittenOffset;
                        segmentId = seg.Id;
                        return true;
                    }

                    TimeSpan delay = seg.NotBeforeUtc - now;
                    if (earliestDelay is null || delay < earliestDelay.Value)
                    {
                        earliestDelay = delay;
                    }
                }
            }

            // 2. No runnable pending segment: dynamically steal half of the active segment with the largest remaining bytes.
            SegmentEntry? victim = null;
            long largestRemaining = 0;

            foreach (var seg in _segments)
            {
                if (seg.State != SegmentState.Active)
                {
                    continue;
                }

                long stealableBase = Math.Max(seg.WrittenOffset, seg.ReservedOffset);
                long remaining = seg.End - stealableBase + 1;
                if (remaining > largestRemaining)
                {
                    largestRemaining = remaining;
                    victim = seg;
                }
            }

            if (victim is not null && largestRemaining >= _minSplitBytes * 2 && largestRemaining >= 2)
            {
                long stealableBase = Math.Max(victim.WrittenOffset, victim.ReservedOffset);
                long half = largestRemaining / 2;
                long mid = stealableBase + half;

                if (mid > stealableBase && mid <= victim.End)
                {
                    long oldEnd = victim.End;
                    victim.End = mid - 1;

                    var stolen = new SegmentEntry
                    {
                        Id = _nextSegmentId++,
                        Start = mid,
                        WrittenOffset = mid,
                        ReservedOffset = mid,
                        End = oldEnd,
                        State = SegmentState.Active
                    };

                    _segments.Add(stolen);
                    _workStealCount++;
                    segmentId = stolen.Id;
                    return true;
                }
            }

            waitDelay = earliestDelay;
            return false;
        }
    }

    /// <summary>
    /// Reads the current inclusive bounds [offset, end] for <paramref name="segmentId"/>.
    /// Returns false if the segment has already completed (including after being shortened by work-stealing).
    /// </summary>
    public bool TryGetCurrentBounds(int segmentId, out long offset, out long end)
    {
        lock (_sync)
        {
            SegmentEntry seg = FindSegmentLocked(segmentId);
            offset = seg.WrittenOffset;
            end = seg.End;

            if (offset > end)
            {
                seg.State = SegmentState.Completed;
                seg.SpeedBps = 0;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Atomically clamps <paramref name="bytesRead"/> against the segment's current <c>End</c>
    /// and reserves the write span so concurrent work-stealers cannot split across an in-flight disk write.
    /// </summary>
    public bool TryReserveWrite(int segmentId, int bytesRead, out long writeOffset, out int bytesToWrite)
    {
        lock (_sync)
        {
            SegmentEntry seg = FindSegmentLocked(segmentId);
            writeOffset = seg.WrittenOffset;

            long maxAllowed = seg.End - writeOffset + 1;
            if (maxAllowed <= 0 || bytesRead <= 0)
            {
                bytesToWrite = 0;
                if (maxAllowed <= 0)
                {
                    seg.State = SegmentState.Completed;
                    seg.SpeedBps = 0;
                }

                return false;
            }

            bytesToWrite = (int)Math.Min(bytesRead, maxAllowed);
            seg.ReservedOffset = writeOffset + bytesToWrite;
            return true;
        }
    }

    /// <summary>
    /// Commits a completed disk write and returns true if the segment has now finished its assigned range.
    /// </summary>
    public bool CommitWrite(int segmentId, long writeOffset, int bytesWritten, double speedBps)
    {
        lock (_sync)
        {
            SegmentEntry seg = FindSegmentLocked(segmentId);
            seg.WrittenOffset = writeOffset + bytesWritten;
            seg.ReservedOffset = seg.WrittenOffset;
            seg.SpeedBps = speedBps;
            seg.RetryCount = 0;

            if (seg.WrittenOffset > seg.End)
            {
                seg.State = SegmentState.Completed;
                seg.SpeedBps = 0;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Rolls back an uncommitted in-flight reservation if a write was cancelled or failed.
    /// </summary>
    public void RollbackReservation(int segmentId)
    {
        lock (_sync)
        {
            SegmentEntry seg = FindSegmentLocked(segmentId);
            seg.ReservedOffset = seg.WrittenOffset;
        }
    }

    /// <summary>
    /// Returns a segment to <see cref="SegmentState.Pending"/> at its exact last committed offset for retry.
    /// Returns the updated retry count.
    /// </summary>
    public int RequeueForRetry(int segmentId, TimeSpan retryDelay)
    {
        lock (_sync)
        {
            SegmentEntry seg = FindSegmentLocked(segmentId);
            seg.ReservedOffset = seg.WrittenOffset;
            seg.SpeedBps = 0;

            if (seg.WrittenOffset > seg.End)
            {
                seg.State = SegmentState.Completed;
                return seg.RetryCount;
            }

            seg.RetryCount++;
            seg.State = SegmentState.Pending;
            seg.NotBeforeUtc = retryDelay > TimeSpan.Zero
                ? DateTimeOffset.UtcNow + retryDelay
                : DateTimeOffset.MinValue;

            return seg.RetryCount;
        }
    }

    public int GetRetryCount(int segmentId)
    {
        lock (_sync)
        {
            return FindSegmentLocked(segmentId).RetryCount;
        }
    }

    public void MarkPaused(int segmentId)
    {
        lock (_sync)
        {
            SegmentEntry seg = FindSegmentLocked(segmentId);
            seg.ReservedOffset = seg.WrittenOffset;
            seg.SpeedBps = 0;
            seg.State = seg.WrittenOffset > seg.End ? SegmentState.Completed : SegmentState.Paused;
        }
    }

    public void MarkFailed(int segmentId)
    {
        lock (_sync)
        {
            SegmentEntry seg = FindSegmentLocked(segmentId);
            seg.ReservedOffset = seg.WrittenOffset;
            seg.SpeedBps = 0;
            seg.State = SegmentState.Failed;
        }
    }

    public int ActiveSegmentCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                foreach (var seg in _segments)
                {
                    if (seg.State == SegmentState.Active)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public IReadOnlyList<SegmentProgress> GetSegmentSnapshots()
    {
        lock (_sync)
        {
            var snapshots = new List<SegmentProgress>(_segments.Count);
            foreach (var seg in _segments)
            {
                snapshots.Add(new SegmentProgress(
                    seg.Id,
                    seg.Start,
                    seg.WrittenOffset,
                    seg.End,
                    seg.State,
                    seg.SpeedBps));
            }

            return snapshots;
        }
    }

    public List<PersistedSegment> ExportPersistedSegments()
    {
        lock (_sync)
        {
            var result = new List<PersistedSegment>(_segments.Count);
            foreach (var seg in _segments)
            {
                SegmentState persistedState = seg.WrittenOffset > seg.End
                    ? SegmentState.Completed
                    : seg.State == SegmentState.Active
                        ? SegmentState.Paused
                        : seg.State;

                result.Add(new PersistedSegment
                {
                    Id = seg.Id,
                    Start = seg.Start,
                    Current = seg.WrittenOffset,
                    End = seg.End,
                    State = persistedState
                });
            }

            return result;
        }
    }

    private SegmentEntry FindSegmentLocked(int segmentId)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            if (_segments[i].Id == segmentId)
            {
                return _segments[i];
            }
        }

        throw new KeyNotFoundException($"Segment {segmentId} not found.");
    }
}
