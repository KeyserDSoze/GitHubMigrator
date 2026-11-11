using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #126: a NON-AUTHORIZING, immutable comparison of already recorded
// isolation history with a separately checked later-checkpoint source
// read. Its SHA is not a SQL read, a second human decision, a lease,
// an on-disk audit or permission to re-admit an F-061 generation.
public sealed record GitIncrementalLaterCheckpointMirrorReadmissionAssessment(
    int EvidenceVersion,
    Guid PreparationId, Guid PlanId, Guid CheckpointId,
    Guid SourceSnapshotId, Guid TargetObservationId,
    Guid WorkpackId, Guid PhysicalMirrorId, string SourceRepositoryId,
    Guid OriginalWorkerId, Guid OriginalLeaseToken,
    long OriginalGeneration, long SuggestedNextGeneration,
    string StagingEvidenceSha256, string SourceFullRefProofSha256,
    string SourceRefSetSha256, string TargetReceiptSha256,
    string BundleSha256, long BundleBytes,
    string IsolationReceiptSha256, string RecoveryDecisionSha256,
    string PreparedIntentSha256, string IsolationDirectoryName,
    string ObjectSetSha256, int ObjectCount,
    DateTimeOffset IsolationRecordedAtUtc,
    DateTimeOffset SourceReviewedAtUtc, DateTimeOffset AssessedAtUtc,
    string EvidenceSha256,
    bool DurableReAdmissionApproved, bool CanReAdmit,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorReadmissionAssessmentIntegrity
{
    private const string Domain =
        "f097c4-f061v2-post-isolation-readmission-assessment-v1";

    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorReadmissionAssessmentInvalid");

    public static GitIncrementalLaterCheckpointMirrorReadmissionAssessment Seal(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt isolated,
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate sourceRead,
        DateTimeOffset assessedAtUtc)
    {
        try
        {
            GitIncrementalLaterCheckpointMirrorStagingIntegrity
                .VerifyOfflineCandidate(stage, durable);
            GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Verify(
                sourceRead, stage, durable);
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw Refused();
        }

        // Re-admission will require a DIFFERENT human decision and a
        // NEW append-only PostgreSQL generation history. Both are absent
        // here. Do not infer either from a self-sealed historical receipt.
        if (isolated is null || isolated.EvidenceVersion != 1 ||
            !isolated.DurableIsolationRecorded ||
            isolated.CanReAdmit || isolated.CanMaterialize ||
            isolated.CanPush || isolated.CanRetry ||
            isolated.CanDispatch || isolated.MigrationComplete ||
            isolated.PhysicalMirrorId != stage.PhysicalMirrorId ||
            isolated.WorkpackId != stage.WorkpackId ||
            isolated.OriginalWorkerId == Guid.Empty ||
            isolated.OriginalLeaseToken == Guid.Empty ||
            isolated.OriginalGeneration is < 1 or >= 2000 ||
            !GitSourceBundleStorageCheck.ValidSha(isolated.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                isolated.RecoveryDecisionSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                isolated.PreparedIntentSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                isolated.ObjectSetSha256) ||
            isolated.ObjectCount is < 1 or > 200000 ||
            isolated.BundleSha256 != durable.BundleSha256 ||
            isolated.BundleBytes != durable.BundleBytes ||
            isolated.IsolationDirectoryName !=
                stage.PhysicalMirrorId.ToString("N") + "." +
                isolated.RecoveryDecisionSha256 + ".quarantine.git" ||
            isolated.RecordedAtUtc == default ||
            isolated.RecordedAtUtc.Offset != TimeSpan.Zero ||
            sourceRead.ReviewedAtUtc < isolated.RecordedAtUtc ||
            assessedAtUtc == default ||
            assessedAtUtc.Offset != TimeSpan.Zero ||
            assessedAtUtc < sourceRead.ReviewedAtUtc ||
            assessedAtUtc - sourceRead.ReviewedAtUtc >
                TimeSpan.FromMinutes(2))
            throw Refused();

        // The generation number is a BOUNDED HINT ONLY. No new worker,
        // token, database lease, F-060 importer or destination operation
        // is created. The old RecoveryRequired lease remains terminal.
        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            assessedAtUtc.ToUnixTimeMilliseconds());
        var candidate = new GitIncrementalLaterCheckpointMirrorReadmissionAssessment(
            1, stage.PreparationId, stage.PlanId,
            stage.CheckpointId, stage.SourceSnapshotId,
            stage.TargetObservationId, stage.WorkpackId,
            stage.PhysicalMirrorId, stage.SourceRepositoryId,
            isolated.OriginalWorkerId, isolated.OriginalLeaseToken,
            isolated.OriginalGeneration, isolated.OriginalGeneration + 1,
            stage.EvidenceSha256, stage.SourceFullRefProofSha256,
            stage.SourceRefSetSha256, stage.TargetReceiptSha256,
            durable.BundleSha256, durable.BundleBytes,
            isolated.EvidenceSha256, isolated.RecoveryDecisionSha256,
            isolated.PreparedIntentSha256, isolated.IsolationDirectoryName,
            isolated.ObjectSetSha256, isolated.ObjectCount,
            isolated.RecordedAtUtc, sourceRead.ReviewedAtUtc,
            at, "", false, false, false, false, false, false, false);
        return candidate with { EvidenceSha256 = Digest(candidate) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorReadmissionAssessment assessment,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt isolated,
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate sourceRead)
    {
        if (assessment is null || assessment.EvidenceVersion != 1 ||
            assessment.DurableReAdmissionApproved || assessment.CanReAdmit ||
            assessment.CanMaterialize || assessment.CanPush ||
            assessment.CanRetry || assessment.CanDispatch ||
            assessment.MigrationComplete ||
            assessment != Seal(stage, durable, isolated, sourceRead,
                assessment.AssessedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorReadmissionAssessment x)
    {
        object[] parts = {
            Domain, x.EvidenceVersion,
            x.PreparationId.ToString("D"), x.PlanId.ToString("D"),
            x.CheckpointId.ToString("D"),
            x.SourceSnapshotId.ToString("D"),
            x.TargetObservationId.ToString("D"),
            x.WorkpackId.ToString("D"),
            x.PhysicalMirrorId.ToString("D"), x.SourceRepositoryId,
            x.OriginalWorkerId.ToString("D"),
            x.OriginalLeaseToken.ToString("D"),
            x.OriginalGeneration, x.SuggestedNextGeneration,
            x.StagingEvidenceSha256, x.SourceFullRefProofSha256,
            x.SourceRefSetSha256, x.TargetReceiptSha256,
            x.BundleSha256, x.BundleBytes,
            x.IsolationReceiptSha256, x.RecoveryDecisionSha256,
            x.PreparedIntentSha256, x.IsolationDirectoryName,
            x.ObjectSetSha256, x.ObjectCount,
            x.IsolationRecordedAtUtc.ToUniversalTime().ToString("O"),
            x.SourceReviewedAtUtc.ToUniversalTime().ToString("O"),
            x.AssessedAtUtc.ToUniversalTime().ToString("O"),
            false, false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }
}
