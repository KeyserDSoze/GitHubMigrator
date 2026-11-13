using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #123: durable PRE-MOVE source-only intent. The separately OFF-by-default
// service performs a fresh real physical mirror inspection BEFORE any SQL
// lock, then a final SOURCE-FIRST DB-time CAS of independent F-091/F-097b/
// F-031/F-035v2, terminal F-061v2 and reviewed #122 decision.
// NO Git/move runs under the transaction, and this journal deliberately
// has NO method for physical rename, post-move release or re-admission.
public sealed record GitIncrementalLaterCheckpointMirrorIsolationIntentOptions(
    bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorIsolationIntentJournal(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorIsolationIntentOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector inspector,
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight admission,
    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore leaseStore)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    // EF maps this read-only projection from the immutable #122 SQL row.
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
                CheckpointId, SourceSnapshotId,
                OriginalWorkerId, OriginalLeaseToken,
                OriginalLeaseGeneration, OriginalBundleActorId,
                ReviewerId, Decision, InspectionEvidenceSha256,
                LeaseEventHistorySha256, StagingEvidenceSha256,
                ObjectSetSha256, ReviewedAtUtc, EvidenceSha256,
                CanReAdmit, CanMaterialize, CanPush,
                CanRetry, CanDispatch, MigrationComplete);
    }

    public async Task<GitIncrementalLaterCheckpointMirrorIsolationIntent>
        PrepareAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorIsolationIntentDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            workpackId == Guid.Empty || originalBundleActorId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !GitSourceBundleStorageCheck.ValidSha(expectedTargetReceiptSha256))
            throw Refused("LaterCheckpointMirrorIsolationIntentScopeInvalid");

        // The independent #121 reader rehashes real UUID.bundle and
        // double-fscks actual UUID.git outside PostgreSQL transactions.
        // Missing, corrupted, or already isolated Git is not Intact.
        var inspected = await inspector.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        if (inspected.PhysicalState !=
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Intact ||
            inspected.SourceSnapshotId == Guid.Empty ||
            inspected.CanReAdmit || inspected.CanMaterialize ||
            inspected.CanPush || inspected.CanRetry ||
            inspected.CanDispatch || inspected.MigrationComplete ||
            inspected.DurableRecoveryApprovalPersisted)
            throw Refused("LaterCheckpointMirrorIsolationIntentNoIntactOrphan");

        var candidate = await admission.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        if (candidate.PhysicalMirrorId != inspected.PhysicalMirrorId ||
            candidate.SourceSnapshotId != inspected.SourceSnapshotId ||
            candidate.CheckpointId != inspected.CheckpointId ||
            candidate.WorkpackId != inspected.WorkpackId ||
            candidate.StagingEvidenceSha256 != inspected.StagingEvidenceSha256 ||
            candidate.SourceFullRefProofSha256 != inspected.FullRefProofSha256 ||
            candidate.SourceRefSetSha256 != inspected.SourceRefSetSha256 ||
            candidate.BundleEvidenceSha256 != inspected.BundleEvidenceSha256 ||
            candidate.BundleSha256 != inspected.BundleSha256 ||
            candidate.DurableLeasePersisted ||
            candidate.F061LeaseGeneration != 0 ||
            candidate.CanMaterialize || candidate.CanPush ||
            candidate.CanRetry || candidate.CanDispatch ||
            candidate.MigrationComplete)
            throw Refused("LaterCheckpointMirrorIsolationIntentSourceChanged");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await leaseStore.LockAsync(candidate.SourceConnectionId, planId, ct);
        await leaseStore.RequireLiveLockedAsync(
            candidate, originalBundleActorId, ct);
        var now = await leaseStore.DatabaseNowAsync(ct);
        var lease = await leaseStore.ReadLeaseAsync(
            inspected.PhysicalMirrorId, ct)
            ?? throw Refused("LaterCheckpointMirrorIsolationIntentLeaseMissing");
        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore.RequireSameScope(
            lease, candidate, originalBundleActorId);
        if (lease.State != "RecoveryRequired" ||
            lease.LeaseUntilUtc is not null ||
            lease.HasVerifiedLocalObjects ||
            lease.FailureCode != inspected.FailureCode ||
            lease.WorkerId != inspected.WorkerId ||
            lease.LeaseToken != inspected.LeaseToken ||
            lease.Generation != inspected.LeaseGeneration ||
            lease.CanMaterialize || lease.CanPush || lease.CanRetry ||
            lease.CanDispatch || lease.MigrationComplete ||
            now < inspected.InspectedAtUtc ||
            now - inspected.InspectedAtUtc > TimeSpan.FromMinutes(2))
            throw Refused("LaterCheckpointMirrorIsolationIntentLeaseChanged");

        // Read the ACTUAL SQL decision, not the caller's sealed object.
        var row = await db.Database.SqlQuery<ReviewRow>($"""
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
                can_push AS "CanPush", can_retry AS "CanRetry",
                can_dispatch AS "CanDispatch",
                migration_complete AS "MigrationComplete"
            FROM git_later_checkpoint_mirror_recovery_decisions
            WHERE mirror_id = {inspected.PhysicalMirrorId}
                AND workpack_id = {workpackId}
            """).SingleOrDefaultAsync(ct)
            ?? throw Refused("LaterCheckpointMirrorIsolationIntentReviewMissing");
        var reviewed = row.AsReceipt();
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
            .VerifyHistoricalForIsolationIntent(reviewed, inspected);
        if (reviewed.OriginalBundleActorId != originalBundleActorId ||
            reviewed.Decision !=
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview)
            throw Refused("LaterCheckpointMirrorIsolationIntentReviewChanged");
        var decisionEventCount = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_recovery_decision_events
            WHERE mirror_id = {inspected.PhysicalMirrorId}
                AND reviewer_id = {reviewed.ReviewerId}
                AND evidence_sha256 = {reviewed.EvidenceSha256}
                AND decision = {reviewed.Decision}
            """).SingleAsync(ct);
        if (decisionEventCount != 1)
            throw Refused("LaterCheckpointMirrorIsolationIntentReviewAuditChanged");

        // Rehash the FULL ordered worker history again under this final
        // source lock. A matching count alone does NOT close the race.
        var events = await db.Database.SqlQuery<
            PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector.EventRow>($"""
            SELECT id AS "Id", generation AS "Generation",
                worker_id AS "WorkerId", type AS "Type",
                staging_evidence_sha256 AS "StagingEvidenceSha256",
                occurred_at_utc AS "OccurredAtUtc"
            FROM git_later_checkpoint_mirror_lease_events
            WHERE mirror_id = {inspected.PhysicalMirrorId}
            ORDER BY id ASC LIMIT 1001
            """).ToArrayAsync(ct);
        if (events.Length != inspected.LeaseEventCount ||
            events.Length is < 2 or > 1000 ||
            events[0].Type != "Acquired" ||
            events[^1].Type != "Quarantined" ||
            events.Count(e => e.Type == "Quarantined") != 1 ||
            events.Count(e => e.Type == "Acquired") != 1 ||
            events.Count(e => e.Type == "Renewed") !=
                inspected.RenewedEventCount ||
            events.Any(e => e.Generation != inspected.LeaseGeneration ||
                e.WorkerId != inspected.WorkerId ||
                e.StagingEvidenceSha256 != inspected.StagingEvidenceSha256) ||
            PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector
                .HashEvents(events) != inspected.LeaseEventHistorySha256)
            throw Refused("LaterCheckpointMirrorIsolationIntentWorkerAuditChanged");

        var previous = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value" FROM
                git_later_checkpoint_mirror_isolation_intents
            WHERE mirror_id = {inspected.PhysicalMirrorId}
                OR workpack_id = {workpackId}
            """).SingleAsync(ct);
        var previouslyVerified = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value" FROM
                git_later_checkpoint_mirror_verified_evidence
            WHERE mirror_id = {inspected.PhysicalMirrorId}
                OR workpack_id = {workpackId}
            """).SingleAsync(ct);
        if (previous != 0 || previouslyVerified != 0)
            throw Refused("LaterCheckpointMirrorIsolationIntentAlreadyPrepared");

        var intent =
            GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity.Seal(
                inspected, reviewed, now);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_isolation_intents (
                mirror_id, workpack_id, checkpoint_id, source_snapshot_id,
                original_worker_id, original_lease_token,
                original_generation, recovery_decision_sha256,
                inspection_sha256, lease_event_history_sha256,
                staging_evidence_sha256, object_set_sha256,
                object_count, isolation_directory_name,
                prepared_at_utc, intent_sha256)
            VALUES ({intent.PhysicalMirrorId}, {intent.WorkpackId},
                {intent.CheckpointId}, {intent.SourceSnapshotId},
                {intent.OriginalWorkerId}, {intent.OriginalLeaseToken},
                {intent.OriginalGeneration}, {intent.RecoveryDecisionSha256},
                {intent.InspectionSha256}, {intent.LeaseEventHistorySha256},
                {intent.StagingEvidenceSha256}, {intent.ObjectSetSha256},
                {intent.ObjectCount}, {intent.IsolationDirectoryName},
                {intent.PreparedAtUtc}, {intent.IntentSha256})
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_isolation_intent_events(
                mirror_id, intent_sha256, type, occurred_at_utc)
            VALUES ({intent.PhysicalMirrorId}, {intent.IntentSha256},
                'Prepared', {intent.PreparedAtUtc})
            """, ct);
        await tx.CommitAsync(ct);
        return intent;
    }
}
