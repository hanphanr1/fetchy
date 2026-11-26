using System.Diagnostics;

namespace Fetchy.Core.Engine;

/// <summary>
/// Thread-safe, runtime-adjustable Token-Bucket bandwidth limiter shared across all concurrent segment workers.
/// Setting <see cref="MaxBytesPerSecond"/> to <c>0</c> disables throttling with a lock-free fast path.
/// </summary>
public sealed class BandwidthLimiter : IDisposable
{
    private readonly object _sync = new();
    private long _maxBytesPerSecond;
    private double _availableTokens;
    private long _lastRefillTimestamp;
    private CancellationTokenSource _limitChangedCts = new();
    private bool _disposed;

    public BandwidthLimiter(long maxBytesPerSecond = 0)
    {
        if (maxBytesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytesPerSecond), "Speed limit must be non-negative.");
        }

        _maxBytesPerSecond = maxBytesPerSecond;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
        _availableTokens = maxBytesPerSecond > 0 ? ComputeInitialTokens(maxBytesPerSecond) : 0;
    }

    /// <summary>
    /// Current bandwidth ceiling in bytes per second (<c>0</c> means unlimited).
    /// </summary>
    public long MaxBytesPerSecond => Volatile.Read(ref _maxBytesPerSecond);

    /// <summary>
    /// Dynamically updates the speed limit at runtime, waking any waiting workers immediately when increased or removed.
    /// </summary>
    public void SetLimit(long bytesPerSecond)
    {
        if (bytesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), "Speed limit must be non-negative.");
        }

        CancellationTokenSource? previousCts = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            long previousLimit = _maxBytesPerSecond;
            RefillLocked(Stopwatch.GetTimestamp());
            _maxBytesPerSecond = bytesPerSecond;

            if (bytesPerSecond == 0)
            {
                _availableTokens = 0;
            }
            else
            {
                double maxBurst = ComputeBurstCapacity(bytesPerSecond);
                if (previousLimit == 0)
                {
                    _availableTokens = ComputeInitialTokens(bytesPerSecond);
                }
                else if (bytesPerSecond > previousLimit)
                {
                    _availableTokens = Math.Clamp(_availableTokens, 0, maxBurst);
                }
                else
                {
                    _availableTokens = Math.Min(_availableTokens, maxBurst);
                }
            }

            previousCts = _limitChangedCts;
            _limitChangedCts = new CancellationTokenSource();
        }

        try
        {
            previousCts.Cancel();
        }
        finally
        {
            previousCts.Dispose();
        }
    }

    /// <summary>
    /// Suggests a read chunk size appropriate for the active speed limit so rate limiting stays smooth and responsive.
    /// </summary>
    public int GetRecommendedChunkSize(int maxBufferSize)
    {
        long limit = Volatile.Read(ref _maxBytesPerSecond);
        if (limit <= 0)
        {
            return maxBufferSize;
        }

        long target = limit / 16;
        return (int)Math.Clamp(target, 4096, maxBufferSize);
    }

    /// <summary>
    /// Consumes tokens for <paramref name="byteCount"/> transferred bytes, delaying asynchronously when over budget.
    /// </summary>
    public ValueTask ThrottleAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        if (byteCount <= 0 || Volatile.Read(ref _maxBytesPerSecond) <= 0)
        {
            return ValueTask.CompletedTask;
        }

        return ThrottleSlowAsync(byteCount, cancellationToken);
    }

    private async ValueTask ThrottleSlowAsync(int byteCount, CancellationToken cancellationToken)
    {
        TimeSpan delay;
        CancellationToken changeToken;

        lock (_sync)
        {
            if (_disposed || _maxBytesPerSecond <= 0)
            {
                return;
            }

            RefillLocked(Stopwatch.GetTimestamp());
            _availableTokens -= byteCount;

            if (_availableTokens >= 0)
            {
                return;
            }

            double waitSeconds = -_availableTokens / _maxBytesPerSecond;
            delay = TimeSpan.FromSeconds(waitSeconds);
            changeToken = _limitChangedCts.Token;
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, changeToken);
        try
        {
            await Task.Delay(delay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Limit changed dynamically at runtime; return promptly to adopt the new rate.
        }
    }

    private void RefillLocked(long nowTimestamp)
    {
        long elapsedTicks = nowTimestamp - _lastRefillTimestamp;
        if (elapsedTicks <= 0)
        {
            return;
        }

        _lastRefillTimestamp = nowTimestamp;
        if (_maxBytesPerSecond <= 0)
        {
            return;
        }

        double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        double maxBurst = ComputeBurstCapacity(_maxBytesPerSecond);
        _availableTokens = Math.Min(maxBurst, _availableTokens + elapsedSeconds * _maxBytesPerSecond);
    }

    private static double ComputeBurstCapacity(long bytesPerSecond)
        => Math.Max(8192.0, bytesPerSecond * 0.15);

    private static double ComputeInitialTokens(long bytesPerSecond)
        => Math.Min(16384.0, Math.Max(4096.0, bytesPerSecond * 0.05));

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cts = _limitChangedCts;
        }

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }
    }
}
