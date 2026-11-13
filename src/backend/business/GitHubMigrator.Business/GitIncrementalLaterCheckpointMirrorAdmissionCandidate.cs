using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #117: a short-lived, source-scope *read* witness only. Even a valid
// SHA-256 is NOT a lease or a claim about PostgreSQL rows without the
// independent, live source-locking reader that constructs this candidate.
public sealed record GitIncrementalLaterCheckpointMirrorAdmissionCandidate(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid SourceConnectionId, Guid SourceSnapshotId, Guid CheckpointId,
    Guid TargetObservationId, Guid WorkpackId, Guid PhysicalMirrorId,
    string SourceRepositoryId, string StagingEvidenceSha256,
    string SourceFullRefProofSha256, string SourceRefSetSha256,
    string WorkpackEvidenceSha256, string TargetReceiptSha256,
    string BundleEvidenceSha256, string BundleSha256, long BundleBytes,
    DateTimeOffset ReviewedAtUtc, string EvidenceSha256,
    bool DurableLeasePersisted, long F061LeaseGeneration,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorAdmissionIntegrity
{
    private const string Domain = "f097c4-f061v2-locked-read-candidate-v1";
    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorAdmissionInvalid");

    public static GitIncrementalLaterCheckpointMirrorAdmissionCandidate Seal(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        DateTimeOffset when)
    {
        try
        {
            GitIncrementalLaterCheckpointMirrorStagingIntegrity
                .VerifyOfflineCandidate(stage, durable);
        }
        catch (GitSourceSnapshotMirrorException) { throw Refused(); }

        if (when == default || when.Offset != TimeSpan.Zero)
            throw Refused();
        // Exact PostgreSQL millisecond precision prevents cross-restart
        // digest drift; the candidate is never a durable SQL event.
        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            when.ToUnixTimeMilliseconds());
        var draft = new GitIncrementalLaterCheckpointMirrorAdmissionCandidate(
            1, stage.PreparationId, stage.PlanId,
            stage.SourceConnectionId, stage.SourceSnapshotId,
            stage.CheckpointId, stage.TargetObservationId,
            stage.WorkpackId, stage.PhysicalMirrorId,
            stage.SourceRepositoryId, stage.EvidenceSha256,
            stage.SourceFullRefProofSha256, stage.SourceRefSetSha256,
            stage.WorkpackEvidenceSha256, stage.TargetReceiptSha256,
            stage.BundleEvidenceSha256, stage.BundleSha256, stage.BundleBytes,
            at, "", false, 0, false, false, false, false, false);
        return draft with { EvidenceSha256 = Hash(draft) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate observed,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable)
    {
        if (observed is null || observed.EvidenceVersion != 1 ||
            observed.DurableLeasePersisted ||
            observed.F061LeaseGeneration != 0 ||
            observed.CanMaterialize || observed.CanPush ||
            observed.CanRetry || observed.CanDispatch ||
            observed.MigrationComplete ||
            observed != Seal(stage, durable, observed.ReviewedAtUtc))
            throw Refused();
    }

    private static string Hash(
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate d)
    {
        var fields = new object[] {
            Domain, d.EvidenceVersion,
            d.PreparationId.ToString("D"), d.PlanId.ToString("D"),
            d.SourceConnectionId.ToString("D"),
            d.SourceSnapshotId.ToString("D"),
            d.CheckpointId.ToString("D"),
            d.TargetObservationId.ToString("D"),
            d.WorkpackId.ToString("D"), d.PhysicalMirrorId.ToString("D"),
            d.SourceRepositoryId, d.StagingEvidenceSha256,
            d.SourceFullRefProofSha256, d.SourceRefSetSha256,
            d.WorkpackEvidenceSha256, d.TargetReceiptSha256,
            d.BundleEvidenceSha256, d.BundleSha256, d.BundleBytes,
            d.ReviewedAtUtc.ToUniversalTime().ToString("O"),
            false, 0L, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))));
    }
}
