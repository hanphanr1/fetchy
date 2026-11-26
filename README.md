# Fetchy

A fast, native .NET 8 multi-segment HTTP download accelerator with dynamic work-stealing, zero-merge direct random-access disk I/O, and crash-safe atomic resume.

Built strictly on the .NET 8 Base Class Library (`SocketsHttpHandler`, `RandomAccess`, `ArrayPool<byte>`, `System.Text.Json`, `IncrementalHash`) with zero third-party HTTP or JSON dependencies.

---

## Features

- **Dynamic Work-Stealing Segmentation (`1–32` connections):** Whenever a worker finishes its byte range, the scheduler atomically splits the remaining tail of the largest active segment in half and assigns it to the idle worker. Fast connections continuously absorb slow tail segments without byte overlaps or gaps.
- **Zero-Merge Direct Disk I/O:** Pre-allocates the exact file size via `RandomAccess.SetLength` and writes chunks directly to their final byte offsets using `SafeFileHandle` + `RandomAccess.WriteAsync` and pooled 128 KiB buffers (`ArrayPool<byte>.Shared`). Eliminates post-download file merging stalls completely.
- **Protocol Support:** Standard HTTP/1.1, HTTP/2, and HTTP/3 (QUIC) with automatic fallback (`HttpVersionPolicy.RequestVersionOrLower`), `HEAD` probing with automatic `GET Range: bytes=0-0` fallback, and single-stream fallback when servers omit range support or `Content-Length`.
- **Crash-Safe Atomic Pause / Resume & URL Refresh:** Checkpoints exact committed segment offsets to `<dest>.fetchy.state` using same-directory temp files, `Flush(flushToDisk: true)`, and atomic `File.Replace` / `File.Move`. Validates `ETag`, `Last-Modified`, and `Content-Length` on resume, safely restarting from byte 0 if the remote file changed, and supports refreshing expired signed URLs without losing progress.
- **Adaptive Backoff & Token-Bucket Speed Limiter:** Honors `429 Too Many Requests` (`Retry-After` delta-seconds and HTTP-date), handles `503` and mid-stream TCP drops with adaptive concurrency step-down, and enforces a shared, runtime-adjustable token-bucket bandwidth cap.
- **Streaming Hash Verification:** Computes and verifies `SHA-256` and `MD5` digests using `IncrementalHash` and pooled buffers.
- **Queue Manager & Smart Categories:** Manages concurrent queues, automatic file categorization (`Programs`, `Compressed`, `Videos`, `Documents`, `Music`, `Other`), and atomic `downloads.json` catalog persistence.
- **Security by Default:** Accepts `http://` and `https://` URIs only, neutralizes directory traversal and Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1–COM9`, `LPT1–LPT9`), enforces output directory containment, and strips sensitive headers (`Cookie`, `Authorization`, `Proxy-Authorization`, `X-Api-Key`) before writing state or catalog files.

---

## Architecture Overview

```text
Fetchy.sln
├── src/
│   ├── Fetchy.Core/
│   │   ├── Http/          # UriValidator, PathSanitizer, HttpClientFactory, HttpProbeClient
│   │   ├── Engine/        # SegmentScheduler, SegmentedDownloader, BandwidthLimiter, RetryPolicy
│   │   ├── Storage/       # DirectDiskWriter (RandomAccess), DownloadStateStore (atomic checkpoints)
│   │   ├── Verification/  # HashVerifier (streaming SHA-256 / MD5)
│   │   └── Management/    # DownloadManager (queue + downloads.json), CategoryClassifier
│   └── Fetchy.Cli/        # Pure BCL CLI (download, resume, verify, benchmark)
└── tests/
    └── Fetchy.Core.Tests/ # Embedded loopback HTTP/1.1 integration & unit test suite
```

### How Work-Stealing Stays Overlap-Free

1. Each active segment tracks both `WrittenOffset` (committed to disk) and `ReservedOffset` (currently in flight to `RandomAccess.WriteAsync`) under a single scheduler lock.
2. When an idle worker steals from the active segment with the largest remaining tail, the split midpoint is always chosen at or beyond `ReservedOffset` (`Mid = max(WrittenOffset, ReservedOffset) + remaining / 2`).
3. The victim updates `End = Mid - 1` atomically and clamps any buffered read to its shortened `End`, closing its range stream cleanly once `WrittenOffset > End`.

---

## CLI Usage

### Download a file

```powershell
# Multi-segment download with 16 connections
fetchy download https://example.com/releases/archive.zip -o ./downloads -c 16

# Bandwidth-limited download with SHA-256 verification
fetchy download https://example.com/app.exe -o app.exe --limit 5MB/s --sha256 <expected-hex> --overwrite

# Custom Referer and User-Agent
fetchy download https://example.com/file.bin --referer https://example.com/ --ua "CustomAgent/1.0"
```

### Resume an interrupted download (with optional refreshed URL)

```powershell
fetchy resume ./downloads/archive.zip.fetchy.state
fetchy resume ./downloads/archive.zip.fetchy.state --url "https://mirror.example.com/archive.zip?token=fresh" --limit 10M
```

### Verify file integrity

```powershell
fetchy verify ./downloads/archive.zip --sha256 <expected-hex>
fetchy verify ./downloads/archive.zip --md5 <expected-hex>
```

### Run the local loopback benchmark

Starts an embedded local HTTP/1.1 Range server on `127.0.0.1` with a deterministic payload and per-connection rate pacing, compares 1-stream vs N-segment work-stealing downloads, and verifies bit-exact `SHA-256` output for both runs:

```powershell
fetchy benchmark --size-mb 16 --connections 16
```

---

## Build & Test

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer with the .NET 8 runtime installed).

```powershell
dotnet restore Fetchy.sln
dotnet build Fetchy.sln -c Release --no-restore
dotnet test Fetchy.sln -c Release --no-build --verbosity normal
```

Run the CLI directly via `dotnet run`:

```powershell
dotnet run --project src/Fetchy.Cli -c Release --no-build -- --help
dotnet run --project src/Fetchy.Cli -c Release --no-build -- benchmark --size-mb 16 --connections 16
```

---

## Roadmap

- **Milestone 1 (Complete):** Core dynamic work-stealing engine, zero-merge `RandomAccess` disk I/O, atomic pause/resume & URL refresh, adaptive backoff, token-bucket rate limiter, download queue manager, CLI, automated test suite, and Windows CI.
- **Milestone 2:** Native desktop UI with live per-segment visualization, queue controls, drag-and-drop URL capture, and system tray integration.
- **Milestone 3:** Browser extension native messaging bridge, scheduling rules, and proxy authentication profiles.

---

## License

MIT License — see [LICENSE](LICENSE).
