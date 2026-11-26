using System.Text.Json.Serialization;
using Fetchy.Core.Engine;
using Fetchy.Core.Management;

namespace Fetchy.Core.Storage;

/// <summary>
/// Serializable checkpoint of an in-progress or paused download persisted in a <c>.fetchy.state</c> file.
/// </summary>
public sealed class DownloadState
{
    public int SchemaVersion { get; set; } = 1;
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long? TotalBytes { get; set; }
    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ExpectedMd5 { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Safe, non-sensitive HTTP headers only (e.g. Referer, User-Agent). Sensitive headers are strictly stripped.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PersistedSegment> Segments { get; set; } = [];
}

/// <summary>
/// Serializable state of an individual byte-range segment.
/// </summary>
public sealed class PersistedSegment
{
    public int Id { get; set; }
    public long Start { get; set; }
    public long Current { get; set; }
    public long End { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SegmentState State { get; set; }
}
