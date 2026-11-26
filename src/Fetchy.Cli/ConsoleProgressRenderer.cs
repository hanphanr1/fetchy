using System.Diagnostics;
using System.Globalization;
using Fetchy.Core.Engine;

namespace Fetchy.Cli;

/// <summary>
/// Renders live single-line TTY download progress or periodic non-TTY log lines when stdout is redirected.
/// </summary>
internal sealed class ConsoleProgressRenderer : IProgress<DownloadProgressSnapshot>
{
    private readonly bool _isInteractive;
    private readonly object _sync = new();
    private long _lastRenderTimestamp;
    private int _lastLineLength;

    public ConsoleProgressRenderer()
    {
        _isInteractive = !Console.IsOutputRedirected;
    }

    public void Report(DownloadProgressSnapshot snapshot)
    {
        lock (_sync)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedMs = (now - _lastRenderTimestamp) * 1000.0 / Stopwatch.Frequency;
            bool isDone = snapshot.TotalBytes.HasValue && snapshot.DownloadedBytes >= snapshot.TotalBytes.Value;

            double minIntervalMs = _isInteractive ? 80.0 : 500.0;
            if (!isDone && _lastRenderTimestamp != 0 && elapsedMs < minIntervalMs)
            {
                return;
            }

            _lastRenderTimestamp = now;
            string line = FormatLine(snapshot);

            if (_isInteractive)
            {
                string padded = line.Length < _lastLineLength
                    ? line + new string(' ', _lastLineLength - line.Length)
                    : line;
                _lastLineLength = line.Length;
                Console.Write('\r' + padded);
            }
            else
            {
                Console.WriteLine(line);
            }
        }
    }

    public void CompleteLine()
    {
        if (_isInteractive && _lastLineLength > 0)
        {
            Console.WriteLine();
            _lastLineLength = 0;
        }
    }

    public static string FormatBytes(long bytes)
    {
        const double KiB = 1024.0;
        const double MiB = KiB * 1024.0;
        const double GiB = MiB * 1024.0;

        return bytes switch
        {
            >= (long)GiB => string.Create(CultureInfo.InvariantCulture, $"{bytes / GiB:F2} GiB"),
            >= (long)MiB => string.Create(CultureInfo.InvariantCulture, $"{bytes / MiB:F2} MiB"),
            >= (long)KiB => string.Create(CultureInfo.InvariantCulture, $"{bytes / KiB:F1} KiB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
        };
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        const double KiB = 1024.0;
        const double MiB = KiB * 1024.0;

        return bytesPerSecond switch
        {
            >= MiB => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / MiB:F2} MB/s"),
            >= KiB => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / KiB:F1} KB/s"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond:F0} B/s")
        };
    }

    private static string FormatLine(DownloadProgressSnapshot s)
    {
        string pct = s.Percentage.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{s.Percentage.Value,5:F1}%")
            : "  --.-%";

        string progressBar = BuildBar(s.Percentage, 20);
        string transferred = s.TotalBytes.HasValue
            ? $"{FormatBytes(s.DownloadedBytes)} / {FormatBytes(s.TotalBytes.Value)}"
            : FormatBytes(s.DownloadedBytes);

        string speed = FormatSpeed(s.SmoothedSpeedBps > 0 ? s.SmoothedSpeedBps : s.InstantSpeedBps);
        string eta = s.Eta.HasValue ? FormatEta(s.Eta.Value) : "--:--";

        return $"[{progressBar}] {pct} | {transferred} | {speed} | ETA {eta} | Conn {s.ActiveConnections} | Steals {s.WorkStealCount}";
    }

    private static string BuildBar(double? percentage, int width)
    {
        if (!percentage.HasValue)
        {
            return new string('.', width);
        }

        int filled = (int)Math.Clamp(Math.Round(percentage.Value / 100.0 * width), 0, width);
        return new string('#', filled) + new string('-', width - filled);
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours:D2}:{eta.Minutes:D2}:{eta.Seconds:D2}";
        }

        return $"{eta.Minutes:D2}:{eta.Seconds:D2}";
    }
}
