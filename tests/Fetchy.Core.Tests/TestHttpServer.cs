using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fetchy.Core.Tests;

internal sealed record RecordedRequest(
    string Method,
    string Path,
    string? RangeHeader,
    long BytesSent,
    int StatusCode);

/// <summary>
/// Self-contained embedded loopback HTTP/1.1 test server supporting byte ranges, chunked encoding,
/// HEAD 405 fallback, per-request throttling, fault injection (429/503/TCP drop), and request recording.
/// </summary>
internal sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private volatile byte[] _payload;
    private volatile string? _etag;
    private DateTimeOffset? _lastModified;
    private int _remaining429;
    private int _remaining503;
    private int _remainingMidStreamDrops;

    public int Port { get; }
    public Uri BaseUri { get; }
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public bool SupportRanges { get; set; } = true;
    public bool SendAcceptRangesHeader { get; set; } = true;
    public bool OmitContentLength { get; set; }
    public bool UseChunkedEncoding { get; set; }
    public bool RejectHeadWith405 { get; set; }
    public string? ContentDisposition { get; set; }
    public string RetryAfterHeaderValue { get; set; } = "0";
    public bool ApplyFaultsOnlyToGetRange { get; set; } = true;

    /// <summary>
    /// Optional per-request bandwidth limit in bytes/sec given (path, rangeStart, rangeEnd). Return 0 for unlimited.
    /// </summary>
    public Func<string, long, long, long>? PerRequestBytesPerSecond { get; set; }

    /// <summary>
    /// Optional delay before sending headers/body for a request.
    /// </summary>
    public Func<string, string, long, TimeSpan>? PerRequestStartDelay { get; set; }

    public byte[] Payload => _payload;
    public string PayloadSha256 => ComputeSha256(_payload);
    public string PayloadMd5 => ComputeMd5(_payload);

    public TestHttpServer(byte[] payload, string? etag = "\"test-etag-v1\"", DateTimeOffset? lastModified = null)
    {
        _payload = payload;
        _etag = etag;
        _lastModified = lastModified;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 64);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{Port}/");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public Uri GetUri(string relativePath = "file.bin") => new(BaseUri, relativePath.TrimStart('/'));

    public void UpdatePayload(byte[] newPayload, string? newEtag, DateTimeOffset? newLastModified = null)
    {
        _payload = newPayload;
        _etag = newEtag;
        _lastModified = newLastModified;
    }

    public void InjectFaults(int count429 = 0, int count503 = 0, int midStreamTcpDrops = 0, string retryAfter = "0")
    {
        Interlocked.Exchange(ref _remaining429, count429);
        Interlocked.Exchange(ref _remaining503, count503);
        Interlocked.Exchange(ref _remainingMidStreamDrops, midStreamTcpDrops);
        RetryAfterHeaderValue = retryAfter;
    }

    public static byte[] CreateDeterministicPayload(int size, uint seed = 0x13579BDFu)
    {
        byte[] data = new byte[size];
        uint x = seed;
        for (int i = 0; i < data.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            data[i] = (byte)(x & 0xFF);
        }

        return data;
    }

    public static string ComputeSha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string ComputeMd5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            while (!ct.IsCancellationRequested)
            {
                string? requestLine;
                try
                {
                    requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (string.IsNullOrEmpty(requestLine))
                {
                    break;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }

                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    break;
                }

                string method = parts[0];
                string path = parts[1];
                headers.TryGetValue("Range", out string? rangeHeader);
                bool closeConn = headers.TryGetValue("Connection", out var conn) &&
                                 conn.Equals("close", StringComparison.OrdinalIgnoreCase);

                byte[] payload = _payload;
                string? etag = _etag;
                DateTimeOffset? lastMod = _lastModified;

                if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    if (RejectHeadWith405)
                    {
                        await WriteSimpleResponseAsync(stream, 405, "Method Not Allowed", closeConn, null, ct).ConfigureAwait(false);
                        Requests.Enqueue(new RecordedRequest(method, path, rangeHeader, 0, 405));
                        if (closeConn) break;
                        continue;
                    }

                    var sb = new StringBuilder();
                    sb.Append("HTTP/1.1 200 OK\r\n");
                    if (!OmitContentLength)
                    {
                        sb.Append($"Content-Length: {payload.Length}\r\n");
                    }

                    if (SupportRanges && SendAcceptRangesHeader)
                    {
                        sb.Append("Accept-Ranges: bytes\r\n");
                    }
                    else if (!SupportRanges)
                    {
                        sb.Append("Accept-Ranges: none\r\n");
                    }

                    AppendCommonHeaders(sb, etag, lastMod);
                    sb.Append($"Connection: {(closeConn ? "close" : "keep-alive")}\r\n\r\n");

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
                    Requests.Enqueue(new RecordedRequest(method, path, rangeHeader, 0, 200));
                    if (closeConn) break;
                    continue;
                }

                if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteSimpleResponseAsync(stream, 405, "Method Not Allowed", true, null, ct).ConfigureAwait(false);
                    Requests.Enqueue(new RecordedRequest(method, path, rangeHeader, 0, 405));
                    break;
                }

                bool isProbeRange = string.Equals(rangeHeader, "bytes=0-0", StringComparison.OrdinalIgnoreCase);
                bool eligibleForFault = !isProbeRange && (!ApplyFaultsOnlyToGetRange || !string.IsNullOrEmpty(rangeHeader));

                if (eligibleForFault && TryDecrementPositive(ref _remaining429))
                {
                    await WriteSimpleResponseAsync(stream, 429, "Too Many Requests", closeConn, $"Retry-After: {RetryAfterHeaderValue}\r\n", ct).ConfigureAwait(false);
                    Requests.Enqueue(new RecordedRequest(method, path, rangeHeader, 0, 429));
                    if (closeConn) break;
                    continue;
                }

                if (eligibleForFault && TryDecrementPositive(ref _remaining503))
                {
                    await WriteSimpleResponseAsync(stream, 503, "Service Unavailable", closeConn, "Retry-After: 0\r\n", ct).ConfigureAwait(false);
                    Requests.Enqueue(new RecordedRequest(method, path, rangeHeader, 0, 503));
                    if (closeConn) break;
                    continue;
                }

                int start = 0;
                int end = payload.Length - 1;
                bool isPartial = false;

                if (SupportRanges && !string.IsNullOrEmpty(rangeHeader) &&
                    TryParseRange(rangeHeader, payload.Length, out int parsedStart, out int parsedEnd))
                {
                    start = parsedStart;
                    end = parsedEnd;
                    isPartial = true;
                }

                if (PerRequestStartDelay is not null && !isProbeRange)
                {
                    TimeSpan startDelay = PerRequestStartDelay(method, path, start);
                    if (startDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(startDelay, ct).ConfigureAwait(false);
                    }
                }

                int length = payload.Length == 0 ? 0 : (end - start + 1);
                bool shouldDropMidStream = eligibleForFault && length >= 4096 && TryDecrementPositive(ref _remainingMidStreamDrops);
                bool forceClose = closeConn || shouldDropMidStream || (OmitContentLength && !UseChunkedEncoding);

                var resp = new StringBuilder();
                int statusCode = isPartial ? 206 : 200;
                resp.Append(isPartial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");

                if (isPartial)
                {
                    resp.Append($"Content-Range: bytes {start}-{end}/{payload.Length}\r\n");
                    resp.Append($"Content-Length: {length}\r\n");
                }
                else if (UseChunkedEncoding)
                {
                    resp.Append("Transfer-Encoding: chunked\r\n");
                }
                else if (!OmitContentLength)
                {
                    resp.Append($"Content-Length: {length}\r\n");
                }

                if (SupportRanges && SendAcceptRangesHeader)
                {
                    resp.Append("Accept-Ranges: bytes\r\n");
                }

                AppendCommonHeaders(resp, etag, lastMod);
                resp.Append($"Connection: {(forceClose ? "close" : "keep-alive")}\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(resp.ToString()), ct).ConfigureAwait(false);

                long bytesPerSecond = (!isProbeRange && PerRequestBytesPerSecond is not null)
                    ? PerRequestBytesPerSecond(path, start, end)
                    : 0;

                int sendLimit = shouldDropMidStream ? Math.Max(1024, length / 3) : length;
                long sent = 0;

                try
                {
                    if (!isPartial && UseChunkedEncoding)
                    {
                        sent = await WriteChunkedBodyAsync(stream, payload, start, sendLimit, bytesPerSecond, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        sent = await WriteBodyAsync(stream, payload, start, sendLimit, bytesPerSecond, ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    Requests.Enqueue(new RecordedRequest(method, path, rangeHeader, sent, statusCode));
                    break;
                }

                Requests.Enqueue(new RecordedRequest(method, path, rangeHeader, sent, statusCode));

                if (shouldDropMidStream)
                {
                    // Abruptly sever the TCP connection to simulate a mid-stream network drop.
                    try
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                    }
                    catch
                    {
                    }

                    break;
                }

                if (forceClose)
                {
                    break;
                }
            }
        }
    }

    private void AppendCommonHeaders(StringBuilder sb, string? etag, DateTimeOffset? lastMod)
    {
        sb.Append("Content-Type: application/octet-stream\r\n");
        if (!string.IsNullOrEmpty(etag))
        {
            sb.Append($"ETag: {etag}\r\n");
        }

        if (lastMod.HasValue)
        {
            sb.Append($"Last-Modified: {lastMod.Value.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)}\r\n");
        }

        if (!string.IsNullOrEmpty(ContentDisposition))
        {
            sb.Append($"Content-Disposition: {ContentDisposition}\r\n");
        }
    }

    private static async Task WriteSimpleResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        bool closeConn,
        string? extraHeaders,
        CancellationToken ct)
    {
        string text =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            (extraHeaders ?? string.Empty) +
            "Content-Length: 0\r\n" +
            $"Connection: {(closeConn ? "close" : "keep-alive")}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
    }

    private static async Task<long> WriteBodyAsync(
        NetworkStream stream,
        byte[] payload,
        int offset,
        int length,
        long bytesPerSecond,
        CancellationToken ct)
    {
        int chunkSize = bytesPerSecond > 0 ? (int)Math.Clamp(bytesPerSecond / 20, 2048, 32 * 1024) : 64 * 1024;
        long startTs = Stopwatch.GetTimestamp();
        int sent = 0;

        while (sent < length)
        {
            int toSend = Math.Min(chunkSize, length - sent);
            await stream.WriteAsync(payload.AsMemory(offset + sent, toSend), ct).ConfigureAwait(false);
            sent += toSend;

            if (bytesPerSecond > 0 && sent < length)
            {
                double expectedSec = (double)sent / bytesPerSecond;
                double actualSec = (Stopwatch.GetTimestamp() - startTs) / (double)Stopwatch.Frequency;
                double delaySec = expectedSec - actualSec;
                if (delaySec > 0.002)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                }
            }
        }

        return sent;
    }

    private static async Task<long> WriteChunkedBodyAsync(
        NetworkStream stream,
        byte[] payload,
        int offset,
        int length,
        long bytesPerSecond,
        CancellationToken ct)
    {
        const int chunkSize = 16 * 1024;
        long sent = 0;
        long startTs = Stopwatch.GetTimestamp();

        while (sent < length)
        {
            int toSend = (int)Math.Min(chunkSize, length - sent);
            byte[] sizeHeader = Encoding.ASCII.GetBytes($"{toSend:X}\r\n");
            await stream.WriteAsync(sizeHeader, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload.AsMemory(offset + (int)sent, toSend), ct).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            sent += toSend;

            if (bytesPerSecond > 0 && sent < length)
            {
                double expectedSec = (double)sent / bytesPerSecond;
                double actualSec = (Stopwatch.GetTimestamp() - startTs) / (double)Stopwatch.Frequency;
                double delaySec = expectedSec - actualSec;
                if (delaySec > 0.002)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                }
            }
        }

        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        return sent;
    }

    private static bool TryDecrementPositive(ref int counter)
    {
        while (true)
        {
            int current = Volatile.Read(ref counter);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private static bool TryParseRange(string header, int totalLength, out int start, out int end)
    {
        start = 0;
        end = totalLength - 1;

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string spec = header[6..].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        if (!int.TryParse(spec[..dash].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0)
        {
            return false;
        }

        string endPart = spec[(dash + 1)..].Trim();
        if (string.IsNullOrEmpty(endPart))
        {
            end = totalLength - 1;
        }
        else if (!int.TryParse(endPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
        {
            return false;
        }

        if (start >= totalLength)
        {
            return false;
        }

        end = Math.Min(end, totalLength - 1);
        return start <= end;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        _cts.Dispose();
    }
}
