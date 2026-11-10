using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #122: immutable human judgment about a quarantined source mirror;
// NOT approval to move/adopt bytes, release a lease, acquire a new
// generation or execute any provider operation.
public sealed record GitIncrementalLaterCheckpointMirrorRecoveryDecision(
    int EvidenceVersion, Guid PhysicalMirrorId, Guid WorkpackId,
    Guid CheckpointId, Guid SourceSnapshotId,
    Guid OriginalWorkerId, Guid OriginalLeaseToken,
    long OriginalLeaseGeneration, Guid OriginalBundleActorId,
    Guid ReviewerId, string Decision, string InspectionEvidenceSha256,
    string LeaseEventHistorySha256, string StagingEvidenceSha256,
    string? ObjectSetSha256, DateTimeOffset ReviewedAtUtc,
    string EvidenceSha256, bool CanReAdmit, bool CanMaterialize,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
{
    public const string ApprovedForIsolationReview =
        "ApprovedForIsolationReview";
    public const string Rejected = "Rejected";
    private const string Domain =
        "f097c4-f061v2-recovery-reviewed-decision-v1";

    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorRecoveryDecisionInvalid");

    public static GitIncrementalLaterCheckpointMirrorRecoveryDecision Seal(
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        Guid originalBundleActorId, Guid reviewerId, string decision,
        DateTimeOffset reviewedAtUtc)
    {
        if (inspection is null || inspection.EvidenceVersion != 1 ||
            inspection.PhysicalMirrorId == Guid.Empty ||
            inspection.WorkpackId == Guid.Empty ||
            inspection.OriginalWorkerInvalid() ||
            inspection.DurableRecoveryApprovalPersisted ||
            inspection.CanReAdmit || inspection.CanMaterialize ||
            inspection.CanPush || inspection.CanRetry ||
            inspection.CanDispatch || inspection.MigrationComplete ||
            !GitSourceBundleStorageCheck.ValidSha(
                inspection.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                inspection.LeaseEventHistorySha256) ||
            originalBundleActorId == Guid.Empty ||
            reviewerId == Guid.Empty ||
            reviewerId == inspection.WorkerId ||
            reviewerId == originalBundleActorId ||
            decision is not (ApprovedForIsolationReview or Rejected) ||
            (decision == ApprovedForIsolationReview &&
                (inspection.PhysicalState !=
                    GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Intact ||
                 !GitSourceBundleStorageCheck.ValidSha(
                    inspection.ObjectSetSha256))) ||
            reviewedAtUtc == default ||
            reviewedAtUtc.Offset != TimeSpan.Zero ||
            reviewedAtUtc < inspection.InspectedAtUtc ||
            reviewedAtUtc - inspection.InspectedAtUtc >
                TimeSpan.FromMinutes(2))
            throw Refused();

        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            reviewedAtUtc.ToUnixTimeMilliseconds());
        var record = new GitIncrementalLaterCheckpointMirrorRecoveryDecision(
            1, inspection.PhysicalMirrorId, inspection.WorkpackId,
            inspection.CheckpointId, inspection.SourceSnapshotId,
            inspection.WorkerId, inspection.LeaseToken,
            inspection.LeaseGeneration, originalBundleActorId,
            reviewerId, decision, inspection.EvidenceSha256,
            inspection.LeaseEventHistorySha256,
            inspection.StagingEvidenceSha256, inspection.ObjectSetSha256,
            at, "", false, false, false, false, false, false);
        return record with { EvidenceSha256 = Digest(record) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorRecoveryDecision record,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection)
    {
        if (record is null || record.EvidenceVersion != 1 ||
            record.CanReAdmit || record.CanMaterialize ||
            record.CanPush || record.CanRetry || record.CanDispatch ||
            record.MigrationComplete ||
            record != Seal(inspection, record.OriginalBundleActorId,
                record.ReviewerId, record.Decision, record.ReviewedAtUtc))
            throw Refused();
    }

    private static bool OriginalWorkerInvalid(
        this GitIncrementalLaterCheckpointMirrorRecoveryInspection row) =>
        row.WorkerId == Guid.Empty || row.LeaseToken == Guid.Empty ||
        row.LeaseGeneration is < 1 or > 2000;

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorRecoveryDecision x)
    {
        object?[] data = {
            Domain, x.EvidenceVersion, x.PhysicalMirrorId.ToString("D"),
            x.WorkpackId.ToString("D"), x.CheckpointId.ToString("D"),
            x.SourceSnapshotId.ToString("D"),
            x.OriginalWorkerId.ToString("D"),
            x.OriginalLeaseToken.ToString("D"),
            x.OriginalLeaseGeneration,
            x.OriginalBundleActorId.ToString("D"),
            x.ReviewerId.ToString("D"), x.Decision,
            x.InspectionEvidenceSha256, x.LeaseEventHistorySha256,
            x.StagingEvidenceSha256, x.ObjectSetSha256,
            x.ReviewedAtUtc.ToUniversalTime().ToString("O"),
            false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))));
    }
}
