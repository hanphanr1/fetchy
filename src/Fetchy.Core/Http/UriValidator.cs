namespace Fetchy.Core.Http;

/// <summary>
/// Validates download URIs and filters sensitive HTTP headers before persistence.
/// </summary>
public static class UriValidator
{
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie",
        "Set-Cookie",
        "Authorization",
        "Proxy-Authorization",
        "X-Api-Key",
        "Api-Key",
        "X-Auth-Token"
    };

    /// <summary>
    /// Validates and parses an absolute HTTP or HTTPS URI string, rejecting control characters and unsupported schemes.
    /// </summary>
    public static Uri ValidateHttpUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL must not be empty.", nameof(url));
        }

        ContainsNoControlCharacters(url, nameof(url));

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri is null)
        {
            throw new ArgumentException($"Invalid absolute URI: '{url}'.", nameof(url));
        }

        return ValidateHttpUri(uri);
    }

    /// <summary>
    /// Validates an existing <see cref="Uri"/> instance for HTTP/HTTPS scheme, valid host, and absence of control characters.
    /// </summary>
    public static Uri ValidateHttpUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("URI must be absolute.", nameof(uri));
        }

        ContainsNoControlCharacters(uri.OriginalString, nameof(uri));
        ContainsNoControlCharacters(uri.AbsoluteUri, nameof(uri));

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported URI scheme '{uri.Scheme}'. Only http and https are allowed.", nameof(uri));
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("URI must contain a valid host.", nameof(uri));
        }

        return uri;
    }

    /// <summary>
    /// Returns true if the given header name is considered sensitive and must never be persisted to disk.
    /// </summary>
    public static bool IsSensitiveHeader(string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return true;
        }

        return SensitiveHeaderNames.Contains(headerName.Trim());
    }

    /// <summary>
    /// Filters a dictionary of HTTP headers to remove all sensitive headers and any values containing control characters.
    /// </summary>
    public static Dictionary<string, string> FilterPersistableHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null || headers.Count == 0)
        {
            return result;
        }

        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            string trimmedKey = key.Trim();
            if (IsSensitiveHeader(trimmedKey))
            {
                continue;
            }

            if (HasControlCharacters(trimmedKey) || HasControlCharacters(value))
            {
                continue;
            }

            result[trimmedKey] = value;
        }

        return result;
    }

    private static void ContainsNoControlCharacters(string value, string paramName)
    {
        if (HasControlCharacters(value))
        {
            throw new ArgumentException("Value must not contain control characters.", paramName);
        }
    }

    private static bool HasControlCharacters(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
