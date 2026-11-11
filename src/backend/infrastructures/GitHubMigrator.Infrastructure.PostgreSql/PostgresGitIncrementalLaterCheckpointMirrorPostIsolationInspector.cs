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
    string StagingEvidenceSha256, string IsolationReceiptSha256,
    string FirstReviewSha256, string PreparedIntentSha256,
    string IsolationDirectoryName, string ObjectSetSha256, int ObjectCount,
    string PhysicalState, DateTimeOffset ObservedAtUtc,
    bool DurableSecondReviewPersisted, bool CanReAdmit,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

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
            Guid originalBundleActorId, CancellationToken ct)
    {
        var snapshot = await reader.ReadCompletedAsync(
            preparationId, planId, checkpointId, targetObservationId,
            workpackId, sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
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
