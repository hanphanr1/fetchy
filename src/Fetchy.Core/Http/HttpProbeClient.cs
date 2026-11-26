using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Fetchy.Core.Http;

/// <summary>
/// Metadata returned by probing a remote HTTP resource.
/// </summary>
public sealed record ProbeResult(
    Uri FinalUri,
    long? TotalBytes,
    bool SupportsRanges,
    string? ETag,
    DateTimeOffset? LastModified,
    string SuggestedFileName,
    string? ContentType);

/// <summary>
/// Probes remote HTTP/HTTPS resources via HEAD with automatic fallback to GET Range: bytes=0-0.
/// </summary>
public sealed class HttpProbeClient
{
    private readonly HttpClient _httpClient;

    public HttpProbeClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Probes <paramref name="uri"/> to determine content length, range support, validators, final redirect URI, and filename.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        Uri validated = UriValidator.ValidateHttpUri(uri);

        ProbeResult? headResult = null;
        bool needRangeProbe = false;

        try
        {
            using var headRequest = HttpClientFactory.CreateRequest(HttpMethod.Head, validated, headers);
            using var headResponse = await _httpClient.SendAsync(
                headRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode)
            {
                headResult = ExtractProbeResult(validated, headResponse, isRangeProbe: false);

                bool explicitlyNoRanges = HasAcceptRangesNone(headResponse);
                if (!headResult.SupportsRanges && !explicitlyNoRanges)
                {
                    // Server did not advertise Accept-Ranges: bytes on HEAD; verify with GET Range: bytes=0-0.
                    needRangeProbe = true;
                }
                else if (headResult.TotalBytes is null)
                {
                    needRangeProbe = true;
                }
            }
            else if (headResponse.StatusCode is HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.NotImplemented
                or HttpStatusCode.BadRequest
                or HttpStatusCode.Forbidden)
            {
                needRangeProbe = true;
            }
            else
            {
                headResponse.EnsureSuccessStatusCode();
            }
        }
        catch (HttpRequestException) when (headResult is null)
        {
            needRangeProbe = true;
        }

        if (!needRangeProbe && headResult is not null)
        {
            return headResult;
        }

        using var rangeRequest = HttpClientFactory.CreateRequest(HttpMethod.Get, validated, headers);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);

        using var rangeResponse = await _httpClient.SendAsync(
            rangeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (rangeResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Often occurs for 0-byte resources when Range: bytes=0-0 is requested.
            long? totalFromRange = TryExtractTotalFromContentRange(rangeResponse);
            if (headResult is not null)
            {
                return headResult with { TotalBytes = totalFromRange ?? headResult.TotalBytes };
            }

            return ExtractProbeResult(validated, rangeResponse, isRangeProbe: true) with
            {
                TotalBytes = totalFromRange ?? 0,
                SupportsRanges = false
            };
        }

        rangeResponse.EnsureSuccessStatusCode();
        var rangeResult = ExtractProbeResult(validated, rangeResponse, isRangeProbe: true);

        if (headResult is null)
        {
            return rangeResult;
        }

        // Merge best available metadata from HEAD and Range GET probes.
        return new ProbeResult(
            FinalUri: rangeResult.FinalUri ?? headResult.FinalUri,
            TotalBytes: rangeResult.TotalBytes ?? headResult.TotalBytes,
            SupportsRanges: rangeResult.SupportsRanges || headResult.SupportsRanges,
            ETag: rangeResult.ETag ?? headResult.ETag,
            LastModified: rangeResult.LastModified ?? headResult.LastModified,
            SuggestedFileName: !string.Equals(rangeResult.SuggestedFileName, PathSanitizer.DefaultFileName, StringComparison.Ordinal)
                ? rangeResult.SuggestedFileName
                : headResult.SuggestedFileName,
            ContentType: rangeResult.ContentType ?? headResult.ContentType);
    }

    private static ProbeResult ExtractProbeResult(Uri originalUri, HttpResponseMessage response, bool isRangeProbe)
    {
        Uri finalUri = response.RequestMessage?.RequestUri ?? originalUri;

        bool supportsRanges = false;
        long? totalBytes = null;

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            long? rangeTotal = TryExtractTotalFromContentRange(response);
            if (rangeTotal.HasValue || response.Content.Headers.ContentRange is not null)
            {
                supportsRanges = true;
                totalBytes = rangeTotal;
            }
        }
        else
        {
            if (response.Headers.AcceptRanges.Any(v => v.Equals("bytes", StringComparison.OrdinalIgnoreCase)))
            {
                supportsRanges = true;
            }

            if (response.Content.Headers.ContentLength.HasValue &&
                response.Content.Headers.ContentLength.Value >= 0)
            {
                // For a non-206 GET response, Content-Length is the full resource size.
                // For HEAD, Content-Length is also the full resource size.
                totalBytes = response.Content.Headers.ContentLength.Value;
            }
        }

        // If range probe got 200 OK, server ignored Range header.
        if (isRangeProbe && response.StatusCode == HttpStatusCode.OK)
        {
            supportsRanges = false;
        }

        string? etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag) &&
            response.Headers.TryGetValues("ETag", out var etagValues))
        {
            etag = etagValues.FirstOrDefault();
        }

        DateTimeOffset? lastModified = response.Content.Headers.LastModified;

        string? rawContentDisposition = null;
        if (response.Content.Headers.TryGetValues("Content-Disposition", out var cdValues))
        {
            rawContentDisposition = cdValues.FirstOrDefault();
        }

        string? fileName = PathSanitizer.ExtractFileNameFromContentDisposition(rawContentDisposition);
        fileName ??= PathSanitizer.ExtractFileNameFromUri(finalUri);

        string? contentType = response.Content.Headers.ContentType?.MediaType;

        return new ProbeResult(
            FinalUri: finalUri,
            TotalBytes: totalBytes,
            SupportsRanges: supportsRanges,
            ETag: etag,
            LastModified: lastModified,
            SuggestedFileName: fileName,
            ContentType: contentType);
    }

    private static bool HasAcceptRangesNone(HttpResponseMessage response)
    {
        return response.Headers.AcceptRanges.Any(v => v.Equals("none", StringComparison.OrdinalIgnoreCase));
    }

    private static long? TryExtractTotalFromContentRange(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentRange?.Length is long parsedLength && parsedLength >= 0)
        {
            return parsedLength;
        }

        if (response.Content.Headers.TryGetValues("Content-Range", out var values))
        {
            string? raw = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                int slash = raw.LastIndexOf('/');
                if (slash >= 0 && slash + 1 < raw.Length)
                {
                    string totalPart = raw[(slash + 1)..].Trim();
                    if (!totalPart.Equals("*", StringComparison.Ordinal) &&
                        long.TryParse(totalPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long total) &&
                        total >= 0)
                    {
                        return total;
                    }
                }
            }
        }

        return null;
    }
}
