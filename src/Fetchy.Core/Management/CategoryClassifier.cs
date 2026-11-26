namespace Fetchy.Core.Management;

/// <summary>
/// High-level category assigned automatically based on a downloaded file's extension.
/// </summary>
public enum DownloadCategory
{
    Programs,
    Compressed,
    Videos,
    Documents,
    Music,
    Other
}

/// <summary>
/// Classifies filenames into smart download categories by extension.
/// </summary>
public static class CategoryClassifier
{
    private static readonly Dictionary<string, DownloadCategory> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Programs
        [".exe"] = DownloadCategory.Programs,
        [".msi"] = DownloadCategory.Programs,
        [".msix"] = DownloadCategory.Programs,
        [".appx"] = DownloadCategory.Programs,
        [".dmg"] = DownloadCategory.Programs,
        [".pkg"] = DownloadCategory.Programs,
        [".deb"] = DownloadCategory.Programs,
        [".rpm"] = DownloadCategory.Programs,
        [".appimage"] = DownloadCategory.Programs,
        [".apk"] = DownloadCategory.Programs,
        [".bat"] = DownloadCategory.Programs,
        [".cmd"] = DownloadCategory.Programs,

        // Compressed
        [".zip"] = DownloadCategory.Compressed,
        [".7z"] = DownloadCategory.Compressed,
        [".rar"] = DownloadCategory.Compressed,
        [".tar"] = DownloadCategory.Compressed,
        [".gz"] = DownloadCategory.Compressed,
        [".tgz"] = DownloadCategory.Compressed,
        [".bz2"] = DownloadCategory.Compressed,
        [".xz"] = DownloadCategory.Compressed,
        [".zst"] = DownloadCategory.Compressed,
        [".iso"] = DownloadCategory.Compressed,
        [".cab"] = DownloadCategory.Compressed,

        // Videos
        [".mp4"] = DownloadCategory.Videos,
        [".mkv"] = DownloadCategory.Videos,
        [".webm"] = DownloadCategory.Videos,
        [".avi"] = DownloadCategory.Videos,
        [".mov"] = DownloadCategory.Videos,
        [".wmv"] = DownloadCategory.Videos,
        [".flv"] = DownloadCategory.Videos,
        [".m4v"] = DownloadCategory.Videos,
        [".ts"] = DownloadCategory.Videos,
        [".mpeg"] = DownloadCategory.Videos,
        [".mpg"] = DownloadCategory.Videos,

        // Documents
        [".pdf"] = DownloadCategory.Documents,
        [".doc"] = DownloadCategory.Documents,
        [".docx"] = DownloadCategory.Documents,
        [".xls"] = DownloadCategory.Documents,
        [".xlsx"] = DownloadCategory.Documents,
        [".ppt"] = DownloadCategory.Documents,
        [".pptx"] = DownloadCategory.Documents,
        [".txt"] = DownloadCategory.Documents,
        [".md"] = DownloadCategory.Documents,
        [".csv"] = DownloadCategory.Documents,
        [".rtf"] = DownloadCategory.Documents,
        [".epub"] = DownloadCategory.Documents,
        [".odt"] = DownloadCategory.Documents,

        // Music
        [".mp3"] = DownloadCategory.Music,
        [".flac"] = DownloadCategory.Music,
        [".wav"] = DownloadCategory.Music,
        [".aac"] = DownloadCategory.Music,
        [".ogg"] = DownloadCategory.Music,
        [".opus"] = DownloadCategory.Music,
        [".m4a"] = DownloadCategory.Music,
        [".wma"] = DownloadCategory.Music,
        [".aiff"] = DownloadCategory.Music
    };

    /// <summary>
    /// Classifies <paramref name="fileNameOrPath"/> into a <see cref="DownloadCategory"/>.
    /// </summary>
    public static DownloadCategory Classify(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return DownloadCategory.Other;
        }

        string extension = Path.GetExtension(fileNameOrPath);
        if (string.IsNullOrEmpty(extension))
        {
            return DownloadCategory.Other;
        }

        return ExtensionMap.TryGetValue(extension, out var category)
            ? category
            : DownloadCategory.Other;
    }
}
