using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #130: historical read-back only. One SQL statement observes a single
// PostgreSQL READ COMMITTED snapshot. No INSERT, writer, lease, source
// re-admission, authorization or operations are enabled by this class.
// Never trust caller-sealed digests alone: the immutable three-row
// journal must exist with exact matching review/event/session evidence.
// A future writer MUST perform live Identity + physical-Git re-audits,
// source-first locking and its own atomic one-winner SQL transaction.
public sealed class PostgresGitIncrementalLaterCheckpointMirrorSecondReviewJournalReadback(
    AppDbContext db)
{
    public async Task<bool> HasExactNonAuthorizingThreeRowReceiptAsync(
        GitIncrementalLaterCheckpointMirrorSecondReviewJournalTuple candidate,
        CancellationToken ct = default)
    {
        GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
            .Verify(candidate);
        var review = candidate.Review;
        var session = candidate.Session;

        // A single EXISTS statement cannot observe a review from one
        // snapshot and a session consumption from another. The strict
        // unique keys and exact FKs are separately enforced by SQL.
        // Confirm all independently observable evidence fields: a
        // matching SHA alone is not enough to prove this historical fact.
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM public.git_later_checkpoint_mirror_second_reviews r
                JOIN public.git_later_checkpoint_mirror_second_review_events e
                  ON e.mirror_id = r.mirror_id
                 AND e.second_reviewer_id = r.second_reviewer_id
                 AND e.decision = r.decision
                 AND e.evidence_sha256 = r.evidence_sha256
                 AND e.occurred_at_utc = r.reviewed_at_utc
                JOIN public.git_later_checkpoint_mirror_second_review_session_consumptions s
                  ON s.mirror_id = r.mirror_id
                 AND s.second_reviewer_id = r.second_reviewer_id
                 AND s.decision = r.decision
                 AND s.review_evidence_sha256 = r.evidence_sha256
                 AND s.reviewed_at_utc = r.reviewed_at_utc
                WHERE r.mirror_id = {review.PhysicalMirrorId}
                  AND r.workpack_id = {review.WorkpackId}
                  AND r.evidence_version = {review.EvidenceVersion}
                  AND r.checkpoint_id = {review.CheckpointId}
                  AND r.source_snapshot_id = {review.SourceSnapshotId}
                  AND r.first_reviewer_id = {review.FirstReviewerId}
                  AND r.second_reviewer_id = {review.SecondReviewerId}
                  AND r.original_worker_id = {review.OriginalWorkerId}
                  AND r.original_lease_token = {review.OriginalLeaseToken}
                  AND r.original_generation = {review.OriginalGeneration}
                  AND r.suggested_next_generation = {review.SuggestedNextGeneration}
                  AND r.decision = {review.Decision}
                  AND r.first_decision_sha256 = {review.FirstDecisionSha256}
                  AND r.isolation_receipt_sha256 = {review.IsolationReceiptSha256}
                  AND r.assessment_evidence_sha256 = {review.AssessmentEvidenceSha256}
                  AND r.staging_evidence_sha256 = {review.StagingEvidenceSha256}
                  AND r.source_full_ref_proof_sha256 = {review.SourceFullRefProofSha256}
                  AND r.source_ref_set_sha256 = {review.SourceRefSetSha256}
                  AND r.target_receipt_sha256 = {review.TargetReceiptSha256}
                  AND r.bundle_sha256 = {review.BundleSha256}
                  AND r.prepared_intent_sha256 = {review.PreparedIntentSha256}
                  AND r.object_set_sha256 = {review.ObjectSetSha256}
                  AND r.object_count = {review.ObjectCount}
                  AND r.source_reviewed_at_utc = {review.SourceReviewedAtUtc}
                  AND r.reviewed_at_utc = {review.ReviewedAtUtc}
                  AND r.evidence_sha256 = {review.EvidenceSha256}
                  AND NOT r.durable_second_review_persisted
                  AND NOT r.can_readmit AND NOT r.can_materialize
                  AND NOT r.can_push AND NOT r.can_retry
                  AND NOT r.can_dispatch AND NOT r.migration_complete
                  AND s.review_session_id = {session.ReviewSessionId}
                  AND s.decision_nonce = {session.DecisionNonce}
                  AND s.review_evidence_sha256 = {session.ReviewEvidenceSha256}
                  AND s.session_intent_evidence_sha256 = {session.EvidenceSha256}
                  AND s.issued_at_utc = {session.IssuedAtUtc}
                  AND s.reviewed_at_utc = {session.ReviewedAtUtc}
                  AND s.consumed_at_utc = {session.ConsumedAtUtc}
                  AND s.expires_at_utc = {session.ExpiresAtUtc}
                  AND NOT s.can_readmit AND NOT s.can_materialize
                  AND NOT s.can_push AND NOT s.can_retry
                  AND NOT s.can_dispatch AND NOT s.migration_complete
            ) AS "Value"
            """).SingleAsync(ct);
    }
}
