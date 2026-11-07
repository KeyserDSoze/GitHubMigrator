using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-078 records the result of an independently validated READ-ONLY review.
// An immutable session is NEVER an executable GitHub action or capability.
public sealed record GitHubRemoteSandboxReviewSessionReceipt(
    Guid Id, Guid HazardId, Guid GrantId, Guid FenceRequestId,
    long FenceGeneration, long RepositoryId, Guid ObservedBy,
    string PacketSha256, string CandidateSetSha256,
    string PreflightSha256, string BaselineSha256,
    string DispatchReviewSha256, string LiveRefEvidenceSha256,
    DateTimeOffset RecordedAtUtc, DateTimeOffset ExpiresAtUtc,
    string EvidenceSha256, string State,
    bool TransportInvoked, bool RemoteWritesAuthorized,
    bool MayAutomaticallyRetry, bool MigrationComplete);

public sealed record GitHubRemoteSandboxReviewSessionInspection(
    GitHubRemoteSandboxReviewSessionReceipt Session,
    string Status, bool HistoricalOnly, bool CanExecute,
    bool CanRetry, bool MigrationComplete);

public sealed class GitHubRemoteSandboxReviewSessionException(string code)
    : Exception("GitHub sandbox review session refused")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteSandboxReviewSessionIntegrity
{
    public const string Recorded = "RecordedWriteDisabled";
    public const string Current = "ReadOnlyReviewCurrent";
    public const string Historical = "HistoricalOnly";

    public static string Digest(
        GitHubRemoteSandboxReviewSessionReceipt s)
    {
        if (s is null || s.Id == Guid.Empty || s.HazardId == Guid.Empty ||
            s.GrantId == Guid.Empty || s.FenceRequestId == Guid.Empty ||
            s.ObservedBy == Guid.Empty || s.FenceGeneration <= 0 ||
            s.RepositoryId <= 0 || s.RecordedAtUtc == default ||
            s.ExpiresAtUtc <= s.RecordedAtUtc ||
            !Valid(s.PacketSha256) || !Valid(s.CandidateSetSha256) ||
            !Valid(s.PreflightSha256) || !Valid(s.BaselineSha256) ||
            !Valid(s.DispatchReviewSha256) ||
            !Valid(s.LiveRefEvidenceSha256) ||
            s.State != Recorded ||
            s.TransportInvoked || s.RemoteWritesAuthorized ||
            s.MayAutomaticallyRetry || s.MigrationComplete)
            throw new GitHubRemoteSandboxReviewSessionException(
                "SandboxReviewSessionEvidenceInvalid");
        var stable = JsonSerializer.Serialize(new object[] {
            "f078-sandbox-review-session-v1",
            s.Id.ToString("D"), s.HazardId.ToString("D"),
            s.GrantId.ToString("D"),
            s.FenceRequestId.ToString("D"), s.FenceGeneration,
            s.RepositoryId, s.ObservedBy.ToString("D"),
            s.PacketSha256, s.CandidateSetSha256,
            s.PreflightSha256, s.BaselineSha256,
            s.DispatchReviewSha256, s.LiveRefEvidenceSha256,
            s.RecordedAtUtc.ToUnixTimeMilliseconds(),
            s.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            Recorded, false, false, false, false
        });
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(stable)));
    }

    public static void Verify(
        GitHubRemoteSandboxReviewSessionReceipt receipt)
    {
        if (Digest(receipt) != receipt.EvidenceSha256)
            throw new GitHubRemoteSandboxReviewSessionException(
                "SandboxReviewSessionIntegrityMismatch");
    }

    private static bool Valid(string? hash) =>
        GitSourceBundleStorageCheck.ValidSha(hash);
}
