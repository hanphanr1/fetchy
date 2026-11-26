using System.Net;

namespace Fetchy.Core.Engine;

/// <summary>
/// Governs transient error classification, <c>Retry-After</c> handling, exponential backoff,
/// and adaptive concurrency step-down under throttling or service degradation.
/// </summary>
public sealed class RetryPolicy
{
    public int MaxRetriesPerSegment { get; init; } = 8;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StepDownCooldown { get; init; } = TimeSpan.FromMilliseconds(300);

    public static RetryPolicy Default { get; } = new();

    public bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.RequestTimeout;
    }

    public bool ShouldStepDownConcurrency(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout;
    }

    public bool IsTransientException(Exception exception)
    {
        return exception is HttpRequestException
            or IOException
            or TimeoutException;
    }

    /// <summary>
    /// Computes the delay before retrying a failed segment request, honoring <c>Retry-After</c> when present.
    /// </summary>
    public TimeSpan ComputeDelay(int attemptNumber, HttpResponseMessage? response = null)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta.HasValue)
            {
                return ClampDelay(retryAfter.Delta.Value);
            }

            if (retryAfter.Date.HasValue)
            {
                TimeSpan delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return ClampDelay(delta);
            }
        }

        int exponent = Math.Clamp(attemptNumber - 1, 0, 10);
        double backoffMs = BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        double jitterFactor = 1.0 + Random.Shared.NextDouble() * 0.25;
        return ClampDelay(TimeSpan.FromMilliseconds(backoffMs * jitterFactor));
    }

    private TimeSpan ClampDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaxDelay ? MaxDelay : delay;
    }
}
