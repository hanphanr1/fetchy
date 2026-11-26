using System.Text.Json.Serialization;

namespace Fetchy.Core.Management;

/// <summary>
/// Lifecycle status of a managed download in the catalog.
/// </summary>
public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Serializable catalog record persisted in <c>downloads.json</c>.
/// Sensitive HTTP headers are strictly excluded.
/// </summary>
public sealed class DownloadCatalogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string? FinalFilePath { get; set; }
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public int MaxConnections { get; set; } = 16;
    public long MaxBytesPerSecond { get; set; }
    public bool Overwrite { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ExpectedMd5 { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;

    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>
    /// Safe, non-sensitive HTTP headers persisted for resume (e.g. Referer, User-Agent).
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DownloadCatalogEntry Clone() => new()
    {
        Id = Id,
        Url = Url,
        OutputPath = OutputPath,
        FinalFilePath = FinalFilePath,
        TotalBytes = TotalBytes,
        DownloadedBytes = DownloadedBytes,
        MaxConnections = MaxConnections,
        MaxBytesPerSecond = MaxBytesPerSecond,
        Overwrite = Overwrite,
        ExpectedSha256 = ExpectedSha256,
        ExpectedMd5 = ExpectedMd5,
        Status = Status,
        Category = Category,
        ErrorMessage = ErrorMessage,
        CreatedAtUtc = CreatedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc,
        CompletedAtUtc = CompletedAtUtc,
        Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase)
    };
}
