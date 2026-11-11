using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #124: a source-locked expected-history snapshot for a NON-AUTHORIZING
// post-crash local path audit. The reconstructed "inspection" contains
// the SQL EXPECTED object SHA/count, not a new physical observation.
// Only a separate real double-fsck can prove an existing Git directory.
// No SQL transaction spans Git or moves anything. No lease/re-admission.
public sealed record GitIncrementalLaterCheckpointMirrorPreparedSnapshot(
    GitIncrementalLaterCheckpointMirrorStagingPlan Stage,
    GitIncrementalLaterCheckpointBundleReceipt DurableBundle,
    GitIncrementalLaterCheckpointMirrorRecoveryInspection ExpectedInspection,
    GitIncrementalLaterCheckpointMirrorRecoveryDecision HumanDecision,
    GitIncrementalLaterCheckpointMirrorIsolationIntent PreparedIntent);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorIsolationIntentOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight staging,
    PostgresGitIncrementalLaterCheckpointBundleHistory history,
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight admission,
    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore leaseStore)
{
    private static GitSourceSnapshotMirrorException Refused(string reason) =>
        new(reason);

    private sealed class ReviewRow
    {
        public int EvidenceVersion { get; set; }
        public Guid PhysicalMirrorId { get; set; }
        public Guid WorkpackId { get; set; }
        public Guid CheckpointId { get; set; }
        public Guid SourceSnapshotId { get; set; }
        public Guid OriginalWorkerId { get; set; }
        public Guid OriginalLeaseToken { get; set; }
        public long OriginalLeaseGeneration { get; set; }
        public Guid OriginalBundleActorId { get; set; }
        public Guid ReviewerId { get; set; }
        public string Decision { get; set; } = "";
        public string InspectionEvidenceSha256 { get; set; } = "";
        public string LeaseEventHistorySha256 { get; set; } = "";
        public string StagingEvidenceSha256 { get; set; } = "";
        public string? ObjectSetSha256 { get; set; }
        public DateTimeOffset ReviewedAtUtc { get; set; }
        public string EvidenceSha256 { get; set; } = "";
        public bool CanReAdmit { get; set; }
        public bool CanMaterialize { get; set; }
        public bool CanPush { get; set; }
        public bool CanRetry { get; set; }
        public bool CanDispatch { get; set; }
        public bool MigrationComplete { get; set; }

        public GitIncrementalLaterCheckpointMirrorRecoveryDecision AsReceipt() =>
            new(EvidenceVersion, PhysicalMirrorId, WorkpackId,
                CheckpointId, SourceSnapshotId, OriginalWorkerId,
                OriginalLeaseToken, OriginalLeaseGeneration,
                OriginalBundleActorId, ReviewerId, Decision,
                InspectionEvidenceSha256, LeaseEventHistorySha256,
                StagingEvidenceSha256, ObjectSetSha256, ReviewedAtUtc,
                EvidenceSha256, CanReAdmit, CanMaterialize,
                CanPush, CanRetry, CanDispatch, MigrationComplete);
    }

    private sealed class PreparedRow
    {
        public Guid PhysicalMirrorId { get; set; }
        public Guid WorkpackId { get; set; }
        public Guid CheckpointId { get; set; }
        public Guid SourceSnapshotId { get; set; }
        public Guid OriginalWorkerId { get; set; }
        public Guid OriginalLeaseToken { get; set; }
        public long OriginalGeneration { get; set; }
        public string RecoveryDecisionSha256 { get; set; } = "";
        public string InspectionSha256 { get; set; } = "";
        public string LeaseEventHistorySha256 { get; set; } = "";
        public string StagingEvidenceSha256 { get; set; } = "";
        public string ObjectSetSha256 { get; set; } = "";
        public int ObjectCount { get; set; }
        public string IsolationDirectoryName { get; set; } = "";
        public DateTimeOffset PreparedAtUtc { get; set; }
        public string IntentSha256 { get; set; } = "";
        public bool CanReAdmit { get; set; }
        public bool CanMaterialize { get; set; }
        public bool CanPush { get; set; }
        public bool CanRetry { get; set; }
        public bool CanDispatch { get; set; }
        public bool MigrationComplete { get; set; }

        public GitIncrementalLaterCheckpointMirrorIsolationIntent AsReceipt() =>
            new(1, PhysicalMirrorId, WorkpackId,
                CheckpointId, SourceSnapshotId, OriginalWorkerId,
                OriginalLeaseToken, OriginalGeneration,
                RecoveryDecisionSha256, InspectionSha256,
                LeaseEventHistorySha256, StagingEvidenceSha256,
                ObjectSetSha256, ObjectCount, IsolationDirectoryName,
                PreparedAtUtc, IntentSha256, CanReAdmit, CanMaterialize,
                CanPush, CanRetry, CanDispatch, MigrationComplete);
    }

    // The PRE-MOVE reader must reject completed isolation; #129 adds
    // a separate READ-ONLY post-Isolated source-first history path.
    public Task<GitIncrementalLaterCheckpointMirrorPreparedSnapshot>
        ReadAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct) =>
        ReadCoreAsync(preparationId, planId, checkpointId,
            targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256, originalBundleActorId,
            requireRecordedIsolation: false, ct: ct);

    // Does NOT inspect disk, authorize a reviewer, grant generation
    // ownership or invoke a physical rename. Callers must separately
    // perform fresh POSIX-locked dual-fsck and authenticated review.
    public Task<GitIncrementalLaterCheckpointMirrorPreparedSnapshot>
        ReadCompletedAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct,
            bool failIfSourceBusy = false) =>
        failIfSourceBusy
            ? ReadCompletedFailFastAsync(preparationId, planId,
                checkpointId, targetObservationId, workpackId,
                sourceRepositoryId, expectedTargetReceiptSha256,
                originalBundleActorId, ct)
            : ReadCoreAsync(preparationId, planId, checkpointId,
                targetObservationId, workpackId, sourceRepositoryId,
                expectedTargetReceiptSha256, originalBundleActorId,
                requireRecordedIsolation: true, ct: ct);

    // #131: PostgreSQL source-first upstream F-031 readers can block
    // BEFORE our own pg_try_advisory_xact_lock check. When a caller
    // already holds the F-060 POSIX root, bound the ENTIRE source read
    // to prevent a reverse-order SQL/POSIX lock deadlock. An expired
    // internal read refuses; caller cancellation stays cancellation.
    private async Task<GitIncrementalLaterCheckpointMirrorPreparedSnapshot>
        ReadCompletedFailFastAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct)
    {
        using var budget = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            return await ReadCoreAsync(preparationId, planId,
                checkpointId, targetObservationId, workpackId,
                sourceRepositoryId, expectedTargetReceiptSha256,
                originalBundleActorId, requireRecordedIsolation: true,
                ct: budget.Token, failIfSourceBusy: true);
        }
        catch (OperationCanceledException) when (
            budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Refused("LaterCheckpointMirrorPostIsolationSourceBusy");
        }
        catch (Npgsql.PostgresException error) when (
            budget.IsCancellationRequested && !ct.IsCancellationRequested &&
            error.SqlState == "57014")
        {
            throw Refused("LaterCheckpointMirrorPostIsolationSourceBusy");
        }
    }

    private async Task<GitIncrementalLaterCheckpointMirrorPreparedSnapshot>
        ReadCoreAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, bool requireRecordedIsolation,
            CancellationToken ct, bool failIfSourceBusy = false)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorIsolationReconciliationDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            workpackId == Guid.Empty || originalBundleActorId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !GitSourceBundleStorageCheck.ValidSha(expectedTargetReceiptSha256))
            throw Refused("LaterCheckpointMirrorIsolationReconciliationScopeInvalid");

        // The two independent earlier immutable readers rehash the real
        // UUID.bundle; they do not need the moved UUID.git to exist.
        var stage = await staging.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        var durable = await history.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct)
            ?? throw Refused("LaterCheckpointMirrorIsolationReconciliationBundleMissing");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        var source = await admission.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct,
            failIfSourceBusy: failIfSourceBusy);
        if (source.PhysicalMirrorId != stage.PhysicalMirrorId ||
            source.SourceSnapshotId != stage.SourceSnapshotId ||
            source.WorkpackId != stage.WorkpackId ||
            source.CheckpointId != stage.CheckpointId ||
            source.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            source.BundleSha256 != durable.BundleSha256 ||
            source.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            source.SourceFullRefProofSha256 != stage.SourceFullRefProofSha256 ||
            source.SourceRefSetSha256 != stage.SourceRefSetSha256 ||
            source.DurableLeasePersisted || source.F061LeaseGeneration != 0 ||
            source.CanMaterialize || source.CanPush || source.CanRetry ||
            source.CanDispatch || source.MigrationComplete)
            throw Refused("LaterCheckpointMirrorIsolationReconciliationSourceChanged");

        GitIncrementalLaterCheckpointMirrorPreparedSnapshot result;
        await using (var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct))
        {
            if (failIfSourceBusy)
            {
                if (!await leaseStore.TryLockAsync(
                    source.SourceConnectionId, planId, ct))
                    throw Refused("LaterCheckpointMirrorPostIsolationSourceBusy");
            }
            else
                await leaseStore.LockAsync(
                    source.SourceConnectionId, planId, ct);
            await leaseStore.RequireLiveLockedAsync(
                source, originalBundleActorId, ct);
            var now = await leaseStore.DatabaseNowAsync(ct);
            var lease = await leaseStore.ReadLeaseAsync(
                stage.PhysicalMirrorId, ct)
                ?? throw Refused("LaterCheckpointMirrorIsolationReconciliationLeaseMissing");
            PostgresGitIncrementalLaterCheckpointMirrorLeaseStore
                .RequireSameScope(lease, source, originalBundleActorId);
            if (lease.State != "RecoveryRequired" ||
                lease.LeaseUntilUtc is not null ||
                lease.HasVerifiedLocalObjects ||
                lease.FailureCode is not { Length: >= 1 and <= 70 } ||
                lease.Generation is < 1 or > 2000 ||
                lease.WorkerId == Guid.Empty ||
                lease.LeaseToken == Guid.Empty ||
                lease.CanMaterialize || lease.CanPush || lease.CanRetry ||
                lease.CanDispatch || lease.MigrationComplete)
                throw Refused("LaterCheckpointMirrorIsolationReconciliationNotQuarantined");

            var confirmedVerified = await db.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                FROM git_later_checkpoint_mirror_verified_evidence
                WHERE mirror_id = {stage.PhysicalMirrorId}
                    OR workpack_id = {stage.WorkpackId}
                """).SingleAsync(ct);
            if (confirmedVerified != 0)
                throw Refused("LaterCheckpointMirrorIsolationReconciliationAlreadyVerified");

            // An already-finalized journal is not a prepared operation
            // to replay. Future inspection of completed quarantine
            // evidence is a separate read-only endpoint, not a
            // permission to repeat the filesystem rename.
            var recorded = await db.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                FROM git_later_checkpoint_mirror_isolation_receipts
                WHERE mirror_id = {stage.PhysicalMirrorId}
                    OR workpack_id = {stage.WorkpackId}
                """).SingleAsync(ct);
            if (requireRecordedIsolation)
            {
                // One source-fenced, immutable, audit-linked Isolated
                // receipt must match the original terminal worker,
                // token, generation and exact Reviewed/Prepared object
                // set. A receipt WITHOUT its real Isolated event does
                // not count. No caller supplies the receipt SHA.
                var completed = await db.Database.SqlQuery<long>($"""
                    SELECT count(*)::bigint AS "Value"
                    FROM git_later_checkpoint_mirror_isolation_receipts r
                    JOIN git_later_checkpoint_mirror_isolation_intents i
                        ON i.mirror_id = r.mirror_id
                        AND i.intent_sha256 = r.intent_sha256
                        AND i.recovery_decision_sha256 =
                            r.recovery_decision_sha256
                        AND i.original_worker_id = r.original_worker_id
                        AND i.original_lease_token = r.original_lease_token
                        AND i.original_generation = r.original_generation
                        AND i.isolation_directory_name =
                            r.isolation_directory_name
                        AND i.object_set_sha256 =
                            r.physically_verified_object_sha256
                        AND i.object_count =
                            r.physically_verified_object_count
                    JOIN git_later_checkpoint_mirror_recovery_decisions d
                        ON d.mirror_id = r.mirror_id
                        AND d.evidence_sha256 = r.recovery_decision_sha256
                        AND d.decision = 'ApprovedForIsolationReview'
                        AND d.original_worker_id = r.original_worker_id
                        AND d.original_lease_token = r.original_lease_token
                        AND d.original_lease_generation =
                            r.original_generation
                    JOIN git_later_checkpoint_mirror_isolation_receipt_events e
                        ON e.mirror_id = r.mirror_id
                        AND e.type = 'Isolated'
                        AND e.evidence_sha256 = r.evidence_sha256
                        AND e.occurred_at_utc = r.recorded_at_utc
                    WHERE r.mirror_id = {stage.PhysicalMirrorId}
                        AND r.workpack_id = {stage.WorkpackId}
                        AND r.original_worker_id = {lease.WorkerId}
                        AND r.original_lease_token = {lease.LeaseToken}
                        AND r.original_generation = {lease.Generation}
                        AND r.isolated_at_utc = r.recorded_at_utc
                        AND r.bundle_sha256 = {durable.BundleSha256}
                        AND r.bundle_bytes = {durable.BundleBytes}
                        AND NOT r.can_readmit AND NOT r.can_materialize
                        AND NOT r.can_push AND NOT r.can_retry
                        AND NOT r.can_dispatch AND NOT r.migration_complete
                    """).SingleAsync(ct);
                if (recorded != 1 || completed != 1)
                    throw Refused("LaterCheckpointMirrorPostIsolationReceiptMissing");
            }
            else if (recorded != 0)
                throw Refused("LaterCheckpointMirrorIsolationReconciliationAlreadyRecorded");

            var events = await db.Database.SqlQuery<
                PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector.EventRow>($"""
                SELECT id AS "Id", generation AS "Generation",
                    worker_id AS "WorkerId", type AS "Type",
                    staging_evidence_sha256 AS "StagingEvidenceSha256",
                    occurred_at_utc AS "OccurredAtUtc"
                FROM git_later_checkpoint_mirror_lease_events
                WHERE mirror_id = {stage.PhysicalMirrorId}
                ORDER BY id ASC LIMIT 1001
                """).ToArrayAsync(ct);
            if (events.Length is < 2 or > 1000 ||
                events[0].Type != "Acquired" ||
                events[^1].Type != "Quarantined" ||
                events[0].OccurredAtUtc < lease.AcquiredAtUtc ||
                events[^1].OccurredAtUtc > lease.UpdatedAtUtc ||
                events[^1].OccurredAtUtc > now ||
                events.Count(e => e.Type == "Acquired") != 1 ||
                events.Count(e => e.Type == "Quarantined") != 1 ||
                events.Skip(1).Take(events.Length - 2)
                    .Any(e => e.Type != "Renewed") ||
                events.Any(e => e.Generation != lease.Generation ||
                    e.WorkerId != lease.WorkerId ||
                    e.StagingEvidenceSha256 != stage.EvidenceSha256 ||
                    e.OccurredAtUtc < lease.AcquiredAtUtc ||
                    e.OccurredAtUtc > now) ||
                events.Zip(events.Skip(1), (a,b) =>
                    a.Id >= b.Id || a.OccurredAtUtc > b.OccurredAtUtc)
                    .Any(invalid => invalid))
                throw Refused("LaterCheckpointMirrorIsolationReconciliationEventsChanged");
            var historySha =
                PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector
                    .HashEvents(events);
            var reviewedRow = await db.Database.SqlQuery<ReviewRow>($"""
                SELECT evidence_version AS "EvidenceVersion",
                    mirror_id AS "PhysicalMirrorId",
                    workpack_id AS "WorkpackId",
                    checkpoint_id AS "CheckpointId",
                    source_snapshot_id AS "SourceSnapshotId",
                    original_worker_id AS "OriginalWorkerId",
                    original_lease_token AS "OriginalLeaseToken",
                    original_lease_generation AS "OriginalLeaseGeneration",
                    original_bundle_actor_id AS "OriginalBundleActorId",
                    reviewer_id AS "ReviewerId",
                    decision AS "Decision",
                    inspection_evidence_sha256 AS "InspectionEvidenceSha256",
                    lease_event_history_sha256 AS "LeaseEventHistorySha256",
                    staging_evidence_sha256 AS "StagingEvidenceSha256",
                    object_set_sha256 AS "ObjectSetSha256",
                    reviewed_at_utc AS "ReviewedAtUtc",
                    evidence_sha256 AS "EvidenceSha256",
                    can_readmit AS "CanReAdmit",
                    can_materialize AS "CanMaterialize",
                    can_push AS "CanPush",
                    can_retry AS "CanRetry",
                    can_dispatch AS "CanDispatch",
                    migration_complete AS "MigrationComplete"
                FROM git_later_checkpoint_mirror_recovery_decisions
                WHERE mirror_id = {stage.PhysicalMirrorId}
                    AND workpack_id = {stage.WorkpackId}
                """).SingleOrDefaultAsync(ct)
                ?? throw Refused("LaterCheckpointMirrorIsolationReconciliationReviewMissing");
            var reviewed = reviewedRow.AsReceipt();
            var preparedRow = await db.Database.SqlQuery<PreparedRow>($"""
                SELECT mirror_id AS "PhysicalMirrorId",
                    workpack_id AS "WorkpackId",
                    checkpoint_id AS "CheckpointId",
                    source_snapshot_id AS "SourceSnapshotId",
                    original_worker_id AS "OriginalWorkerId",
                    original_lease_token AS "OriginalLeaseToken",
                    original_generation AS "OriginalGeneration",
                    recovery_decision_sha256 AS "RecoveryDecisionSha256",
                    inspection_sha256 AS "InspectionSha256",
                    lease_event_history_sha256 AS "LeaseEventHistorySha256",
                    staging_evidence_sha256 AS "StagingEvidenceSha256",
                    object_set_sha256 AS "ObjectSetSha256",
                    object_count AS "ObjectCount",
                    isolation_directory_name AS "IsolationDirectoryName",
                    prepared_at_utc AS "PreparedAtUtc",
                    intent_sha256 AS "IntentSha256",
                    can_readmit AS "CanReAdmit",
                    can_materialize AS "CanMaterialize",
                    can_push AS "CanPush", can_retry AS "CanRetry",
                    can_dispatch AS "CanDispatch",
                    migration_complete AS "MigrationComplete"
                FROM git_later_checkpoint_mirror_isolation_intents
                WHERE mirror_id = {stage.PhysicalMirrorId}
                    AND workpack_id = {stage.WorkpackId}
                """).SingleOrDefaultAsync(ct)
                ?? throw Refused("LaterCheckpointMirrorIsolationReconciliationNotPrepared");
            var intent = preparedRow.AsReceipt();
            if (intent.OriginalWorkerId != lease.WorkerId ||
                intent.OriginalLeaseToken != lease.LeaseToken ||
                intent.OriginalGeneration != lease.Generation ||
                intent.LeaseEventHistorySha256 != historySha ||
                intent.StagingEvidenceSha256 != stage.EvidenceSha256 ||
                intent.RecoveryDecisionSha256 != reviewed.EvidenceSha256 ||
                reviewed.OriginalBundleActorId != originalBundleActorId ||
                intent.PreparedAtUtc > now)
                throw Refused("LaterCheckpointMirrorIsolationReconciliationHistoryMismatch");

            var decisionEvents = await db.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                FROM git_later_checkpoint_mirror_recovery_decision_events
                WHERE mirror_id = {stage.PhysicalMirrorId}
                    AND reviewer_id = {reviewed.ReviewerId}
                    AND decision = {reviewed.Decision}
                    AND evidence_sha256 = {reviewed.EvidenceSha256}
                """).SingleAsync(ct);
            var preparedEvents = await db.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                FROM git_later_checkpoint_mirror_isolation_intent_events
                WHERE mirror_id = {stage.PhysicalMirrorId}
                    AND intent_sha256 = {intent.IntentSha256}
                    AND type = 'Prepared'
                    AND occurred_at_utc = {intent.PreparedAtUtc}
                """).SingleAsync(ct);
            if (decisionEvents != 1 || preparedEvents != 1)
                throw Refused("LaterCheckpointMirrorIsolationReconciliationAuditMissing");

            // Reconstruct EXPECTED pre-move graph only from immutable
            // SQL hashes. This is NOT physical verification, so must
            // never be returned as a claim that Git is isolated.
            var expectedGraph =
                GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity.Seal(
                    stage, durable, intent.ObjectSetSha256,
                    intent.ObjectCount, intent.PreparedAtUtc);
            var expectedInspection =
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Seal(
                    stage, durable, lease.WorkerId, lease.LeaseToken,
                    lease.Generation, lease.FailureCode,
                    events[^1].OccurredAtUtc, events.Length, events.Length - 2,
                    historySha, expectedGraph, intent.PreparedAtUtc);
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .VerifyHistoricalForIsolationIntent(reviewed, expectedInspection);
            GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
                .Verify(intent, expectedInspection, reviewed);

            result = new(stage, durable, expectedInspection, reviewed, intent);
            await tx.CommitAsync(ct);
        }

        // Source may be superseded while the transaction is released;
        // check again before allowing even a read-only filesystem audit.
        var laterSource = await admission.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct,
            failIfSourceBusy: failIfSourceBusy);
        if (laterSource.PhysicalMirrorId != source.PhysicalMirrorId ||
            laterSource.SourceSnapshotId != source.SourceSnapshotId ||
            laterSource.StagingEvidenceSha256 != source.StagingEvidenceSha256 ||
            laterSource.SourceFullRefProofSha256 !=
                source.SourceFullRefProofSha256 ||
            laterSource.SourceRefSetSha256 != source.SourceRefSetSha256 ||
            laterSource.BundleSha256 != source.BundleSha256 ||
            laterSource.BundleEvidenceSha256 != source.BundleEvidenceSha256)
            throw Refused("LaterCheckpointMirrorIsolationReconciliationSourceChanged");
        return result;
    }
}
