using System.Buffers;
using System.Security.Cryptography;

namespace Fetchy.Core.Verification;

/// <summary>
/// Supported cryptographic digest algorithms for download integrity verification.
/// </summary>
public enum HashAlgorithmType
{
    Sha256,
    Md5
}

/// <summary>
/// Result of computing or verifying a file digest.
/// </summary>
public sealed record HashVerificationResult(
    HashAlgorithmType Algorithm,
    string ComputedHex,
    string? ExpectedHex,
    bool IsMatch);

/// <summary>
/// Streaming SHA-256 and MD5 file verifier using <see cref="IncrementalHash"/> and pooled buffers.
/// </summary>
public static class HashVerifier
{
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Computes the lowercase hexadecimal hash of <paramref name="filePath"/> using the specified algorithm.
    /// </summary>
    public static async Task<string> ComputeHashAsync(
        string filePath,
        HashAlgorithmType algorithm,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must not be empty.", nameof(filePath));
        }

        HashAlgorithmName algorithmName = algorithm switch
        {
            HashAlgorithmType.Sha256 => HashAlgorithmName.SHA256,
            HashAlgorithmType.Md5 => HashAlgorithmName.MD5,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported hash algorithm.")
        };

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hasher = IncrementalHash.CreateHash(algorithmName);
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                useAsync: true);

            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, bytesRead);
            }

            byte[] hash = hasher.GetHashAndReset();
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Verifies <paramref name="filePath"/> against <paramref name="expectedHex"/> (case-insensitive).
    /// </summary>
    public static async Task<HashVerificationResult> VerifyAsync(
        string filePath,
        HashAlgorithmType algorithm,
        string expectedHex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            throw new ArgumentException("Expected hash must not be empty.", nameof(expectedHex));
        }

        string normalizedExpected = expectedHex.Trim();
        string computed = await ComputeHashAsync(filePath, algorithm, cancellationToken).ConfigureAwait(false);
        bool isMatch = string.Equals(computed, normalizedExpected, StringComparison.OrdinalIgnoreCase);

        return new HashVerificationResult(algorithm, computed, normalizedExpected.ToLowerInvariant(), isMatch);
    }

    public static Task<HashVerificationResult> VerifySha256Async(
        string filePath,
        string expectedHex,
        CancellationToken cancellationToken = default)
        => VerifyAsync(filePath, HashAlgorithmType.Sha256, expectedHex, cancellationToken);

    public static Task<HashVerificationResult> VerifyMd5Async(
        string filePath,
        string expectedHex,
        CancellationToken cancellationToken = default)
        => VerifyAsync(filePath, HashAlgorithmType.Md5, expectedHex, cancellationToken);
}
