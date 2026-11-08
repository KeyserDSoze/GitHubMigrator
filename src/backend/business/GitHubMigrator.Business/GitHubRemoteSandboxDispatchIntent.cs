using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-080 is an inert, immutable handoff of a previously reviewed
// sandbox session. It is NOT an outbox for a sender and CANNOT grant
// permission to push, create refs, retry, or complete a migration.
public sealed record GitHubRemoteSandboxDispatchIntentReceipt(
    Guid Id, Guid SessionId, Guid HazardId, Guid GrantId,
    long RepositoryId, Guid RequestedBy,
    string SessionEvidenceSha256, string DispatchReviewSha256,
    string PacketSha256, string CandidateSetSha256,
    string LiveRefEvidenceSha256,
    DateTimeOffset RecordedAtUtc, DateTimeOffset ExpiresAtUtc,
    string EvidenceSha256, string State, bool CanExecute,
    bool TransportInvoked, bool MayAutomaticallyRetry,
    bool MigrationComplete);

public sealed record GitHubRemoteSandboxDispatchIntentInspection(
    GitHubRemoteSandboxDispatchIntentReceipt Intent,
    string Status, bool HistoricalOnly,
    bool CanExecute, bool CanRetry, bool MigrationComplete);

public sealed class GitHubRemoteSandboxDispatchIntentException(string code)
    : Exception("GitHub sandbox dispatch intent refused")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteSandboxDispatchIntentIntegrity
{
    public const string Recorded = "PreparedWriteDisabled";
    public const string Current = "ReadOnlyIntentCurrent";
    public const string Historical = "HistoricalOnly";

    public static string Digest(GitHubRemoteSandboxDispatchIntentReceipt x)
    {
        if (x is null || x.Id == Guid.Empty || x.SessionId == Guid.Empty ||
            x.HazardId == Guid.Empty || x.GrantId == Guid.Empty ||
            x.RepositoryId <= 0 || x.RequestedBy == Guid.Empty ||
            x.RecordedAtUtc == default || x.ExpiresAtUtc <= x.RecordedAtUtc ||
            !Valid(x.SessionEvidenceSha256) ||
            !Valid(x.DispatchReviewSha256) || !Valid(x.PacketSha256) ||
            !Valid(x.CandidateSetSha256) || !Valid(x.LiveRefEvidenceSha256) ||
            x.State != Recorded || x.CanExecute || x.TransportInvoked ||
            x.MayAutomaticallyRetry || x.MigrationComplete)
            throw Invalid();
        var stable = JsonSerializer.Serialize(new object[] {
            "f080-sandbox-dispatch-intent-v1",
            x.Id.ToString("D"), x.SessionId.ToString("D"),
            x.HazardId.ToString("D"), x.GrantId.ToString("D"),
            x.RepositoryId, x.RequestedBy.ToString("D"),
            x.SessionEvidenceSha256, x.DispatchReviewSha256,
            x.PacketSha256, x.CandidateSetSha256, x.LiveRefEvidenceSha256,
            x.RecordedAtUtc.ToUnixTimeMilliseconds(),
            x.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            Recorded, false, false, false, false
        });
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(stable)));
    }

    public static void Verify(GitHubRemoteSandboxDispatchIntentReceipt x)
    {
        if (Digest(x) != x.EvidenceSha256)
            throw new GitHubRemoteSandboxDispatchIntentException(
                "SandboxDispatchIntentHistoryCorrupt");
    }

    private static bool Valid(string? x) =>
        GitSourceBundleStorageCheck.ValidSha(x);

    private static GitHubRemoteSandboxDispatchIntentException Invalid() =>
        new("SandboxDispatchIntentEvidenceInvalid");
}
