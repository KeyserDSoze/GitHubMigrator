using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #118: internal coordination only; no Git subprocess or destination call.
// The claim NEVER sets a materialization or migration execution capability.
public sealed record GitIncrementalLaterCheckpointMirrorLeaseOptions(bool Enabled);

public sealed record GitIncrementalLaterCheckpointMirrorLeaseClaim(
    Guid MirrorId, Guid WorkpackId, Guid SourceSnapshotId,
    Guid CheckpointId, Guid LeaseToken, long Generation,
    Guid WorkerId, Guid BundleActorId,
    string StagingEvidenceSha256, DateTimeOffset ExpiresAtUtc,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorLeaseStore(
    AppDbContext db, GitIncrementalLaterCheckpointMirrorLeaseOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight admission)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);
    public static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(2);

    public async Task<GitIncrementalLaterCheckpointMirrorLeaseClaim> ClaimAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, Guid bundleActorId,
        Guid workerId, TimeSpan duration, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorLeaseDisabled");
        if (workerId == Guid.Empty || bundleActorId == Guid.Empty ||
            duration < MinimumLease || duration > MaximumLease)
            throw Refused("LaterCheckpointMirrorLeaseRequestInvalid");

        var proof = await admission.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            bundleActorId, ct);
        if (proof.DurableLeasePersisted || proof.F061LeaseGeneration != 0 ||
            proof.CanMaterialize || proof.CanPush || proof.CanRetry ||
            proof.CanDispatch || proof.MigrationComplete)
            throw Refused("LaterCheckpointMirrorLeaseAdmissionInvalid");
        var token = Guid.NewGuid();

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await LockAsync(proof.SourceConnectionId, planId, ct);
        await RequireLiveLockedAsync(proof, bundleActorId, ct);
        var now = await DatabaseNowAsync(ct);
        var existing = await ReadLeaseAsync(proof.PhysicalMirrorId, ct);
        if (existing is not null)
        {
            RequireSameScope(existing, proof, bundleActorId);
            if (existing.State == "Held" && existing.LeaseUntilUtc <= now)
            {
                // An expired physical importer might have left UUID.git.
                // DO NOT adopt or delete those bytes and do not silently
                // grant a new generation; require manual quarantine review.
                var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE git_later_checkpoint_mirror_leases
                    SET state = 'RecoveryRequired', lease_until_utc = NULL,
                        failure_code = 'ExpiredWorker',
                        updated_at_utc = {now}
                    WHERE mirror_id = {proof.PhysicalMirrorId}
                        AND state = 'Held'
                        AND generation = {existing.Generation}
                        AND lease_token = {existing.LeaseToken}
                        AND lease_until_utc <= {now}
                    """, ct);
                if (changed != 1)
                    throw Refused("LaterCheckpointMirrorLeaseLost");
                await AppendEventAsync(proof.PhysicalMirrorId,
                    existing.Generation, existing.WorkerId, "Quarantined",
                    proof.StagingEvidenceSha256, now, ct);
                await tx.CommitAsync(ct);
                throw Refused("LaterCheckpointMirrorLeaseRecoveryRequired");
            }
            if (existing.State == "Held")
                throw Refused("LaterCheckpointMirrorLeaseBusy");
            throw Refused("LaterCheckpointMirrorLeaseRecoveryRequired");
        }

        var expires = now.Add(duration);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_leases (
                mirror_id, workpack_id, preparation_id, plan_id,
                checkpoint_id, source_snapshot_id,
                source_connection_id, source_repository_id,
                target_observation_id, staging_evidence_sha256,
                source_full_ref_proof_sha256, source_ref_set_sha256,
                workpack_evidence_sha256, bundle_evidence_sha256,
                bundle_sha256, target_receipt_sha256,
                lease_token, generation, worker_id,
                original_bundle_actor_id, state, acquired_at_utc,
                lease_until_utc, updated_at_utc)
            VALUES (
                {proof.PhysicalMirrorId}, {workpackId}, {preparationId},
                {planId}, {checkpointId}, {proof.SourceSnapshotId},
                {proof.SourceConnectionId}, {sourceRepositoryId},
                {targetObservationId}, {proof.StagingEvidenceSha256},
                {proof.SourceFullRefProofSha256}, {proof.SourceRefSetSha256},
                {proof.WorkpackEvidenceSha256},
                {proof.BundleEvidenceSha256}, {proof.BundleSha256},
                {expectedTargetReceiptSha256}, {token}, {1L},
                {workerId}, {bundleActorId}, {"Held"},
                {now}, {expires}, {now})
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted != 1)
            throw Refused("LaterCheckpointMirrorLeaseBusy");
        await AppendEventAsync(proof.PhysicalMirrorId, 1L,
            workerId, "Acquired", proof.StagingEvidenceSha256, now, ct);
        await tx.CommitAsync(ct);
        return Claim(proof, token, 1L, workerId, bundleActorId, expires);
    }

    // No operator can extend a lease without repeating the independent
    // LIVE full-source/approval read; then database-locked latest source,
    // target review, F-035 file provenance and exact owner are checked.
    // Every renewal is capped by original admission time + 2 hours.
    public async Task<GitIncrementalLaterCheckpointMirrorLeaseClaim>
        RenewAsync(GitIncrementalLaterCheckpointMirrorLeaseClaim lease,
            TimeSpan duration, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorLeaseDisabled");
        RequireHandle(lease, duration);

        var row = await ReadLeaseAsync(lease.MirrorId, ct)
            ?? throw Refused("LaterCheckpointMirrorLeaseMissing");
        if (row.WorkpackId != lease.WorkpackId ||
            row.SourceSnapshotId != lease.SourceSnapshotId ||
            row.CheckpointId != lease.CheckpointId ||
            row.Generation != lease.Generation ||
            row.LeaseToken != lease.LeaseToken ||
            row.WorkerId != lease.WorkerId ||
            row.OriginalBundleActorId != lease.BundleActorId ||
            row.StagingEvidenceSha256 != lease.StagingEvidenceSha256 ||
            row.LeaseUntilUtc != lease.ExpiresAtUtc)
            throw Refused("LaterCheckpointMirrorLeaseFenced");

        var proof = await admission.ReadAsync(row.PreparationId,
            row.PlanId, row.CheckpointId, row.TargetObservationId,
            row.WorkpackId, row.SourceRepositoryId,
            row.TargetReceiptSha256, row.OriginalBundleActorId, ct);
        RequireSameScope(row, proof, row.OriginalBundleActorId);
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await LockAsync(proof.SourceConnectionId, proof.PlanId, ct);
        await RequireLiveLockedAsync(proof, row.OriginalBundleActorId, ct);
        var now = await DatabaseNowAsync(ct);
        if (lease.ExpiresAtUtc <= now ||
            now >= row.AcquiredAtUtc.Add(MaximumLifetime))
            throw Refused("LaterCheckpointMirrorLeaseExpired");
        var expires = now.Add(duration);
        if (expires <= lease.ExpiresAtUtc)
            throw Refused("LaterCheckpointMirrorLeaseNotExtended");
        if (expires > row.AcquiredAtUtc.Add(MaximumLifetime))
            throw Refused("LaterCheckpointMirrorLeaseLifetimeExceeded");

        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE git_later_checkpoint_mirror_leases
            SET lease_until_utc = {expires}, updated_at_utc = {now}
            WHERE mirror_id = {lease.MirrorId}
                AND workpack_id = {lease.WorkpackId}
                AND state = 'Held'
                AND lease_token = {lease.LeaseToken}
                AND generation = {lease.Generation}
                AND worker_id = {lease.WorkerId}
                AND original_bundle_actor_id = {lease.BundleActorId}
                AND staging_evidence_sha256 = {lease.StagingEvidenceSha256}
                AND lease_until_utc = {lease.ExpiresAtUtc}
                AND lease_until_utc > {now}
                AND lease_until_utc < {expires}
                AND NOT can_materialize AND NOT can_push
                AND NOT can_retry AND NOT can_dispatch
                AND NOT migration_complete
            """, ct);
        if (changed != 1)
            throw Refused("LaterCheckpointMirrorLeaseFenced");

        await AppendEventAsync(lease.MirrorId, lease.Generation,
            lease.WorkerId, "Renewed", lease.StagingEvidenceSha256,
            now, ct);
        await tx.CommitAsync(ct);
        return lease with { ExpiresAtUtc = expires };
    }

    // A complete read of the lease is intentionally NOT a trusted
    // statement of ownership: Claim/Renew always recheck under SQL locks.
    private sealed class LeaseRow
    {
        public Guid MirrorId { get; set; }
        public Guid WorkpackId { get; set; }
        public Guid PreparationId { get; set; }
        public Guid PlanId { get; set; }
        public Guid CheckpointId { get; set; }
        public Guid SourceSnapshotId { get; set; }
        public Guid SourceConnectionId { get; set; }
        public string SourceRepositoryId { get; set; } = "";
        public Guid TargetObservationId { get; set; }
        public string StagingEvidenceSha256 { get; set; } = "";
        public string SourceFullRefProofSha256 { get; set; } = "";
        public string SourceRefSetSha256 { get; set; } = "";
        public string WorkpackEvidenceSha256 { get; set; } = "";
        public string BundleEvidenceSha256 { get; set; } = "";
        public string BundleSha256 { get; set; } = "";
        public string TargetReceiptSha256 { get; set; } = "";
        public Guid LeaseToken { get; set; }
        public long Generation { get; set; }
        public Guid WorkerId { get; set; }
        public Guid OriginalBundleActorId { get; set; }
        public string State { get; set; } = "";
        public DateTimeOffset AcquiredAtUtc { get; set; }
        public DateTimeOffset? LeaseUntilUtc { get; set; }
    }

    private Task<LeaseRow?> ReadLeaseAsync(Guid id, CancellationToken ct) =>
        db.Database.SqlQuery<LeaseRow>($"""
            SELECT mirror_id AS "MirrorId", workpack_id AS "WorkpackId",
                preparation_id AS "PreparationId", plan_id AS "PlanId",
                checkpoint_id AS "CheckpointId",
                source_snapshot_id AS "SourceSnapshotId",
                source_connection_id AS "SourceConnectionId",
                source_repository_id AS "SourceRepositoryId",
                target_observation_id AS "TargetObservationId",
                staging_evidence_sha256 AS "StagingEvidenceSha256",
                source_full_ref_proof_sha256 AS "SourceFullRefProofSha256",
                source_ref_set_sha256 AS "SourceRefSetSha256",
                workpack_evidence_sha256 AS "WorkpackEvidenceSha256",
                bundle_evidence_sha256 AS "BundleEvidenceSha256",
                bundle_sha256 AS "BundleSha256",
                target_receipt_sha256 AS "TargetReceiptSha256",
                lease_token AS "LeaseToken", generation AS "Generation",
                worker_id AS "WorkerId",
                original_bundle_actor_id AS "OriginalBundleActorId",
                state AS "State", acquired_at_utc AS "AcquiredAtUtc",
                lease_until_utc AS "LeaseUntilUtc"
            FROM git_later_checkpoint_mirror_leases
            WHERE mirror_id = {id}
            """).SingleOrDefaultAsync(ct);

    private async Task RequireLiveLockedAsync(
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate p,
        Guid actor, CancellationToken ct)
    {
        // The outer #117 preflight verifies immutable SHA contents,
        // human approval and F-035v2 physical bytes before/after an SQL
        // read. This checks MUTABLE latest epoch and the same scope once
        // more while the source-first writer lock is held.
        var plan = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == p.PlanId &&
                x.PreparationId == p.PreparationId &&
                x.SourceConnectionId == p.SourceConnectionId, ct);
        var latestSource = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == p.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var lastCheckpoint = await (
            from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join s in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals s.SnapshotId
            where cp.PlanId == p.PlanId
            orderby s.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        var source = await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.CheckpointId == p.CheckpointId &&
                x.SourceRepositoryId == p.SourceRepositoryId, ct);
        var pack = await db.GitLaterCheckpointWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == p.WorkpackId, ct);
        var bundle = await db.GitLaterCheckpointBundleEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == p.WorkpackId, ct);
        var f035 = await db.GitLaterCheckpointBundleLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == p.WorkpackId, ct);
        var target = await db.GitCheckpointTargetObservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == p.TargetObservationId, ct);
        var human = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == p.CheckpointId &&
                x.SourceRepositoryId == p.SourceRepositoryId, ct);
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == p.PreparationId, ct);
        var dest = plan is null ? null : await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.Id == plan.DestinationConnectionId, ct);
        var now = await DatabaseNowAsync(ct);
        if (plan is null ||
            plan.SnapshotId == p.SourceSnapshotId ||
            plan.SourceConnectionId != p.SourceConnectionId ||
            latestSource != p.SourceSnapshotId ||
            lastCheckpoint is null ||
            lastCheckpoint.Id != p.CheckpointId ||
            lastCheckpoint.SnapshotId != p.SourceSnapshotId ||
            lastCheckpoint.PreparationId != p.PreparationId ||
            lastCheckpoint.SourceConnectionId != p.SourceConnectionId ||
            lastCheckpoint.CanPush || lastCheckpoint.CanRetry ||
            lastCheckpoint.CanDispatch || lastCheckpoint.MigrationComplete ||
            source is null || source.PlanId != p.PlanId ||
            source.SnapshotId != p.SourceSnapshotId ||
            source.SourceConnectionId != p.SourceConnectionId ||
            source.EvidenceSha256 != p.SourceFullRefProofSha256 ||
            source.RecordedBy != actor ||
            pack is null || pack.PlanId != p.PlanId ||
            pack.PreparationId != p.PreparationId ||
            pack.CheckpointId != p.CheckpointId ||
            pack.SourceSnapshotId != p.SourceSnapshotId ||
            pack.SourceRepositoryId != p.SourceRepositoryId ||
            pack.TargetObservationId != p.TargetObservationId ||
            pack.TargetReceiptSha256 != p.TargetReceiptSha256 ||
            pack.FullRefProofSha256 != p.SourceFullRefProofSha256 ||
            pack.EvidenceSha256 != p.WorkpackEvidenceSha256 ||
            pack.RecordedBy != actor ||
            pack.State != "Blocked" || pack.Phase != "WriteBlocked" ||
            pack.ReadyForExecution || pack.CanPush || pack.CanRetry ||
            pack.CanDispatch || pack.MigrationComplete ||
            bundle is null || bundle.PlanId != p.PlanId ||
            bundle.CheckpointId != p.CheckpointId ||
            bundle.SourceSnapshotId != p.SourceSnapshotId ||
            bundle.TargetObservationId != p.TargetObservationId ||
            bundle.SourceRepositoryId != p.SourceRepositoryId ||
            bundle.EvidenceSha256 != p.BundleEvidenceSha256 ||
            bundle.BundleSha256 != p.BundleSha256 ||
            bundle.TargetReceiptSha256 != p.TargetReceiptSha256 ||
            bundle.RecordedBy != actor ||
            bundle.ReadyForExecution || bundle.CanPush || bundle.CanRetry ||
            bundle.CanDispatch || bundle.MigrationComplete ||
            f035 is null || f035.State != "Recorded" ||
            f035.SourceSnapshotId != p.SourceSnapshotId ||
            f035.SourceConnectionId != p.SourceConnectionId ||
            f035.TargetObservationId != p.TargetObservationId ||
            f035.WorkpackEvidenceSha256 != p.WorkpackEvidenceSha256 ||
            f035.FullRefProofSha256 != p.SourceFullRefProofSha256 ||
            f035.ActorId != actor ||
            f035.LeaseUntilUtc is not null ||
            f035.DurableEvidencePersisted ||
            f035.CanPush || f035.CanRetry ||
            f035.CanDispatch || f035.MigrationComplete ||
            target is null || target.CheckpointId != p.CheckpointId ||
            target.SourceSnapshotId != p.SourceSnapshotId ||
            target.SourceRepositoryId != p.SourceRepositoryId ||
            target.EvidenceSha256 != p.TargetReceiptSha256 ||
            target.CanObserveTarget || target.CanPush ||
            target.CanRetry || target.CanDispatch || target.MigrationComplete ||
            human is null || human.CheckpointId != p.CheckpointId ||
            human.SourceSnapshotId != p.SourceSnapshotId ||
            human.SourceRepositoryId != p.SourceRepositoryId ||
            human.Decision !=
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            human.ReviewedBy != target.ReviewedBy ||
            human.EvidenceSha256 != target.HumanReviewEvidenceSha256 ||
            human.CanObserveTarget || human.CanPush ||
            human.CanRetry || human.CanDispatch || human.MigrationComplete ||
            review is null ||
            review.State != IncrementalReviewStates.ApprovedForPlanning ||
            review.DryRunSha256 != plan.ReviewSha256 ||
            dest is null || dest.ValidationCode != "Verified" ||
            dest.ValidatedAtUtc is null || now < dest.ValidatedAtUtc ||
            now - dest.ValidatedAtUtc >= TimeSpan.FromMinutes(5) ||
            now < target.ObservedAtUtc ||
            now - target.ObservedAtUtc >= TimeSpan.FromMinutes(5))
            throw Refused("LaterCheckpointMirrorLeaseContextChanged");
    }

    private static void RequireSameScope(LeaseRow row,
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate p, Guid actor)
    {
        if (row.MirrorId != p.PhysicalMirrorId ||
            row.WorkpackId != p.WorkpackId ||
            row.PreparationId != p.PreparationId ||
            row.PlanId != p.PlanId || row.CheckpointId != p.CheckpointId ||
            row.SourceSnapshotId != p.SourceSnapshotId ||
            row.SourceConnectionId != p.SourceConnectionId ||
            row.SourceRepositoryId != p.SourceRepositoryId ||
            row.TargetObservationId != p.TargetObservationId ||
            row.StagingEvidenceSha256 != p.StagingEvidenceSha256 ||
            row.SourceFullRefProofSha256 != p.SourceFullRefProofSha256 ||
            row.SourceRefSetSha256 != p.SourceRefSetSha256 ||
            row.WorkpackEvidenceSha256 != p.WorkpackEvidenceSha256 ||
            row.BundleEvidenceSha256 != p.BundleEvidenceSha256 ||
            row.BundleSha256 != p.BundleSha256 ||
            row.TargetReceiptSha256 != p.TargetReceiptSha256 ||
            row.OriginalBundleActorId != actor)
            throw Refused("LaterCheckpointMirrorLeaseScopeChanged");
    }

    private static void RequireHandle(
        GitIncrementalLaterCheckpointMirrorLeaseClaim lease,
        TimeSpan duration)
    {
        if (lease is null || lease.MirrorId == Guid.Empty ||
            lease.WorkpackId == Guid.Empty || lease.SourceSnapshotId == Guid.Empty ||
            lease.CheckpointId == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.WorkerId == Guid.Empty || lease.BundleActorId == Guid.Empty ||
            lease.Generation is < 1 or > 2000 ||
            !GitSourceBundleStorageCheck.ValidSha(
                lease.StagingEvidenceSha256) ||
            duration < MinimumLease || duration > MaximumLease ||
            lease.CanMaterialize || lease.CanPush || lease.CanRetry ||
            lease.CanDispatch || lease.MigrationComplete)
            throw Refused("LaterCheckpointMirrorLeaseHandleInvalid");
    }

    private static GitIncrementalLaterCheckpointMirrorLeaseClaim Claim(
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate p,
        Guid token, long generation, Guid workerId, Guid bundleActorId,
        DateTimeOffset expires) => new(p.PhysicalMirrorId, p.WorkpackId,
            p.SourceSnapshotId, p.CheckpointId, token, generation,
            workerId, bundleActorId, p.StagingEvidenceSha256, expires,
            false, false, false, false, false);

    private async Task LockAsync(
        Guid sourceConnectionId, Guid planId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({sourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);
    }

    // All lease deadline/fencing decisions use PostgreSQL clock authority
    // (never a caller, host or injected TimeProvider).
    private Task<DateTimeOffset> DatabaseNowAsync(CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>(
            $"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private Task<int> AppendEventAsync(Guid id, long gen, Guid worker,
        string type, string stageSha, DateTimeOffset now,
        CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_lease_events (
                mirror_id, generation, worker_id, type,
                staging_evidence_sha256, occurred_at_utc)
            VALUES ({id}, {gen}, {worker}, {type}, {stageSha}, {now})
            """, ct);
}
