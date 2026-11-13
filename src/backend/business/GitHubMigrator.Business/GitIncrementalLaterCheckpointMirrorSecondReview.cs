using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #129: a DOMAIN-ONLY, NON-AUTHORIZING second-review candidate. No
// authenticated role/JWT, source-first SQL read, POSIX proof, generation
// lease or destination operation is granted by a self-sealed digest.
public sealed record GitIncrementalLaterCheckpointMirrorSecondReview(
    int EvidenceVersion, Guid PhysicalMirrorId, Guid WorkpackId,
    Guid CheckpointId, Guid SourceSnapshotId, string SourceRepositoryId,
    Guid FirstReviewerId, Guid SecondReviewerId, string Decision,
    Guid OriginalWorkerId, Guid OriginalLeaseToken,
    long OriginalGeneration, long SuggestedNextGeneration,
    string StagingEvidenceSha256, string SourceFullRefProofSha256,
    string SourceRefSetSha256, string TargetReceiptSha256,
    string BundleSha256, long BundleBytes,
    string FirstDecisionSha256, string IsolationReceiptSha256,
    string PreparedIntentSha256, string AssessmentEvidenceSha256,
    string IsolationDirectoryName, string ObjectSetSha256, int ObjectCount,
    DateTimeOffset SourceReviewedAtUtc, DateTimeOffset AssessedAtUtc,
    DateTimeOffset ReviewedAtUtc, string EvidenceSha256,
    bool DurableSecondReviewPersisted, bool CanReAdmit,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
{
    public const string ApprovedForIndependentReview =
        "ApprovedForIndependentReview";
    public const string RejectedForIndependentReview =
        "RejectedForIndependentReview";

    private const string Domain =
        "f097c4-f061v2e-second-independent-review-candidate-v1";

    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorSecondReviewInvalid");

    public static GitIncrementalLaterCheckpointMirrorSecondReview Seal(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt isolated,
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate sourceRead,
        GitIncrementalLaterCheckpointMirrorReadmissionAssessment assessment,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection firstInspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision firstReview,
        Guid secondReviewerId, string decision, DateTimeOffset reviewedAtUtc)
    {
        try
        {
            GitIncrementalLaterCheckpointMirrorReadmissionAssessmentIntegrity
                .Verify(assessment, stage, durable, isolated, sourceRead);
            // A first-human decision was sealed against the ORIGINAL
            // recovery inspection. The post-Isolated source-first
            // reader reconstructs a later, independently checked
            // inspection with a DIFFERENT inspection timestamp, but
            // exactly the same immutable worker/lease/graph and
            // review-candidate SHA. Do not falsely re-seal the old
            // decision as if the human reviewed this later timestamp.
            // The existing physical-isolation verification accepts
            // either the original exact inspection OR a later
            // independently checked equivalent, with all original
            // decision hashes and reviewer independence preserved.
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .VerifyForPhysicalIsolation(firstReview, firstInspection);
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw Refused();
        }

        // The FIRST decision permits isolation review only. The second
        // human is distinct from that reviewer, bundle actor and failed
        // worker. This scope check does NOT establish actual authentication.
        if (firstReview.Decision !=
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview ||
            firstReview.EvidenceSha256 != isolated.RecoveryDecisionSha256 ||
            firstReview.EvidenceSha256 != assessment.RecoveryDecisionSha256 ||
            firstReview.PhysicalMirrorId != assessment.PhysicalMirrorId ||
            firstReview.WorkpackId != assessment.WorkpackId ||
            firstReview.CheckpointId != assessment.CheckpointId ||
            firstReview.SourceSnapshotId != assessment.SourceSnapshotId ||
            firstReview.OriginalWorkerId != assessment.OriginalWorkerId ||
            firstReview.OriginalLeaseToken != assessment.OriginalLeaseToken ||
            firstReview.OriginalLeaseGeneration != assessment.OriginalGeneration ||
            secondReviewerId == Guid.Empty ||
            secondReviewerId == firstReview.ReviewerId ||
            secondReviewerId == firstReview.OriginalBundleActorId ||
            secondReviewerId == assessment.OriginalWorkerId ||
            decision is not (ApprovedForIndependentReview or
                RejectedForIndependentReview) ||
            reviewedAtUtc == default ||
            reviewedAtUtc.Offset != TimeSpan.Zero ||
            reviewedAtUtc < assessment.AssessedAtUtc ||
            reviewedAtUtc < assessment.SourceReviewedAtUtc ||
            reviewedAtUtc - assessment.SourceReviewedAtUtc >
                TimeSpan.FromMinutes(2))
            throw Refused();

        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            reviewedAtUtc.ToUnixTimeMilliseconds());
        var candidate = new GitIncrementalLaterCheckpointMirrorSecondReview(
            1, assessment.PhysicalMirrorId, assessment.WorkpackId,
            assessment.CheckpointId, assessment.SourceSnapshotId,
            stage.SourceRepositoryId,
            firstReview.ReviewerId, secondReviewerId, decision,
            assessment.OriginalWorkerId, assessment.OriginalLeaseToken,
            assessment.OriginalGeneration, assessment.SuggestedNextGeneration,
            assessment.StagingEvidenceSha256,
            assessment.SourceFullRefProofSha256,
            assessment.SourceRefSetSha256, assessment.TargetReceiptSha256,
            assessment.BundleSha256, assessment.BundleBytes,
            firstReview.EvidenceSha256, assessment.IsolationReceiptSha256,
            assessment.PreparedIntentSha256, assessment.EvidenceSha256,
            assessment.IsolationDirectoryName,
            assessment.ObjectSetSha256, assessment.ObjectCount,
            assessment.SourceReviewedAtUtc, assessment.AssessedAtUtc,
            at, "", false, false, false, false, false, false, false);
        return candidate with { EvidenceSha256 = Digest(candidate) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorSecondReview candidate,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt isolated,
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate sourceRead,
        GitIncrementalLaterCheckpointMirrorReadmissionAssessment assessment,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection firstInspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision firstReview)
    {
        if (candidate is null || candidate.EvidenceVersion != 1 ||
            candidate.DurableSecondReviewPersisted ||
            candidate.CanReAdmit || candidate.CanMaterialize ||
            candidate.CanPush || candidate.CanRetry ||
            candidate.CanDispatch || candidate.MigrationComplete ||
            candidate != Seal(stage, durable, isolated, sourceRead,
                assessment, firstInspection, firstReview,
                candidate.SecondReviewerId, candidate.Decision,
                candidate.ReviewedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorSecondReview x)
    {
        object[] parts = {
            Domain, x.EvidenceVersion, x.PhysicalMirrorId.ToString("D"),
            x.WorkpackId.ToString("D"), x.CheckpointId.ToString("D"),
            x.SourceSnapshotId.ToString("D"), x.SourceRepositoryId,
            x.FirstReviewerId.ToString("D"),
            x.SecondReviewerId.ToString("D"), x.Decision,
            x.OriginalWorkerId.ToString("D"),
            x.OriginalLeaseToken.ToString("D"),
            x.OriginalGeneration, x.SuggestedNextGeneration,
            x.StagingEvidenceSha256, x.SourceFullRefProofSha256,
            x.SourceRefSetSha256, x.TargetReceiptSha256,
            x.BundleSha256, x.BundleBytes, x.FirstDecisionSha256,
            x.IsolationReceiptSha256, x.PreparedIntentSha256,
            x.AssessmentEvidenceSha256, x.IsolationDirectoryName,
            x.ObjectSetSha256, x.ObjectCount,
            x.SourceReviewedAtUtc.ToUniversalTime().ToString("O"),
            x.AssessedAtUtc.ToUniversalTime().ToString("O"),
            x.ReviewedAtUtc.ToUniversalTime().ToString("O"),
            false, false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }
}
