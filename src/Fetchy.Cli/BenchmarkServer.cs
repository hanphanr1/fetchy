using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fetchy.Cli;

/// <summary>
/// Self-contained loopback HTTP/1.1 Range server used by <c>fetchy benchmark</c>
/// with a deterministic payload and simulated per-connection rate limit.
/// </summary>
internal sealed class BenchmarkServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _payload;
    private readonly long _perConnectionBytesPerSecond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _rangeRequestCounter;

    public Uri EndpointUri { get; }
    public string ExpectedSha256 { get; }
    public long PayloadSizeBytes => _payload.LongLength;

    private BenchmarkServer(
        TcpListener listener,
        byte[] payload,
        string expectedSha256,
        long perConnectionBytesPerSecond)
    {
        _listener = listener;
        _payload = payload;
        ExpectedSha256 = expectedSha256;
        _perConnectionBytesPerSecond = perConnectionBytesPerSecond;

        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        EndpointUri = new Uri($"http://127.0.0.1:{port}/benchmark-payload.bin");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public static BenchmarkServer Start(int sizeMb, long perConnectionBytesPerSecond = 2_500_000)
    {
        int totalBytes = checked(sizeMb * 1024 * 1024);
        byte[] payload = GenerateDeterministicPayload(totalBytes);
        string sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 64);
        return new BenchmarkServer(listener, payload, sha256, perConnectionBytesPerSecond);
    }

    private static byte[] GenerateDeterministicPayload(int size)
    {
        byte[] data = new byte[size];
        uint state = 0x5EED1234u;
        for (int i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)(state & 0xFF);
        }

        return data;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
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
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

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
                    string? headerLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(headerLine))
                    {
                        break;
                    }

                    int colon = headerLine.IndexOf(':');
                    if (colon > 0)
                    {
                        string key = headerLine[..colon].Trim();
                        string val = headerLine[(colon + 1)..].Trim();
                        headers[key] = val;
                    }
                }

                string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    break;
                }

                string method = parts[0];
                bool closeAfter = headers.TryGetValue("Connection", out var connVal) &&
                                  connVal.Equals("close", StringComparison.OrdinalIgnoreCase);

                if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    string headResp =
                        "HTTP/1.1 200 OK\r\n" +
                        $"Content-Length: {_payload.Length}\r\n" +
                        "Accept-Ranges: bytes\r\n" +
                        "ETag: \"fetchy-bench-v1\"\r\n" +
                        "Content-Type: application/octet-stream\r\n" +
                        $"Connection: {(closeAfter ? "close" : "keep-alive")}\r\n\r\n";

                    byte[] headBytes = Encoding.ASCII.GetBytes(headResp);
                    await stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
                }
                else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    int start = 0;
                    int end = _payload.Length - 1;
                    bool isPartial = false;

                    if (headers.TryGetValue("Range", out var rangeHeader) &&
                        TryParseRange(rangeHeader, _payload.Length, out start, out end))
                    {
                        isPartial = true;
                    }

                    int length = end - start + 1;
                    string statusLine = isPartial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n";
                    string contentRange = isPartial
                        ? $"Content-Range: bytes {start}-{end}/{_payload.Length}\r\n"
                        : string.Empty;

                    string respHeaders =
                        statusLine +
                        $"Content-Length: {length}\r\n" +
                        contentRange +
                        "Accept-Ranges: bytes\r\n" +
                        "ETag: \"fetchy-bench-v1\"\r\n" +
                        "Content-Type: application/octet-stream\r\n" +
                        $"Connection: {(closeAfter ? "close" : "keep-alive")}\r\n\r\n";

                    byte[] headerBytes = Encoding.ASCII.GetBytes(respHeaders);
                    await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);

                    // Simulate realistic per-connection rate limit; on partial multi-segment requests
                    // introduce mild variance across streams so dynamic work-stealing is exercised naturally.
                    long effectiveRate = _perConnectionBytesPerSecond;
                    if (isPartial && length < _payload.Length)
                    {
                        int reqIndex = Interlocked.Increment(ref _rangeRequestCounter);
                        effectiveRate = (reqIndex % 4) switch
                        {
                            1 => (long)(_perConnectionBytesPerSecond * 0.45),
                            2 => (long)(_perConnectionBytesPerSecond * 1.20),
                            3 => (long)(_perConnectionBytesPerSecond * 1.15),
                            _ => (long)(_perConnectionBytesPerSecond * 1.20)
                        };
                    }

                    try
                    {
                        await WriteRateLimitedBodyAsync(stream, start, length, effectiveRate, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Client closed early (e.g. after work-stealing truncated its range).
                        break;
                    }
                }
                else
                {
                    byte[] notAllowed = Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(notAllowed, ct).ConfigureAwait(false);
                    break;
                }

                if (closeAfter)
                {
                    break;
                }
            }
        }
    }

    private async Task WriteRateLimitedBodyAsync(
        NetworkStream stream,
        int offset,
        int length,
        long bytesPerSecond,
        CancellationToken ct)
    {
        const int chunkSize = 32 * 1024;
        long startTimestamp = Stopwatch.GetTimestamp();
        int sent = 0;

        while (sent < length)
        {
            int toSend = Math.Min(chunkSize, length - sent);
            await stream.WriteAsync(_payload.AsMemory(offset + sent, toSend), ct).ConfigureAwait(false);
            sent += toSend;

            if (bytesPerSecond > 0 && sent < length)
            {
                double expectedSeconds = (double)sent / bytesPerSecond;
                double actualSeconds = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                double remainingDelay = expectedSeconds - actualSeconds;
                if (remainingDelay > 0.002)
                {
                    await Task.Delay(TimeSpan.FromSeconds(remainingDelay), ct).ConfigureAwait(false);
                }
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

        string startStr = spec[..dash].Trim();
        string endStr = spec[(dash + 1)..].Trim();

        if (!int.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(endStr))
        {
            end = totalLength - 1;
        }
        else if (!int.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
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
