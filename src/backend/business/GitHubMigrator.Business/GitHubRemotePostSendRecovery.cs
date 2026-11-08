using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-085 is a durable recovery projection: F-082 possible-send hazard =>
// independently observed F-054 tips => independently fetched F-058 full
// object closure. None of these receipts proves a *sender* caused the
// remote state; no state ever grants a retry or completes the migration.
public static class GitHubRemotePostSendRecoveryStates
{
    public const string AwaitingIndependentReadback =
        "AwaitingIndependentReadback";
    public const string OperatorReviewRequired = "OperatorReviewRequired";
    public const string TipsObservedNeedsClosure =
        "TipsObservedNeedsClosure";
    public const string IndependentClosureObserved =
        "IndependentClosureObserved";
    public const string CheckpointedIndependentClosure =
        "CheckpointedIndependentClosure";
}

public sealed class GitHubRemotePostSendRecoveryException(string code)
    : Exception("Independent post-send recovery refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubRemotePostSendRecoveryInspection(
    Guid BarrierId, Guid ConfirmationId, Guid IntentId,
    Guid HazardId, Guid GrantId, long RepositoryId,
    string State, string BarrierEvidenceSha256,
    Guid? ReadbackId, string? ReadbackEvidenceSha256,
    Guid? ReconciliationId, string? ReconciliationEvidenceSha256,
    string? FullObjectClosureSha256,
    GitHubRemotePostSendClosureCheckpoint? Checkpoint,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public sealed record GitHubRemotePostSendClosureCheckpoint(
    Guid Id, Guid BarrierId, Guid ConfirmationId, Guid IntentId,
    Guid HazardId, Guid GrantId, Guid ReadbackId,
    Guid ReconciliationId, long RepositoryId, Guid RecordedBy,
    string BarrierEvidenceSha256, string ReadbackEvidenceSha256,
    string ReconciliationEvidenceSha256, string FullObjectClosureSha256,
    DateTimeOffset VerifiedAtUtc, DateTimeOffset RecordedAtUtc,
    string State, string EvidenceSha256,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitHubRemotePostSendRecoveryIntegrity
{
    public const string CheckpointState = "IndependentClosureEvidenceOnly";

    public static string CheckpointDigest(GitHubRemotePostSendClosureCheckpoint x)
    {
        if (x is null || x.Id == Guid.Empty ||
            x.BarrierId == Guid.Empty || x.ConfirmationId == Guid.Empty ||
            x.IntentId == Guid.Empty || x.HazardId == Guid.Empty ||
            x.GrantId == Guid.Empty || x.ReadbackId == Guid.Empty ||
            x.ReconciliationId == Guid.Empty || x.RepositoryId <= 0 ||
            x.RecordedBy == Guid.Empty ||
            x.VerifiedAtUtc == default || x.RecordedAtUtc == default ||
            x.VerifiedAtUtc > x.RecordedAtUtc ||
            !ValidSha(x.BarrierEvidenceSha256) ||
            !ValidSha(x.ReadbackEvidenceSha256) ||
            !ValidSha(x.ReconciliationEvidenceSha256) ||
            !ValidSha(x.FullObjectClosureSha256) ||
            x.State != CheckpointState || x.CanRetry ||
            x.CanDispatch || x.MigrationComplete)
            throw new GitHubRemotePostSendRecoveryException(
                "PostSendCheckpointInvalid");
        return Hash(new object[] {
            "f085-independent-post-send-closure-v1",
            x.Id.ToString("D"), x.BarrierId.ToString("D"),
            x.ConfirmationId.ToString("D"), x.IntentId.ToString("D"),
            x.HazardId.ToString("D"), x.GrantId.ToString("D"),
            x.ReadbackId.ToString("D"), x.ReconciliationId.ToString("D"),
            x.RepositoryId, x.RecordedBy.ToString("D"),
            x.BarrierEvidenceSha256, x.ReadbackEvidenceSha256,
            x.ReconciliationEvidenceSha256, x.FullObjectClosureSha256,
            x.VerifiedAtUtc.ToUnixTimeMilliseconds(),
            x.RecordedAtUtc.ToUnixTimeMilliseconds(),
            CheckpointState, false, false, false
        });
    }

    public static void VerifyCheckpoint(GitHubRemotePostSendClosureCheckpoint x)
    {
        if (CheckpointDigest(x) != x.EvidenceSha256)
            throw new GitHubRemotePostSendRecoveryException(
                "PostSendCheckpointHistoryCorrupt");
    }

    // Re-hash persisted F-058 domain proof independently, without
    // accepting its stored EvidenceSha256 as sufficient.
    public static void VerifyFullClosure(
        GitHubRemoteObjectReconciliationStatus x)
    {
        if (x is null || x.Id == Guid.Empty || x.GrantId == Guid.Empty ||
            x.ReadbackId == Guid.Empty || x.HazardId == Guid.Empty ||
            x.RepositoryId <= 0 || x.SourceSnapshotId == Guid.Empty ||
            x.State != GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly ||
            x.ObservedBy == Guid.Empty || x.VerifiedAtUtc == default ||
            !ValidSha(x.OriginalBaselineSha256) ||
            !ValidSha(x.CandidateSetSha256) ||
            !ValidSha(x.TargetRefSetSha256) ||
            !ValidSha(x.RemoteRefObservationSha256) ||
            !ValidSha(x.FullObjectClosureSha256) ||
            !ValidSha(x.RemoteMirrorReceiptSha256) ||
            x.RemotePushInvoked || x.RemoteWritesAuthorized ||
            x.MayAutomaticallyRetry || x.MigrationComplete)
            throw Corrupt();

        var expected = Hash(new object[] {
            "f058-immutable-independent-remote-object-evidence-v1",
            x.GrantId.ToString("D"), x.ReadbackId.ToString("D"),
            x.HazardId.ToString("D"), x.RepositoryId,
            x.SourceSnapshotId.ToString("D"),
            GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
            x.OriginalBaselineSha256, x.CandidateSetSha256,
            x.TargetRefSetSha256, x.RemoteRefObservationSha256,
            x.FullObjectClosureSha256, x.RemoteMirrorReceiptSha256,
            x.VerifiedAtUtc.ToUnixTimeMilliseconds(),
            false, false, false, false
        });
        if (x.EvidenceSha256 != expected)
            throw Corrupt();
    }

    private static GitHubRemotePostSendRecoveryException Corrupt() =>
        new("PostSendFullClosureHistoryCorrupt");

    private static bool ValidSha(string? s) =>
        GitSourceBundleStorageCheck.ValidSha(s);

    private static string Hash(object[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
}
