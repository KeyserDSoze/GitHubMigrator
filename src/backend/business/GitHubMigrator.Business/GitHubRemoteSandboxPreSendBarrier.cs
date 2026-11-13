using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-082 is a pessimistic one-shot durable possible-send marker. Recording
// this evidence consumes F-081 permanently, even if no bytes were sent.
// No method returns a GitHub credential, refspec or transport authority.
public sealed record GitHubRemoteSandboxPreSendBarrierReceipt(
    Guid Id, Guid ConfirmationId, Guid IntentId, Guid SessionId,
    Guid HazardId, Guid GrantId, long RepositoryId, Guid MarkedBy,
    string ConfirmationEvidenceSha256, string IntentEvidenceSha256,
    string SessionEvidenceSha256, string PacketSha256,
    string CandidateSetSha256, string LiveRefEvidenceSha256,
    DateTimeOffset RecordedAtUtc, string EvidenceSha256, string State,
    bool CanExecute, bool TransportInvoked,
    bool MayAutomaticallyRetry, bool MigrationComplete);

public sealed record GitHubRemoteSandboxPreSendBarrierInspection(
    GitHubRemoteSandboxPreSendBarrierReceipt Barrier,
    string Status, bool CanExecute, bool CanRetry, bool MigrationComplete);

public sealed class GitHubRemoteSandboxPreSendBarrierException(string code)
    : Exception("GitHub pre-send barrier refused")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteSandboxPreSendBarrierIntegrity
{
    public const string Recorded = "MayHaveTransmittedWriteDisabled";

    public static string Digest(GitHubRemoteSandboxPreSendBarrierReceipt x)
    {
        if (x is null || x.Id == Guid.Empty || x.ConfirmationId == Guid.Empty ||
            x.IntentId == Guid.Empty || x.SessionId == Guid.Empty ||
            x.HazardId == Guid.Empty || x.GrantId == Guid.Empty ||
            x.RepositoryId <= 0 || x.MarkedBy == Guid.Empty ||
            x.RecordedAtUtc == default ||
            !Valid(x.ConfirmationEvidenceSha256) ||
            !Valid(x.IntentEvidenceSha256) ||
            !Valid(x.SessionEvidenceSha256) ||
            !Valid(x.PacketSha256) ||
            !Valid(x.CandidateSetSha256) ||
            !Valid(x.LiveRefEvidenceSha256) ||
            x.State != Recorded || x.CanExecute || x.TransportInvoked ||
            x.MayAutomaticallyRetry || x.MigrationComplete)
            throw new GitHubRemoteSandboxPreSendBarrierException(
                "PreSendBarrierEvidenceInvalid");
        var canonical = JsonSerializer.Serialize(new object[] {
            "f082-pessimistic-pre-send-barrier-v1",
            x.Id.ToString("D"), x.ConfirmationId.ToString("D"),
            x.IntentId.ToString("D"), x.SessionId.ToString("D"),
            x.HazardId.ToString("D"), x.GrantId.ToString("D"),
            x.RepositoryId, x.MarkedBy.ToString("D"),
            x.ConfirmationEvidenceSha256, x.IntentEvidenceSha256,
            x.SessionEvidenceSha256, x.PacketSha256,
            x.CandidateSetSha256, x.LiveRefEvidenceSha256,
            x.RecordedAtUtc.ToUnixTimeMilliseconds(),
            Recorded, false, false, false, false
        });
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical)));
    }

    public static void Verify(GitHubRemoteSandboxPreSendBarrierReceipt x)
    {
        if (Digest(x) != x.EvidenceSha256)
            throw new GitHubRemoteSandboxPreSendBarrierException(
                "PreSendBarrierHistoryCorrupt");
    }

    private static bool Valid(string? x) =>
        GitSourceBundleStorageCheck.ValidSha(x);
}
