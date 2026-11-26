using System.Net;

namespace Fetchy.Core.Http;

/// <summary>
/// Creates configured <see cref="HttpClient"/> instances and protocol-negotiating HTTP requests.
/// </summary>
public static class HttpClientFactory
{
    public const string DefaultUserAgent = "Fetchy/1.0";

    /// <summary>
    /// Creates an <see cref="HttpClient"/> backed by <see cref="SocketsHttpHandler"/> tuned for high-throughput range downloads.
    /// </summary>
    public static HttpClient CreateClient(TimeSpan? requestTimeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            EnableMultipleHttp2Connections = true
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = requestTimeout ?? Timeout.InfiniteTimeSpan
        };

        return client;
    }

    /// <summary>
    /// Creates an <see cref="HttpRequestMessage"/> supporting HTTP/3 with graceful fallback to HTTP/2 and HTTP/1.1 on HTTPS,
    /// and HTTP/1.1 on cleartext HTTP endpoints.
    /// </summary>
    public static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Uri validated = UriValidator.ValidateHttpUri(uri);

        var request = new HttpRequestMessage(method, validated)
        {
            Version = string.Equals(validated.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? HttpVersion.Version30
                : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        bool hasUserAgent = false;
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                {
                    continue;
                }

                if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    hasUserAgent = true;
                }

                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        if (!hasUserAgent)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        }

        return request;
    }
}
