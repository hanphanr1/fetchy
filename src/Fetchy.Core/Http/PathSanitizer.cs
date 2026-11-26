using System.Text;

namespace Fetchy.Core.Http;

/// <summary>
/// Sanitizes filenames, parses Content-Disposition headers, and enforces safe output directory containment.
/// </summary>
public static class PathSanitizer
{
    public const string DefaultFileName = "download.bin";
    private const int MaxFileNameLength = 200;

    private static readonly HashSet<char> IllegalFileNameChars = new(
        Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']));

    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Sanitizes an untrusted filename by removing path separators, traversal segments,
    /// illegal/control characters, trailing dots/spaces, and renaming Windows reserved device names.
    /// </summary>
    public static string SanitizeFileName(string? rawName, string fallback = DefaultFileName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallback;
        }

        // Split on both POSIX and Windows separators and take the final meaningful segment
        // to neutralize directory traversal inputs like "../../evil.exe" or "sub/dir/file.bin".
        string[] segments = rawName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        string candidate = string.Empty;
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            string seg = segments[i].Trim();
            if (seg.Length > 0 && seg != "." && seg != "..")
            {
                candidate = seg;
                break;
            }
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return fallback;
        }

        // Remove any remaining ".." sequences inside the candidate and strip illegal/control chars.
        var sb = new StringBuilder(candidate.Length);
        foreach (char c in candidate)
        {
            if (char.IsControl(c) || IllegalFileNameChars.Contains(c))
            {
                continue;
            }

            sb.Append(c);
        }

        string cleaned = sb.ToString();
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", "_", StringComparison.Ordinal);
        }

        // Windows strips trailing spaces and dots from filenames.
        cleaned = cleaned.Trim().TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "." || cleaned == "..")
        {
            return fallback;
        }

        // Check Windows reserved device names against the stem before the first dot
        // (Windows treats CON, CON.txt, AUX.tar.gz, etc. as reserved device names).
        int firstDot = cleaned.IndexOf('.');
        string rootName = firstDot >= 0 ? cleaned[..firstDot] : cleaned;
        if (WindowsReservedDeviceNames.Contains(rootName.Trim()))
        {
            cleaned = "_" + cleaned;
        }

        // Enforce reasonable length limit while preserving extension where possible.
        if (cleaned.Length > MaxFileNameLength)
        {
            string ext = Path.GetExtension(cleaned);
            if (ext.Length > 0 && ext.Length < 32)
            {
                int stemLen = MaxFileNameLength - ext.Length;
                cleaned = cleaned[..stemLen].TrimEnd('.', ' ') + ext;
            }
            else
            {
                cleaned = cleaned[..MaxFileNameLength].TrimEnd('.', ' ');
            }
        }

        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    /// <summary>
    /// Resolves a sanitized filename inside <paramref name="outputDirectory"/> and verifies strict directory containment.
    /// </summary>
    public static string ResolveSafeDestinationPath(string outputDirectory, string? untrustedFileName)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory must not be empty.", nameof(outputDirectory));
        }

        string sanitizedName = SanitizeFileName(untrustedFileName);
        string fullDirectory = Path.GetFullPath(outputDirectory);
        string combinedPath = Path.GetFullPath(Path.Combine(fullDirectory, sanitizedName));

        EnsurePathContainedInDirectory(fullDirectory, combinedPath);
        return combinedPath;
    }

    /// <summary>
    /// Validates that <paramref name="candidatePath"/> resolves strictly inside <paramref name="outputDirectory"/>.
    /// </summary>
    public static void EnsurePathContainedInDirectory(string outputDirectory, string candidatePath)
    {
        string fullDirectory = Path.GetFullPath(outputDirectory);
        string directoryPrefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;

        string fullCandidate = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullCandidate.StartsWith(directoryPrefix, comparison))
        {
            throw new UnauthorizedAccessException(
                $"Destination path '{fullCandidate}' escapes output directory '{fullDirectory}'.");
        }
    }

    /// <summary>
    /// Extracts a sanitized filename from a Content-Disposition header value, preferring RFC 5987 filename* over filename.
    /// </summary>
    public static string? ExtractFileNameFromContentDisposition(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        string? extendedValue = null;
        string? plainValue = null;

        foreach (string part in SplitHeaderParameters(headerValue))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = part[..eq].Trim();
            string rawVal = part[(eq + 1)..].Trim();

            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                extendedValue = DecodeRfc5987Value(rawVal);
            }
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                plainValue = UnquoteHeaderValue(rawVal);
            }
        }

        string? chosen = !string.IsNullOrWhiteSpace(extendedValue) ? extendedValue : plainValue;
        return string.IsNullOrWhiteSpace(chosen) ? null : SanitizeFileName(chosen);
    }

    /// <summary>
    /// Extracts a sanitized filename from the path component of a URI.
    /// </summary>
    public static string ExtractFileNameFromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return DefaultFileName;
        }

        string trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        string segment = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;

        try
        {
            segment = Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            // Keep raw segment if percent-encoding is malformed.
        }

        return SanitizeFileName(segment);
    }

    /// <summary>
    /// Returns <paramref name="desiredPath"/> if overwrite is allowed or it does not exist;
    /// otherwise generates an auto-disambiguated path such as "name (1).ext".
    /// </summary>
    public static string GetUniqueDestinationPath(string desiredPath, bool overwrite)
    {
        if (overwrite || !File.Exists(desiredPath))
        {
            return desiredPath;
        }

        string directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(desiredPath);
        string extension = Path.GetExtension(desiredPath);

        for (int counter = 1; counter < int.MaxValue; counter++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({counter}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static List<string> SplitHeaderParameters(string headerValue)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool escaped = false;

        foreach (char c in headerValue)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\' && inQuotes)
            {
                current.Append(c);
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
                continue;
            }

            if (c == ';' && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string UnquoteHeaderValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var sb = new StringBuilder(value.Length - 2);
            for (int i = 1; i < value.Length - 1; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length - 1)
                {
                    i++;
                }

                sb.Append(value[i]);
            }

            return sb.ToString();
        }

        return value;
    }

    private static string? DecodeRfc5987Value(string raw)
    {
        string unquoted = UnquoteHeaderValue(raw).Trim();
        int firstQuote = unquoted.IndexOf('\'');
        if (firstQuote < 0)
        {
            try
            {
                return Uri.UnescapeDataString(unquoted);
            }
            catch (UriFormatException)
            {
                return unquoted;
            }
        }

        int secondQuote = unquoted.IndexOf('\'', firstQuote + 1);
        if (secondQuote < 0)
        {
            return null;
        }

        string charset = unquoted[..firstQuote].Trim();
        string encoded = unquoted[(secondQuote + 1)..];

        var bytes = new List<byte>(encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length &&
                TryParseHexByte(encoded[i + 1], encoded[i + 2], out byte b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.Add((byte)encoded[i]);
            }
        }

        Encoding encoding = charset.Equals("ISO-8859-1", StringComparison.OrdinalIgnoreCase)
            ? Encoding.Latin1
            : Encoding.UTF8;

        return encoding.GetString(bytes.ToArray());
    }

    private static bool TryParseHexByte(char hi, char lo, out byte result)
    {
        int h = HexDigit(hi);
        int l = HexDigit(lo);
        if (h < 0 || l < 0)
        {
            result = 0;
            return false;
        }

        result = (byte)((h << 4) | l);
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
