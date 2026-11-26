using Fetchy.Core.Http;
using Microsoft.Win32.SafeHandles;

namespace Fetchy.Core.Storage;

/// <summary>
/// Performs zero-merge, pre-allocated direct random-access disk I/O to a <c>.fetchy.part</c> file
/// and atomically finalizes it to its destination path on completion.
/// </summary>
public sealed class DirectDiskWriter : IDisposable
{
    public const string PartFileSuffix = ".fetchy.part";

    private SafeFileHandle? _handle;
    private bool _disposed;

    public string PartFilePath { get; }

    private DirectDiskWriter(string partFilePath, SafeFileHandle handle)
    {
        PartFilePath = partFilePath;
        _handle = handle;
    }

    public static string GetPartFilePath(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path must not be empty.", nameof(destinationPath));
        }

        return destinationPath + PartFileSuffix;
    }

    /// <summary>
    /// Opens or creates the partial file with asynchronous random-access flags and optionally pre-allocates its size.
    /// </summary>
    public static DirectDiskWriter Open(string partFilePath, long? totalBytes, bool resetExisting = false)
    {
        string? directory = Path.GetDirectoryName(partFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var mode = resetExisting ? FileMode.Create : FileMode.OpenOrCreate;
        SafeFileHandle handle = File.OpenHandle(
            partFilePath,
            mode,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            if (totalBytes.HasValue && totalBytes.Value >= 0)
            {
                long currentLength = RandomAccess.GetLength(handle);
                if (resetExisting || currentLength != totalBytes.Value)
                {
                    RandomAccess.SetLength(handle, totalBytes.Value);
                }
            }
            else if (resetExisting)
            {
                RandomAccess.SetLength(handle, 0);
            }

            return new DirectDiskWriter(partFilePath, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes <paramref name="buffer"/> directly at exact <paramref name="fileOffset"/> without locks or intermediate staging.
    /// </summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed || _handle is null, this);
        return RandomAccess.WriteAsync(_handle, buffer, fileOffset, cancellationToken);
    }

    /// <summary>
    /// Sets the file length explicitly (for pre-allocation or truncating an unknown-length download).
    /// </summary>
    public void SetLength(long length)
    {
        ObjectDisposedException.ThrowIf(_disposed || _handle is null, this);
        RandomAccess.SetLength(_handle, length);
    }

    /// <summary>
    /// Flushes OS file buffers to physical storage.
    /// </summary>
    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed || _handle is null, this);
        RandomAccess.FlushToDisk(_handle);
    }

    /// <summary>
    /// Flushes the partial file to disk, closes the handle, moves it to the final destination path
    /// (auto-disambiguating if needed), and deletes the accompanying state file if present.
    /// </summary>
    public string FinalizeToDestination(string destinationPath, bool overwrite, string? stateFilePath = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_handle is not null)
        {
            RandomAccess.FlushToDisk(_handle);
            _handle.Dispose();
            _handle = null;
        }

        _disposed = true;

        string finalPath = PathSanitizer.GetUniqueDestinationPath(destinationPath, overwrite);
        string? finalDir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(finalDir))
        {
            Directory.CreateDirectory(finalDir);
        }

        File.Move(PartFilePath, finalPath, overwrite: overwrite);

        if (!string.IsNullOrWhiteSpace(stateFilePath) && File.Exists(stateFilePath))
        {
            try
            {
                File.Delete(stateFilePath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of completed state file.
            }
        }

        return finalPath;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle is not null)
        {
            try
            {
                RandomAccess.FlushToDisk(_handle);
            }
            catch (IOException)
            {
                // Ignore flush error during teardown.
            }
            finally
            {
                _handle.Dispose();
                _handle = null;
            }
        }
    }
}
