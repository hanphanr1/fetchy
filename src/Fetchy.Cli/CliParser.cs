using System.Globalization;
using Fetchy.Core.Http;

namespace Fetchy.Cli;

internal abstract record CliCommand;

internal sealed record HelpCommand : CliCommand;

internal sealed record VersionCommand : CliCommand;

internal sealed record DownloadCommand(
    Uri Url,
    string OutputPath,
    int Connections,
    long MaxBytesPerSecond,
    string? ExpectedSha256,
    string? ExpectedMd5,
    string? Referer,
    string? UserAgent,
    bool Overwrite) : CliCommand;

internal sealed record ResumeCommand(
    string StateFilePath,
    Uri? RefreshedUrl,
    int Connections,
    long MaxBytesPerSecond,
    bool Overwrite) : CliCommand;

internal sealed record VerifyCommand(
    string FilePath,
    string? ExpectedSha256,
    string? ExpectedMd5) : CliCommand;

internal sealed record BenchmarkOptionsCommand(
    int SizeMb,
    int Connections) : CliCommand;

internal sealed class CliParseException : Exception
{
    public CliParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// Pure BCL command-line argument and byte-rate parser for the Fetchy CLI.
/// </summary>
internal static class CliParser
{
    public static CliCommand Parse(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return new HelpCommand();
        }

        string first = args[0].Trim();
        if (first is "-h" or "--help" or "help")
        {
            return new HelpCommand();
        }

        if (first is "-v" or "--version" or "version")
        {
            return new VersionCommand();
        }

        string[] rest = args[1..];
        return first.ToLowerInvariant() switch
        {
            "download" => ParseDownload(rest),
            "resume" => ParseResume(rest),
            "verify" => ParseVerify(rest),
            "benchmark" => ParseBenchmark(rest),
            _ => throw new CliParseException($"Unknown command '{first}'. Run 'fetchy --help' for usage.")
        };
    }

    private static DownloadCommand ParseDownload(string[] args)
    {
        string? rawUrl = null;
        string output = Directory.GetCurrentDirectory();
        int connections = 16;
        long maxBytesPerSecond = 0;
        string? sha256 = null;
        string? md5 = null;
        string? referer = null;
        string? userAgent = null;
        bool overwrite = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    throw new CliParseException(GetUsageText());
                case "-o" or "--output":
                    output = RequireValue(args, ref i, arg);
                    break;
                case "-c" or "--connections":
                    connections = ParseConnections(RequireValue(args, ref i, arg));
                    break;
                case "--limit":
                    maxBytesPerSecond = ParseByteRate(RequireValue(args, ref i, arg));
                    break;
                case "--sha256":
                    sha256 = RequireValue(args, ref i, arg);
                    break;
                case "--md5":
                    md5 = RequireValue(args, ref i, arg);
                    break;
                case "--referer":
                    referer = RequireValue(args, ref i, arg);
                    break;
                case "--ua" or "--user-agent":
                    userAgent = RequireValue(args, ref i, arg);
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new CliParseException($"Unknown option '{arg}' for download command.");
                    }

                    if (rawUrl is not null)
                    {
                        throw new CliParseException($"Unexpected positional argument '{arg}'.");
                    }

                    rawUrl = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new CliParseException("Missing required <url> argument for 'download'.");
        }

        Uri validatedUri;
        try
        {
            validatedUri = UriValidator.ValidateHttpUri(rawUrl);
        }
        catch (ArgumentException ex)
        {
            throw new CliParseException(ex.Message);
        }

        return new DownloadCommand(
            validatedUri,
            output,
            connections,
            maxBytesPerSecond,
            sha256,
            md5,
            referer,
            userAgent,
            overwrite);
    }

    private static ResumeCommand ParseResume(string[] args)
    {
        string? stateFile = null;
        Uri? refreshedUrl = null;
        int connections = 16;
        long maxBytesPerSecond = 0;
        bool overwrite = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--url":
                    string rawNewUrl = RequireValue(args, ref i, arg);
                    try
                    {
                        refreshedUrl = UriValidator.ValidateHttpUri(rawNewUrl);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new CliParseException(ex.Message);
                    }
                    break;
                case "-c" or "--connections":
                    connections = ParseConnections(RequireValue(args, ref i, arg));
                    break;
                case "--limit":
                    maxBytesPerSecond = ParseByteRate(RequireValue(args, ref i, arg));
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new CliParseException($"Unknown option '{arg}' for resume command.");
                    }

                    if (stateFile is not null)
                    {
                        throw new CliParseException($"Unexpected positional argument '{arg}'.");
                    }

                    stateFile = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(stateFile))
        {
            throw new CliParseException("Missing required <state-file> argument for 'resume'.");
        }

        return new ResumeCommand(stateFile, refreshedUrl, connections, maxBytesPerSecond, overwrite);
    }

    private static VerifyCommand ParseVerify(string[] args)
    {
        string? filePath = null;
        string? sha256 = null;
        string? md5 = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--sha256":
                    sha256 = RequireValue(args, ref i, arg);
                    break;
                case "--md5":
                    md5 = RequireValue(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new CliParseException($"Unknown option '{arg}' for verify command.");
                    }

                    if (filePath is not null)
                    {
                        throw new CliParseException($"Unexpected positional argument '{arg}'.");
                    }

                    filePath = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new CliParseException("Missing required <file> argument for 'verify'.");
        }

        if (string.IsNullOrWhiteSpace(sha256) && string.IsNullOrWhiteSpace(md5))
        {
            throw new CliParseException("Specify at least one of --sha256 <hex> or --md5 <hex>.");
        }

        return new VerifyCommand(filePath, sha256, md5);
    }

    private static BenchmarkOptionsCommand ParseBenchmark(string[] args)
    {
        int sizeMb = 16;
        int connections = 16;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--size-mb":
                    string rawSize = RequireValue(args, ref i, arg);
                    if (!int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out sizeMb) ||
                        sizeMb < 1 || sizeMb > 1024)
                    {
                        throw new CliParseException("--size-mb must be an integer between 1 and 1024.");
                    }
                    break;
                case "-c" or "--connections":
                    connections = ParseConnections(RequireValue(args, ref i, arg));
                    break;
                default:
                    throw new CliParseException($"Unknown option '{arg}' for benchmark command.");
            }
        }

        return new BenchmarkOptionsCommand(sizeMb, connections);
    }

    /// <summary>
    /// Parses human-readable byte rates such as <c>5MB/s</c>, <c>500KB/s</c>, <c>10M</c>, <c>1.5G</c>, or raw bytes/s.
    /// </summary>
    public static long ParseByteRate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new CliParseException("Speed limit value must not be empty.");
        }

        string trimmed = raw.Trim();
        if (trimmed.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].Trim();
        }
        else if (trimmed.EndsWith("ps", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].Trim();
        }

        int splitIndex = 0;
        while (splitIndex < trimmed.Length && (char.IsDigit(trimmed[splitIndex]) || trimmed[splitIndex] == '.'))
        {
            splitIndex++;
        }

        if (splitIndex == 0)
        {
            throw new CliParseException($"Invalid speed limit '{raw}'. Examples: 5MB/s, 500KB/s, 10M.");
        }

        string numberPart = trimmed[..splitIndex];
        string unitPart = trimmed[splitIndex..].Trim().ToUpperInvariant();

        if (!double.TryParse(numberPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) ||
            double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new CliParseException($"Invalid numeric value in speed limit '{raw}'.");
        }

        double multiplier = unitPart switch
        {
            "" or "B" => 1.0,
            "K" or "KB" or "KIB" => 1024.0,
            "M" or "MB" or "MIB" => 1024.0 * 1024.0,
            "G" or "GB" or "GIB" => 1024.0 * 1024.0 * 1024.0,
            _ => throw new CliParseException($"Unknown speed unit '{unitPart}' in '{raw}'. Supported: B, K/KB, M/MB, G/GB.")
        };

        double bytesPerSecond = value * multiplier;
        if (bytesPerSecond > long.MaxValue)
        {
            throw new CliParseException($"Speed limit '{raw}' is too large.");
        }

        return (long)Math.Round(bytesPerSecond);
    }

    private static int ParseConnections(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
            value < 1 || value > 32)
        {
            throw new CliParseException("Connections (-c / --connections) must be an integer between 1 and 32.");
        }

        return value;
    }

    private static string RequireValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new CliParseException($"Option '{optionName}' requires a value.");
        }

        return args[++index];
    }

    public static string GetUsageText() => """
        Fetchy - High-Performance Multi-Segment HTTP Download Accelerator

        Usage:
          fetchy download <url> [options]
          fetchy resume <state-file> [options]
          fetchy verify <file> --sha256 <hex> | --md5 <hex>
          fetchy benchmark [--size-mb <1..1024>] [--connections <1..32>]
          fetchy --version | --help

        Download options:
          -o, --output <dir|file>      Destination directory or file path (default: current directory)
          -c, --connections <1..32>    Max concurrent connections (default: 16)
              --limit <rate>           Bandwidth limit (e.g. 5MB/s, 500KB/s, 10M; 0 = unlimited)
              --sha256 <hex>           Verify SHA-256 digest after download
              --md5 <hex>              Verify MD5 digest after download
              --referer <url>          HTTP Referer header
              --ua <agent>             HTTP User-Agent header
              --overwrite              Overwrite destination file if it already exists

        Resume options:
              --url <refreshed-url>    Replace expired download URL while keeping segment progress
          -c, --connections <1..32>    Max concurrent connections (default: 16)
              --limit <rate>           Bandwidth limit (e.g. 5MB/s, 500KB/s, 10M)
              --overwrite              Overwrite destination file if it already exists

        Benchmark options:
              --size-mb <N>            Deterministic payload size in MiB (default: 16)
          -c, --connections <1..32>    Multi-segment connection count (default: 16)
        """;
}
