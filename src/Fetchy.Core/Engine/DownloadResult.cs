using Fetchy.Core.Management;
using Fetchy.Core.Verification;

namespace Fetchy.Core.Engine;

/// <summary>
/// Summary of a completed download operation.
/// </summary>
public sealed record DownloadResult(
    Uri FinalUri,
    string FilePath,
    long BytesDownloaded,
    long? TotalBytes,
    TimeSpan Elapsed,
    double AverageSpeedBps,
    int ConnectionsUsed,
    int WorkStealCount,
    int AdaptiveStepDownCount,
    bool UsedSegmentedDownload,
    bool WasResetOnResume,
    DownloadCategory Category,
    HashVerificationResult? Sha256Verification = null,
    HashVerificationResult? Md5Verification = null);

/// <summary>
/// Thrown when post-download cryptographic hash verification fails.
/// </summary>
public sealed class HashMismatchException : IOException
{
    public HashVerificationResult VerificationResult { get; }

    public HashMismatchException(string filePath, HashVerificationResult verificationResult)
        : base($"Hash mismatch ({verificationResult.Algorithm}) for '{filePath}'. Expected '{verificationResult.ExpectedHex}', actual '{verificationResult.ComputedHex}'.")
    {
        VerificationResult = verificationResult;
    }
}
