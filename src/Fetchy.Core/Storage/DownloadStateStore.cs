using System.Text.Json;
using Fetchy.Core.Http;

namespace Fetchy.Core.Storage;

/// <summary>
/// Atomically persists and loads <see cref="DownloadState"/> checkpoints (<c>.fetchy.state</c>)
/// using same-directory temp files, flush-to-disk, and atomic replacement.
/// </summary>
public static class DownloadStateStore
{
    public const string StateFileSuffix = ".fetchy.state";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetStateFilePath(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path must not be empty.", nameof(destinationPath));
        }

        return destinationPath + StateFileSuffix;
    }

    /// <summary>
    /// Atomically writes <paramref name="state"/> to <paramref name="stateFilePath"/>, stripping all sensitive headers.
    /// </summary>
    public static async Task SaveAsync(
        string stateFilePath,
        DownloadState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(stateFilePath))
        {
            throw new ArgumentException("State file path must not be empty.", nameof(stateFilePath));
        }

        // Sanitize headers defensively before writing to disk so secrets can never leak.
        var sanitizedState = new DownloadState
        {
            SchemaVersion = state.SchemaVersion,
            Url = state.Url,
            DestinationPath = state.DestinationPath,
            TotalBytes = state.TotalBytes,
            SupportsRanges = state.SupportsRanges,
            ETag = state.ETag,
            LastModified = state.LastModified,
            ExpectedSha256 = state.ExpectedSha256,
            ExpectedMd5 = state.ExpectedMd5,
            Category = state.Category,
            CreatedAtUtc = state.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Headers = UriValidator.FilterPersistableHeaders(state.Headers),
            Segments = state.Segments
        };

        await WriteJsonAtomicallyAsync(stateFilePath, sanitizedState, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a <see cref="DownloadState"/> from <paramref name="stateFilePath"/>.
    /// </summary>
    public static async Task<DownloadState> LoadAsync(
        string stateFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stateFilePath))
        {
            throw new ArgumentException("State file path must not be empty.", nameof(stateFilePath));
        }

        await using var stream = new FileStream(
            stateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);

        DownloadState? state = await JsonSerializer.DeserializeAsync<DownloadState>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            throw new InvalidDataException($"State file '{stateFilePath}' is empty or invalid.");
        }

        state.Headers = UriValidator.FilterPersistableHeaders(state.Headers);
        return state;
    }

    /// <summary>
    /// Atomically writes JSON content using a unique same-directory temp file, flush-to-disk, and File.Replace / File.Move.
    /// </summary>
    internal static async Task WriteJsonAtomicallyAsync<T>(
        string targetPath,
        T payload,
        CancellationToken cancellationToken = default)
    {
        string fullTargetPath = Path.GetFullPath(targetPath);
        string? directory = Path.GetDirectoryName(fullTargetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{fullTargetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

            using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await fs.WriteAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(fullTargetPath))
            {
                File.Replace(tempPath, fullTargetPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(tempPath, fullTargetPath);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Ignore cleanup failure for temp file.
                }
            }

            throw;
        }
    }
}
