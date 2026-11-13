using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #129: internal-only read-only source+physical snapshot. This does
// NOT authenticate a second reviewer, issue a lease or authorize any
// re-admission. It is a preflight observation, not an atomic SQL/POSIX
// authorization fence. Callers MUST recheck before writing.
public sealed record GitIncrementalLaterCheckpointMirrorPostIsolationInspectionOptions(
    bool Enabled);

public sealed record GitIncrementalLaterCheckpointMirrorPostIsolationInspection(
    int EvidenceVersion, Guid PhysicalMirrorId, Guid WorkpackId,
    Guid CheckpointId, Guid SourceSnapshotId,
    Guid FirstReviewerId, Guid OriginalBundleActorId,
    Guid OriginalWorkerId,
    string StagingEvidenceSha256, string IsolationReceiptSha256,
    string FirstReviewSha256, string PreparedIntentSha256,
    string IsolationDirectoryName, string ObjectSetSha256, int ObjectCount,
    string PhysicalState, DateTimeOffset ObservedAtUtc,
    bool DurableSecondReviewPersisted, bool CanReAdmit,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

// #129: server-derived, source+POSIX-checked #127 assessment.
// A durable audit input, NEVER a second human review or lease capability.
public sealed record GitIncrementalLaterCheckpointMirrorAssessedIsolationInspection(
    GitIncrementalLaterCheckpointMirrorPostIsolationInspection Inspection,
    string AssessmentEvidenceSha256, long SuggestedNextGeneration,
    DateTimeOffset SourceReviewedAtUtc, DateTimeOffset AssessedAtUtc,
    string? ApprovedPreviewEvidenceSha256 = null,
    string? RejectedPreviewEvidenceSha256 = null,
    DateTimeOffset? PreviewReviewedAtUtc = null);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorPostIsolationInspector(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorPostIsolationInspectionOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader reader,
    IGitIncrementalLaterCheckpointMirrorPostIsolationPathAudit physical,
    TimeProvider clock)
{
    private const string IsolatedIntact = "PreparedIsolatedIntact";

    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    private sealed class IsolationRow
    {
        public Guid PhysicalMirrorId { get; set; }
        public Guid WorkpackId { get; set; }
        public Guid OriginalWorkerId { get; set; }
        public Guid OriginalLeaseToken { get; set; }
        public long OriginalGeneration { get; set; }
        public string RecoveryDecisionSha256 { get; set; } = "";
        public string PreparedIntentSha256 { get; set; } = "";
        public string OfflineReceiptSha256 { get; set; } = "";
        public string IsolationDirectoryName { get; set; } = "";
        public string ObjectSetSha256 { get; set; } = "";
        public int ObjectCount { get; set; }
        public string BundleSha256 { get; set; } = "";
        public long BundleBytes { get; set; }
        public bool ReconciledExistingQuarantine { get; set; }
        public DateTimeOffset RecordedAtUtc { get; set; }
        public string EvidenceSha256 { get; set; } = "";
        public bool CanReAdmit { get; set; }
        public bool CanMaterialize { get; set; }
        public bool CanPush { get; set; }
        public bool CanRetry { get; set; }
        public bool CanDispatch { get; set; }
        public bool MigrationComplete { get; set; }

        public GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt
            ToReceipt() =>
            new(1, PhysicalMirrorId, WorkpackId, OriginalWorkerId,
                OriginalLeaseToken, OriginalGeneration,
                RecoveryDecisionSha256, PreparedIntentSha256,
                OfflineReceiptSha256, IsolationDirectoryName,
                ObjectSetSha256, ObjectCount, BundleSha256, BundleBytes,
                ReconciledExistingQuarantine, RecordedAtUtc, EvidenceSha256,
                true, CanReAdmit, CanMaterialize, CanPush, CanRetry,
                CanDispatch, MigrationComplete);
    }

    private sealed class AssessmentRow
    {
        public string EvidenceSha256 { get; set; } = "";
        public long SuggestedNextGeneration { get; set; }
        public DateTimeOffset SourceReviewedAtUtc { get; set; }
        public DateTimeOffset AssessedAtUtc { get; set; }
    }

    // A second-human review candidate MUST be anchored to an existing,
    // immutable #127 Assessed row AND event. Merely having a quarantined
    // directory and a self-sealed SHA is not sufficient. This function
    // never INSERTs, creates an F-061 generation or authorizes recovery.
    public async Task<GitIncrementalLaterCheckpointMirrorAssessedIsolationInspection>
        ReadAssessedAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct,
            Guid? authenticatedSecondReviewerId = null,
            DateTimeOffset? requestedReviewAtUtc = null,
            Guid? authenticatedReviewSessionId = null,
            Guid? decisionNonce = null)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorPostIsolationInspectorDisabled");
        if ((authenticatedReviewSessionId is not null ||
             decisionNonce is not null || requestedReviewAtUtc is not null) &&
            authenticatedSecondReviewerId is null)
            throw Refused("LaterCheckpointMirrorSecondReviewPreflightInvalid");
        var before = await ReadSqlAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);

        // The second source-first SQL read and exact Assessed/Isolated
        // digest recheck now run inside the SAME F-060 root/process
        // lock as the current two-pass Git audit. There is NO SQL
        // transaction spanning Git subprocesses, and NO write/CAS.
        return await physical.WithVerifiedQuarantineAsync<
            GitIncrementalLaterCheckpointMirrorAssessedIsolationInspection>(
            before.Snapshot.Stage, before.Snapshot.DurableBundle,
            before.Snapshot.ExpectedInspection, before.Snapshot.HumanDecision,
            before.Snapshot.PreparedIntent, async lockedCt =>
        {
        var current = await ReadSqlAsync(preparationId, planId, checkpointId,
            targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256, originalBundleActorId, lockedCt,
            failIfSourceBusy: true);
        var physical = ConfirmUnchanged(before, current, IsolatedIntact);
        var stage = current.Snapshot.Stage;
        var durable = current.Snapshot.DurableBundle;
        var isolated = current.Isolated;
        if (physical.PhysicalMirrorId != stage.PhysicalMirrorId ||
            physical.WorkpackId != stage.WorkpackId ||
            physical.CheckpointId != stage.CheckpointId ||
            physical.SourceSnapshotId != stage.SourceSnapshotId ||
            physical.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            physical.IsolationReceiptSha256 != isolated.EvidenceSha256 ||
            physical.FirstReviewSha256 != isolated.RecoveryDecisionSha256 ||
            physical.PreparedIntentSha256 != isolated.PreparedIntentSha256 ||
            physical.ObjectSetSha256 != isolated.ObjectSetSha256 ||
            physical.ObjectCount != isolated.ObjectCount)
            throw Refused("LaterCheckpointMirrorPostIsolationSourceChanged");

        var assessed = await db.Database.SqlQuery<AssessmentRow>($"""
            SELECT a.evidence_sha256 AS "EvidenceSha256",
                a.suggested_next_generation AS "SuggestedNextGeneration",
                a.source_reviewed_at_utc AS "SourceReviewedAtUtc",
                a.assessed_at_utc AS "AssessedAtUtc"
            FROM git_later_checkpoint_mirror_generation_assessments a
            JOIN git_later_checkpoint_mirror_generation_assessment_events e
                ON e.mirror_id = a.mirror_id
                AND e.evidence_sha256 = a.evidence_sha256
                AND e.type = 'Assessed'
                AND e.occurred_at_utc = a.assessed_at_utc
            WHERE a.mirror_id = {stage.PhysicalMirrorId}
                AND a.workpack_id = {stage.WorkpackId}
                AND a.checkpoint_id = {stage.CheckpointId}
                AND a.source_snapshot_id = {stage.SourceSnapshotId}
                AND a.evidence_version = 1
                AND a.original_worker_id = {isolated.OriginalWorkerId}
                AND a.original_lease_token = {isolated.OriginalLeaseToken}
                AND a.original_generation = {isolated.OriginalGeneration}
                AND a.suggested_next_generation = a.original_generation + 1
                AND a.staging_evidence_sha256 = {stage.EvidenceSha256}
                AND a.source_full_ref_proof_sha256 =
                    {stage.SourceFullRefProofSha256}
                AND a.source_ref_set_sha256 = {stage.SourceRefSetSha256}
                AND a.target_receipt_sha256 = {stage.TargetReceiptSha256}
                AND a.bundle_sha256 = {durable.BundleSha256}
                AND a.bundle_bytes = {durable.BundleBytes}
                AND a.isolation_receipt_sha256 = {isolated.EvidenceSha256}
                AND a.recovery_decision_sha256 =
                    {isolated.RecoveryDecisionSha256}
                AND a.prepared_intent_sha256 =
                    {isolated.PreparedIntentSha256}
                AND a.isolation_directory_name =
                    {isolated.IsolationDirectoryName}
                AND a.object_set_sha256 = {isolated.ObjectSetSha256}
                AND a.object_count = {isolated.ObjectCount}
                AND NOT a.durable_readmission_approved
                AND NOT a.can_readmit AND NOT a.can_materialize
                AND NOT a.can_push AND NOT a.can_retry
                AND NOT a.can_dispatch AND NOT a.migration_complete
            """).SingleOrDefaultAsync(ct)
            ?? throw Refused("LaterCheckpointMirrorSecondReviewAssessmentMissing");

        // Rebuild the *domain-separated* SHA from the trusted source
        // and recorded DB timestamps rather than accepting any guessed
        // SHA or actor ID from an HTTP query. An FK/check alone cannot
        // establish a genuine #127 assessment.
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate sourceRead;
        GitIncrementalLaterCheckpointMirrorReadmissionAssessment sealedValue;
        try
        {
            sourceRead =
                GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Seal(
                    stage, durable, assessed.SourceReviewedAtUtc);
            sealedValue =
                GitIncrementalLaterCheckpointMirrorReadmissionAssessmentIntegrity.Seal(
                    stage, durable, isolated, sourceRead,
                    assessed.AssessedAtUtc);
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw Refused("LaterCheckpointMirrorSecondReviewAssessmentInvalid");
        }

        if (sealedValue.EvidenceSha256 != assessed.EvidenceSha256 ||
            sealedValue.SuggestedNextGeneration !=
                assessed.SuggestedNextGeneration)
            throw Refused("LaterCheckpointMirrorSecondReviewAssessmentInvalid");

        // A human must inspect a RECENT source review, not approve a
        // historic #127 SHA months later. This is a non-authorizing
        // time check; a real writer must re-check using DB time under
        // the source and POSIX locks and use replay-proof JWT identity.
        var now = clock.GetUtcNow();
        if (now - assessed.AssessedAtUtc > TimeSpan.FromMinutes(2) ||
            assessed.AssessedAtUtc - now > TimeSpan.FromMinutes(2))
            throw Refused("LaterCheckpointMirrorSecondReviewAssessmentExpired");

        // Seal only optional, actor-bound and ephemeral review PREVIEWS,
        // NEVER SQL second-review events or lease authority. This still
        // runs inside the shared F-060 POSIX-locked SQL callback.
        string? approveSha = null;
        string? rejectSha = null;
        DateTimeOffset? previewAt = null;
        if (authenticatedSecondReviewerId is Guid reviewer)
        {
            if (reviewer == Guid.Empty ||
                reviewer == current.Snapshot.HumanDecision.ReviewerId ||
                reviewer == current.Snapshot.HumanDecision.OriginalBundleActorId ||
                reviewer == isolated.OriginalWorkerId)
                throw Refused(
                    "LaterCheckpointMirrorSecondReviewIndependentReviewerRequired");

            var at = requestedReviewAtUtc ??
                (now >= assessed.AssessedAtUtc
                    ? now : assessed.AssessedAtUtc);
            // A POST must supply the exact UTC millisecond timestamp
            // returned by GET. Re-seal an identical decision SHA only
            // after a FRESH full physical+SQL audit, never by trusting
            // a client-provided review digest.
            if (at == default || at.Offset != TimeSpan.Zero ||
                at < assessed.AssessedAtUtc || at > now ||
                now - at > TimeSpan.FromMinutes(2) ||
                (requestedReviewAtUtc is not null &&
                 at != DateTimeOffset.FromUnixTimeMilliseconds(
                    at.ToUnixTimeMilliseconds())))
                throw Refused(
                    "LaterCheckpointMirrorSecondReviewPreflightExpired");
            at = DateTimeOffset.FromUnixTimeMilliseconds(
                at.ToUnixTimeMilliseconds());
            var approve =
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity.Seal(
                    stage, durable, isolated, sourceRead, sealedValue,
                    current.Snapshot.ExpectedInspection,
                    current.Snapshot.HumanDecision, reviewer,
                    GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                        .ApprovedForIndependentReview, at);
            var reject =
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity.Seal(
                    stage, durable, isolated, sourceRead, sealedValue,
                    current.Snapshot.ExpectedInspection,
                    current.Snapshot.HumanDecision, reviewer,
                    GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                        .RejectedForIndependentReview, at);
            if (approve.DurableSecondReviewPersisted ||
                reject.DurableSecondReviewPersisted ||
                approve.CanReAdmit || reject.CanReAdmit ||
                approve.CanMaterialize || reject.CanMaterialize ||
                approve.CanPush || reject.CanPush ||
                approve.CanRetry || reject.CanRetry ||
                approve.CanDispatch || reject.CanDispatch ||
                approve.MigrationComplete || reject.MigrationComplete ||
                approve.EvidenceSha256 == reject.EvidenceSha256)
                throw Refused("LaterCheckpointMirrorSecondReviewInvalid");
            approveSha = approve.EvidenceSha256;
            rejectSha = reject.EvidenceSha256;
            previewAt = at;
            if (authenticatedReviewSessionId is not null ||
                decisionNonce is not null)
            {
                if (authenticatedReviewSessionId is not Guid session ||
                    session == Guid.Empty ||
                    decisionNonce is not Guid nonce ||
                    nonce == Guid.Empty)
                    throw Refused(
                        "LaterCheckpointMirrorSecondReviewPreflightInvalid");
                // Non-authorizing snapshot only. SQL UNIQUE keys are
                // the ultimate atomic replay protection once a future
                // authenticated decision writer is implemented.
                var used = await db.Database.SqlQuery<long>($"""
                    SELECT count(*)::bigint AS "Value"
                    FROM git_later_checkpoint_mirror_second_review_session_consumptions
                    WHERE review_session_id = {session}
                        OR decision_nonce = {nonce}
                        OR mirror_id = {stage.PhysicalMirrorId}
                    """).SingleAsync(lockedCt);
                if (used != 0)
                    throw Refused(
                        "LaterCheckpointMirrorSecondReviewSessionAlreadyConsumed");
            }
        }
        return new GitIncrementalLaterCheckpointMirrorAssessedIsolationInspection(
            physical, assessed.EvidenceSha256,
            assessed.SuggestedNextGeneration,
            assessed.SourceReviewedAtUtc, assessed.AssessedAtUtc,
            approveSha, rejectSha, previewAt);
        }, ct);
    }

    public async Task<GitIncrementalLaterCheckpointMirrorPostIsolationInspection>
        ReadAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorPostIsolationInspectorDisabled");

        var before = await ReadSqlAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);

        // Takes the existing F-060 process+POSIX root lock, verifies
        // exact reviewed Prepared marker and bundle bytes, and performs
        // two complete quarantined Git fsck/ref/OID passes, no writes.
        var state = await physical.InspectPreparedPathsAsync(
            before.Snapshot.Stage, before.Snapshot.DurableBundle,
            before.Snapshot.ExpectedInspection,
            before.Snapshot.HumanDecision, before.Snapshot.PreparedIntent, ct);
        if (state != IsolatedIntact)
            throw Refused("LaterCheckpointMirrorPostIsolationPhysicalUntrusted");

        // Recheck F-091/F-097/F-031 source, original lease/event chain,
        // immutable decision/intent and Isolated SQL receipt. This is
        // NOT one atomic authorizing SQL+POSIX transaction.
        var after = await ReadSqlAsync(preparationId, planId, checkpointId,
            targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256, originalBundleActorId, ct);
        return ConfirmUnchanged(before, after, state);
    }

    private GitIncrementalLaterCheckpointMirrorPostIsolationInspection
        ConfirmUnchanged(
            (GitIncrementalLaterCheckpointMirrorPreparedSnapshot Snapshot,
             GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt Isolated) before,
            (GitIncrementalLaterCheckpointMirrorPreparedSnapshot Snapshot,
             GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt Isolated) after,
            string state)
    {
        var first = before.Snapshot;
        var last = after.Snapshot;
        if (before.Isolated != after.Isolated ||
            first.DurableBundle != last.DurableBundle ||
            first.ExpectedInspection != last.ExpectedInspection ||
            first.HumanDecision != last.HumanDecision ||
            first.PreparedIntent != last.PreparedIntent ||
            (first.Stage with { SourceRefs = last.Stage.SourceRefs }) !=
                last.Stage ||
            !first.Stage.SourceRefs.SequenceEqual(last.Stage.SourceRefs))
            throw Refused("LaterCheckpointMirrorPostIsolationSourceChanged");

        var receipt = after.Isolated;
        return new(1, last.Stage.PhysicalMirrorId, last.Stage.WorkpackId,
            last.Stage.CheckpointId, last.Stage.SourceSnapshotId,
            last.HumanDecision.ReviewerId,
            last.HumanDecision.OriginalBundleActorId,
            receipt.OriginalWorkerId,
            last.Stage.EvidenceSha256, receipt.EvidenceSha256,
            receipt.RecoveryDecisionSha256, receipt.PreparedIntentSha256,
            receipt.IsolationDirectoryName, receipt.ObjectSetSha256,
            receipt.ObjectCount, state, clock.GetUtcNow(),
            false, false, false, false, false, false, false);
    }

    private async Task<(
        GitIncrementalLaterCheckpointMirrorPreparedSnapshot Snapshot,
        GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt Isolated)>
        ReadSqlAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct,
            bool failIfSourceBusy = false)
    {
        var snapshot = await reader.ReadCompletedAsync(
            preparationId, planId, checkpointId, targetObservationId,
            workpackId, sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct,
            failIfSourceBusy: failIfSourceBusy);
        var intent = snapshot.PreparedIntent;
        var human = snapshot.HumanDecision;
        var stage = snapshot.Stage;
        var durable = snapshot.DurableBundle;
        var row = await db.Database.SqlQuery<IsolationRow>($"""
            SELECT r.mirror_id AS "PhysicalMirrorId",
                r.workpack_id AS "WorkpackId",
                r.original_worker_id AS "OriginalWorkerId",
                r.original_lease_token AS "OriginalLeaseToken",
                r.original_generation AS "OriginalGeneration",
                r.recovery_decision_sha256 AS "RecoveryDecisionSha256",
                r.intent_sha256 AS "PreparedIntentSha256",
                r.offline_receipt_sha256 AS "OfflineReceiptSha256",
                r.isolation_directory_name AS "IsolationDirectoryName",
                r.physically_verified_object_sha256 AS "ObjectSetSha256",
                r.physically_verified_object_count AS "ObjectCount",
                r.bundle_sha256 AS "BundleSha256",
                r.bundle_bytes AS "BundleBytes",
                r.reconciled_existing_quarantine AS "ReconciledExistingQuarantine",
                r.recorded_at_utc AS "RecordedAtUtc",
                r.evidence_sha256 AS "EvidenceSha256",
                r.can_readmit AS "CanReAdmit",
                r.can_materialize AS "CanMaterialize",
                r.can_push AS "CanPush",
                r.can_retry AS "CanRetry",
                r.can_dispatch AS "CanDispatch",
                r.migration_complete AS "MigrationComplete"
            FROM git_later_checkpoint_mirror_isolation_receipts r
            JOIN git_later_checkpoint_mirror_isolation_receipt_events e
                ON e.mirror_id = r.mirror_id
                AND e.type = 'Isolated'
                AND e.evidence_sha256 = r.evidence_sha256
                AND e.occurred_at_utc = r.recorded_at_utc
            WHERE r.mirror_id = {stage.PhysicalMirrorId}
                AND r.workpack_id = {stage.WorkpackId}
                AND r.original_worker_id = {intent.OriginalWorkerId}
                AND r.original_lease_token = {intent.OriginalLeaseToken}
                AND r.original_generation = {intent.OriginalGeneration}
                AND r.recovery_decision_sha256 = {human.EvidenceSha256}
                AND r.intent_sha256 = {intent.IntentSha256}
                AND r.isolation_directory_name =
                    {intent.IsolationDirectoryName}
                AND r.physically_verified_object_sha256 =
                    {intent.ObjectSetSha256}
                AND r.physically_verified_object_count = {intent.ObjectCount}
                AND r.bundle_sha256 = {durable.BundleSha256}
                AND r.bundle_bytes = {durable.BundleBytes}
                AND r.isolated_at_utc = r.recorded_at_utc
                AND NOT r.can_readmit AND NOT r.can_materialize
                AND NOT r.can_push AND NOT r.can_retry
                AND NOT r.can_dispatch AND NOT r.migration_complete
            """).SingleOrDefaultAsync(ct)
            ?? throw Refused("LaterCheckpointMirrorPostIsolationAuditMissing");
        var receipt = row.ToReceipt();
        // Recompute domain-separated historical evidence from trusted
        // PostgreSQL rows. This is not evidence of current filesystem
        // freshness; the POSIX audit occurs independently in ReadAsync.
        var expectedPhysical =
            new GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof(
                receipt.PhysicalMirrorId, receipt.WorkpackId,
                receipt.IsolationDirectoryName, receipt.ObjectSetSha256,
                receipt.ObjectCount, receipt.BundleSha256, receipt.BundleBytes,
                receipt.RecordedAtUtc, receipt.ReconciledExistingQuarantine);
        GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity.Verify(
            receipt, snapshot.ExpectedInspection, human, intent, durable,
            expectedPhysical);
        return (snapshot, receipt);
    }
}
