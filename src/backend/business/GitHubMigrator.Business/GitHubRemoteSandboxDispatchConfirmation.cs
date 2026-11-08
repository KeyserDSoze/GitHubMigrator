using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-081 is a manual second-person confirmation of F-080 audit evidence.
// It does not delegate or authorize a GitHub sender. All outputs are inert.
public sealed record GitHubRemoteSandboxDispatchConfirmationReceipt(
    Guid Id, Guid IntentId, Guid SessionId, Guid HazardId, Guid GrantId,
    long RepositoryId, Guid RequestedBy, Guid ConfirmedBy,
    string IntentEvidenceSha256, string SessionEvidenceSha256,
    string PacketSha256, string CandidateSetSha256,
    string LiveRefEvidenceSha256, DateTimeOffset RecordedAtUtc,
    DateTimeOffset ExpiresAtUtc, string EvidenceSha256, string State,
    bool CanDispatch, bool TransportInvoked,
    bool MayAutomaticallyRetry, bool MigrationComplete);

public sealed record GitHubRemoteSandboxDispatchConfirmationInspection(
    GitHubRemoteSandboxDispatchConfirmationReceipt Confirmation,
    string Status, bool HistoricalOnly, bool CanDispatch,
    bool CanRetry, bool MigrationComplete);

public sealed class GitHubRemoteSandboxDispatchConfirmationException(string code)
    : Exception("GitHub sandbox dispatch confirmation refused")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteSandboxDispatchConfirmationIntegrity
{
    public const string Recorded = "ConfirmedWriteDisabled";
    public const string Current = "ReadOnlyConfirmationCurrent";
    public const string Historical = "HistoricalOnly";

    public static string Digest(GitHubRemoteSandboxDispatchConfirmationReceipt x)
    {
        if (x is null || x.Id == Guid.Empty || x.IntentId == Guid.Empty ||
            x.SessionId == Guid.Empty || x.HazardId == Guid.Empty ||
            x.GrantId == Guid.Empty || x.RepositoryId <= 0 ||
            x.RequestedBy == Guid.Empty || x.ConfirmedBy == Guid.Empty ||
            x.RequestedBy == x.ConfirmedBy ||
            x.RecordedAtUtc == default || x.ExpiresAtUtc <= x.RecordedAtUtc ||
            !Valid(x.IntentEvidenceSha256) ||
            !Valid(x.SessionEvidenceSha256) || !Valid(x.PacketSha256) ||
            !Valid(x.CandidateSetSha256) || !Valid(x.LiveRefEvidenceSha256) ||
            x.State != Recorded || x.CanDispatch || x.TransportInvoked ||
            x.MayAutomaticallyRetry || x.MigrationComplete)
            throw new GitHubRemoteSandboxDispatchConfirmationException(
                "SandboxDispatchConfirmationEvidenceInvalid");
        var canonical = JsonSerializer.Serialize(new object[] {
            "f081-sandbox-dispatch-confirmation-v1",
            x.Id.ToString("D"), x.IntentId.ToString("D"),
            x.SessionId.ToString("D"), x.HazardId.ToString("D"),
            x.GrantId.ToString("D"), x.RepositoryId,
            x.RequestedBy.ToString("D"), x.ConfirmedBy.ToString("D"),
            x.IntentEvidenceSha256, x.SessionEvidenceSha256,
            x.PacketSha256, x.CandidateSetSha256,
            x.LiveRefEvidenceSha256,
            x.RecordedAtUtc.ToUnixTimeMilliseconds(),
            x.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            Recorded, false, false, false, false
        });
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical)));
    }

    public static void Verify(GitHubRemoteSandboxDispatchConfirmationReceipt x)
    {
        if (Digest(x) != x.EvidenceSha256)
            throw new GitHubRemoteSandboxDispatchConfirmationException(
                "SandboxDispatchConfirmationHistoryCorrupt");
    }

    private static bool Valid(string? x) =>
        GitSourceBundleStorageCheck.ValidSha(x);
}
