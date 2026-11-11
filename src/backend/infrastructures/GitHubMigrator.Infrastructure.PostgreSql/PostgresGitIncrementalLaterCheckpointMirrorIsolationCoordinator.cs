using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #124: NO endpoint, NO scheduled job and OFF by default. A durable
// PREPARED intent and independent approved reviewer exist BEFORE this
// method may acquire the physical POSIX root lock or rename UUID.git.
// Git runs OUTSIDE SQL; the short final source-first SQL transaction
// runs INSIDE the physical root lock after two quarantined fsck passes.
public sealed record GitIncrementalLaterCheckpointMirrorIsolationCoordinatorOptions(
    bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorIsolationCoordinator(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorIsolationCoordinatorOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorPreparedReconciliationReader reader,
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight admission,
    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore leaseStore,
    IGitIncrementalLaterCheckpointMirrorPhysicalIsolationSession physical)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    public async Task<GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt>
        RunAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorIsolationCoordinatorDisabled");

        // Reader uses only trusted source/workpack IDs, independently
        // rehashes actual UUID.bundle and rechecks the complete durable
        // #122 decision + Prepared row/event + original worker history.
        // It never calls a physical Git importer or trusts caller SHA.
        var expected = await reader.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        return await physical.WithVerifiedIsolationAsync(
            expected.Stage, expected.DurableBundle,
            expected.ExpectedInspection, expected.HumanDecision,
            expected.PreparedIntent,
            async (proof, lockedCt) =>
            {
                // POSIX root/process locks are held by the local worker
                // throughout this callback. A crash after Linux rename
                // but before SQL commit is reconciled by a later call:
                // only an existing, graph-verified quarantine can pass.
                var reread = await reader.ReadAsync(preparationId, planId,
                    checkpointId, targetObservationId, workpackId,
                    sourceRepositoryId, expectedTargetReceiptSha256,
                    originalBundleActorId, lockedCt);
                if (expected.PreparedIntent != reread.PreparedIntent ||
                    expected.HumanDecision != reread.HumanDecision ||
                    expected.DurableBundle != reread.DurableBundle ||
                    (expected.Stage with {
                        SourceRefs = reread.Stage.SourceRefs
                    }) != reread.Stage ||
                    !expected.Stage.SourceRefs.SequenceEqual(
                        reread.Stage.SourceRefs) ||
                    expected.ExpectedInspection != reread.ExpectedInspection)
                    throw Refused("LaterCheckpointMirrorIsolationCoordinatorHistoryChanged");
                return await CommitLockedAsync(reread, proof,
                    originalBundleActorId, lockedCt);
            }, ct);
    }

    private async Task<GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt>
        CommitLockedAsync(
            GitIncrementalLaterCheckpointMirrorPreparedSnapshot snapshot,
            GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof proof,
            Guid originalBundleActorId, CancellationToken ct)
    {
        var intent = snapshot.PreparedIntent;
        var stage = snapshot.Stage;
        var reviewed = snapshot.HumanDecision;
        var inspection = snapshot.ExpectedInspection;
        var durable = snapshot.DurableBundle;
        if (proof.PhysicalMirrorId != intent.PhysicalMirrorId ||
            proof.WorkpackId != intent.WorkpackId ||
            proof.IsolationDirectoryName != intent.IsolationDirectoryName ||
            proof.ObjectSetSha256 != intent.ObjectSetSha256 ||
            proof.ObjectCount != intent.ObjectCount ||
            proof.BundleSha256 != durable.BundleSha256 ||
            proof.BundleBytes != durable.BundleBytes)
            throw Refused("LaterCheckpointMirrorIsolationCoordinatorPhysicalChanged");

        // Independent source preflight opens its own transactions;
        // finish it BEFORE the final short SQL write fence.
        var source = await admission.ReadAsync(inspection.PreparationId,
            inspection.PlanId, inspection.CheckpointId,
            inspection.TargetObservationId, inspection.WorkpackId,
            inspection.SourceRepositoryId, durable.TargetReceiptSha256,
            originalBundleActorId, ct);
        if (source.PhysicalMirrorId != intent.PhysicalMirrorId ||
            source.SourceConnectionId != stage.SourceConnectionId ||
            source.StagingEvidenceSha256 != intent.StagingEvidenceSha256 ||
            source.BundleSha256 != durable.BundleSha256 ||
            source.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            source.DurableLeasePersisted ||
            source.F061LeaseGeneration != 0 ||
            source.CanMaterialize || source.CanPush ||
            source.CanRetry || source.CanDispatch ||
            source.MigrationComplete)
            throw Refused("LaterCheckpointMirrorIsolationCoordinatorSourceChanged");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await leaseStore.LockAsync(source.SourceConnectionId, source.PlanId, ct);
        await leaseStore.RequireLiveLockedAsync(
            source, originalBundleActorId, ct);
        var now = await leaseStore.DatabaseNowAsync(ct);
        var lease = await leaseStore.ReadLeaseAsync(
            intent.PhysicalMirrorId, ct)
            ?? throw Refused("LaterCheckpointMirrorIsolationCoordinatorLeaseMissing");
        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore
            .RequireSameScope(lease, source, originalBundleActorId);
        if (lease.State != "RecoveryRequired" ||
            lease.LeaseUntilUtc is not null ||
            lease.HasVerifiedLocalObjects ||
            lease.WorkerId != intent.OriginalWorkerId ||
            lease.LeaseToken != intent.OriginalLeaseToken ||
            lease.Generation != intent.OriginalGeneration ||
            lease.FailureCode != inspection.FailureCode ||
            lease.CanMaterialize || lease.CanPush || lease.CanRetry ||
            lease.CanDispatch || lease.MigrationComplete)
            throw Refused("LaterCheckpointMirrorIsolationCoordinatorLeaseChanged");

        var rows = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_isolation_intents i
            JOIN git_later_checkpoint_mirror_isolation_intent_events e
                ON e.mirror_id = i.mirror_id
            JOIN git_later_checkpoint_mirror_recovery_decisions d
                ON d.mirror_id = i.mirror_id
            JOIN git_later_checkpoint_mirror_recovery_decision_events de
                ON de.mirror_id = d.mirror_id
            WHERE i.mirror_id = {intent.PhysicalMirrorId}
                AND i.workpack_id = {intent.WorkpackId}
                AND i.intent_sha256 = {intent.IntentSha256}
                AND i.inspection_sha256 = {intent.InspectionSha256}
                AND i.recovery_decision_sha256 = {reviewed.EvidenceSha256}
                AND i.object_set_sha256 = {intent.ObjectSetSha256}
                AND i.object_count = {intent.ObjectCount}
                AND i.original_worker_id = {intent.OriginalWorkerId}
                AND i.original_lease_token = {intent.OriginalLeaseToken}
                AND i.original_generation = {intent.OriginalGeneration}
                AND i.lease_event_history_sha256 =
                    {intent.LeaseEventHistorySha256}
                AND i.isolation_directory_name =
                    {intent.IsolationDirectoryName}
                AND e.type = 'Prepared'
                AND e.intent_sha256 = {intent.IntentSha256}
                AND e.occurred_at_utc = {intent.PreparedAtUtc}
                AND d.decision = 'ApprovedForIsolationReview'
                AND d.evidence_sha256 = {reviewed.EvidenceSha256}
                AND d.reviewer_id = {reviewed.ReviewerId}
                AND d.original_bundle_actor_id = {originalBundleActorId}
                AND de.decision = d.decision
                AND de.reviewer_id = d.reviewer_id
                AND de.evidence_sha256 = d.evidence_sha256
                AND NOT i.can_readmit AND NOT i.can_materialize
                AND NOT i.can_push AND NOT i.can_retry
                AND NOT i.can_dispatch AND NOT i.migration_complete
                AND NOT d.can_readmit AND NOT d.can_materialize
                AND NOT d.can_push AND NOT d.can_retry
                AND NOT d.can_dispatch AND NOT d.migration_complete
            """).SingleAsync(ct);
        if (rows != 1)
            throw Refused("LaterCheckpointMirrorIsolationCoordinatorIntentChanged");

        var events = await db.Database.SqlQuery<
            PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector.EventRow>($"""
            SELECT id AS "Id", generation AS "Generation",
                worker_id AS "WorkerId", type AS "Type",
                staging_evidence_sha256 AS "StagingEvidenceSha256",
                occurred_at_utc AS "OccurredAtUtc"
            FROM git_later_checkpoint_mirror_lease_events
            WHERE mirror_id = {intent.PhysicalMirrorId}
            ORDER BY id ASC LIMIT 1001
            """).ToArrayAsync(ct);
        if (events.Length != inspection.LeaseEventCount ||
            events.Length is < 2 or > 1000 ||
            events[0].Type != "Acquired" ||
            events[^1].Type != "Quarantined" ||
            events.Count(e => e.Type == "Acquired") != 1 ||
            events.Count(e => e.Type == "Quarantined") != 1 ||
            events.Skip(1).Take(events.Length - 2)
                .Any(e => e.Type != "Renewed") ||
            events.Any(e => e.Generation != intent.OriginalGeneration ||
                e.WorkerId != intent.OriginalWorkerId ||
                e.StagingEvidenceSha256 != intent.StagingEvidenceSha256) ||
            PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector
                .HashEvents(events) != intent.LeaseEventHistorySha256)
            throw Refused("LaterCheckpointMirrorIsolationCoordinatorEventsChanged");

        var previous = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_isolation_receipts
            WHERE mirror_id = {intent.PhysicalMirrorId}
                OR workpack_id = {intent.WorkpackId}
            """).SingleAsync(ct);
        var alreadyVerified = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_verified_evidence
            WHERE mirror_id = {intent.PhysicalMirrorId}
                OR workpack_id = {intent.WorkpackId}
            """).SingleAsync(ct);
        if (previous != 0 || alreadyVerified != 0)
            throw Refused("LaterCheckpointMirrorIsolationCoordinatorAlreadyRecorded");

        // The physical *audit* timestamp is recorded at DB time after
        // verified bytes, rather than pretending it is the unknowable
        // kernel rename time after a power loss.
        var sealedResult =
            GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity
                .Seal(inspection, reviewed, intent, durable, proof, now);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_isolation_receipts (
                mirror_id, workpack_id, isolation_directory_name,
                intent_sha256, recovery_decision_sha256,
                original_worker_id, original_lease_token,
                original_generation, bundle_sha256, bundle_bytes,
                reconciled_existing_quarantine,
                offline_receipt_sha256,
                physically_verified_object_sha256,
                physically_verified_object_count,
                isolated_at_utc, recorded_at_utc, evidence_sha256)
            VALUES ({sealedResult.PhysicalMirrorId}, {sealedResult.WorkpackId},
                {sealedResult.IsolationDirectoryName},
                {sealedResult.PreparedIntentSha256},
                {sealedResult.RecoveryDecisionSha256},
                {sealedResult.OriginalWorkerId},
                {sealedResult.OriginalLeaseToken},
                {sealedResult.OriginalGeneration},
                {sealedResult.BundleSha256}, {sealedResult.BundleBytes},
                {sealedResult.ReconciledExistingQuarantine},
                {sealedResult.OfflineReceiptSha256},
                {sealedResult.ObjectSetSha256}, {sealedResult.ObjectCount},
                {sealedResult.RecordedAtUtc}, {sealedResult.RecordedAtUtc},
                {sealedResult.EvidenceSha256})
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_isolation_receipt_events (
                mirror_id, evidence_sha256, type, occurred_at_utc)
            VALUES ({sealedResult.PhysicalMirrorId},
                {sealedResult.EvidenceSha256}, 'Isolated',
                {sealedResult.RecordedAtUtc})
            """, ct);
        await tx.CommitAsync(ct);
        return sealedResult;
    }
}
