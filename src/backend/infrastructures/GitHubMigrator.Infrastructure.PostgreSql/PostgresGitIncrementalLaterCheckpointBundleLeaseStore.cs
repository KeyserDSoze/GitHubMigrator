using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #113: sole-main v2 local bundle coordination. Nothing in this class
// writes a bundle file, a remote GitHub ref, or a durable F-035v2 receipt.
// The held lease is a fencing handle, NEVER execution permission.
public sealed record GitIncrementalLaterCheckpointBundleLeaseOptions(bool Enabled);

public sealed record GitIncrementalLaterCheckpointBundleLeaseClaim(
    Guid WorkpackId, Guid LeaseToken, long Generation, Guid ActorId,
    string BundlePlanSha256, DateTimeOffset ExpiresAtUtc,
    bool CanAcquireBundle, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public sealed class PostgresGitIncrementalLaterCheckpointBundleLeaseStore(
    AppDbContext db, TimeProvider clock,
    GitIncrementalLaterCheckpointBundleLeaseOptions options,
    IGitIncrementalLaterCheckpointBundleReadPreflight preflight)
{
    public static readonly TimeSpan MaxLease = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MinLease = TimeSpan.FromSeconds(30);

    private static GitSourceBundleException Refused(string code) => new(code);

    public async Task<GitIncrementalLaterCheckpointBundleLeaseClaim> ClaimAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, Guid actorId, TimeSpan duration,
        CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointBundleLeaseDisabled");
        if (actorId == Guid.Empty || duration < MinLease ||
            duration > MaxLease)
            throw Refused("LaterCheckpointBundleLeaseRequestInvalid");
        var plan = await preflight.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256, ct);
        RequirePlan(plan, preparationId, planId, checkpointId,
            targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256);
        var now = clock.GetUtcNow();
        var expires = now.Add(duration);
        var token = Guid.NewGuid();

        // Shared advisory locks cover the SOURCE and the plan (source
        // first, matching F-029/F-091/F-032v2). Never execute Git while
        // holding these locks: only a short database-only lease claim.
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await LockAsync(plan.SourceOnlyRequest.SourceConnectionId, planId, ct);
        await RequireLiveLockedAsync(plan, actorId, ct);
        var held = await db.GitLaterCheckpointBundleLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == workpackId, ct);
        if (held is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO git_later_checkpoint_bundle_leases (
                    workpack_id, preparation_id, plan_id, checkpoint_id,
                    source_snapshot_id, source_connection_id,
                    target_observation_id, source_repository_id,
                    bundle_plan_sha256, workpack_evidence_sha256,
                    full_ref_proof_sha256, target_receipt_sha256,
                    lease_token, generation, actor_id, state,
                    acquired_at_utc, lease_until_utc, updated_at_utc,
                    durable_evidence_persisted, can_push, can_retry,
                    can_dispatch, migration_complete)
                VALUES ({workpackId}, {preparationId}, {planId},
                    {checkpointId}, {plan.SourceSnapshotId},
                    {plan.SourceOnlyRequest.SourceConnectionId},
                    {targetObservationId}, {sourceRepositoryId},
                    {plan.EvidenceSha256}, {plan.WorkpackEvidenceSha256},
                    {plan.SourceFullRefProofSha256},
                    {plan.TargetReceiptSha256}, {token}, {1L}, {actorId},
                    'Held', {now}, {expires}, {now},
                    false, false, false, false, false)
                """, ct);
            await EventAsync(workpackId, 1L, actorId, "Acquired",
                plan.EvidenceSha256, now, ct);
            await tx.CommitAsync(ct);
            return Claim(workpackId, token, 1L, actorId,
                plan.EvidenceSha256, expires);
        }

        RequireMatch(held, plan);
        if (held.State == "Held" && held.LeaseUntilUtc <= now)
        {
            // CRASH/EXPIRED is quarantined: no silent takeover of an
            // existing UUID.bundle file or of a possibly dead worker.
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE git_later_checkpoint_bundle_leases
                SET state = 'RecoveryRequired', lease_until_utc = NULL,
                    updated_at_utc = {now}
                WHERE workpack_id = {workpackId} AND state = 'Held'
                    AND generation = {held.Generation}
                    AND lease_token = {held.LeaseToken}
                """, ct);
            if (changed != 1)
                throw Refused("LaterCheckpointBundleLeaseLost");
            await EventAsync(workpackId, held.Generation, actorId,
                "Quarantined", plan.EvidenceSha256, now, ct);
            await tx.CommitAsync(ct);
            throw Refused("LaterCheckpointBundleLeaseRecoveryRequired");
        }
        if (held.State == "Held")
            throw Refused("LaterCheckpointBundleLeaseBusy");
        if (held.State == "RecoveryRequired")
            throw Refused("LaterCheckpointBundleLeaseRecoveryRequired");
        if (held.State == "Recorded")
            throw Refused("LaterCheckpointBundleLeaseAlreadyRecorded");
        if (held.State != "Released" || held.Generation >= long.MaxValue - 1)
            throw Refused("LaterCheckpointBundleLeaseStateInvalid");
        var generation = checked(held.Generation + 1);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE git_later_checkpoint_bundle_leases
            SET lease_token = {token}, generation = {generation},
                actor_id = {actorId}, state = 'Held',
                acquired_at_utc = {now}, lease_until_utc = {expires},
                updated_at_utc = {now}
            WHERE workpack_id = {workpackId} AND state = 'Released'
                AND generation = {held.Generation}
                AND lease_token = {held.LeaseToken}
                AND bundle_plan_sha256 = {plan.EvidenceSha256}
                AND durable_evidence_persisted = false
                AND can_push = false AND can_retry = false
                AND can_dispatch = false AND migration_complete = false
            """, ct);
        if (updated != 1)
            throw Refused("LaterCheckpointBundleLeaseLost");
        await EventAsync(workpackId, generation, actorId, "Acquired",
            plan.EvidenceSha256, now, ct);
        await tx.CommitAsync(ct);
        return Claim(workpackId, token, generation, actorId,
            plan.EvidenceSha256, expires);
    }

    // Released requires the exact token, generation, owner and still-live
    // time window; stale or expired workers may not release another lease.
    public async Task<bool> ReleaseAsync(
        Guid workpackId, Guid leaseToken, long generation, Guid actorId,
        string bundlePlanSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointBundleLeaseDisabled");
        RequireHandle(workpackId, leaseToken, generation, actorId,
            bundlePlanSha256);
        var row = await db.GitLaterCheckpointBundleLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == workpackId, ct);
        if (row is null) return false;
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await LockAsync(row.SourceConnectionId, row.PlanId, ct);
        var now = clock.GetUtcNow();
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE git_later_checkpoint_bundle_leases
            SET state = 'Released', lease_until_utc = NULL,
                updated_at_utc = {now}
            WHERE workpack_id = {workpackId} AND state = 'Held'
                AND lease_token = {leaseToken}
                AND generation = {generation} AND actor_id = {actorId}
                AND bundle_plan_sha256 = {bundlePlanSha256}
                AND lease_until_utc > {now}
                AND durable_evidence_persisted = false
                AND can_push = false AND can_retry = false
                AND can_dispatch = false AND migration_complete = false
            """, ct);
        if (changed == 1)
            await EventAsync(workpackId, generation, actorId, "Released",
                bundlePlanSha256, now, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    private async Task RequireLiveLockedAsync(
        GitIncrementalLaterCheckpointBundlePlan plan, Guid actorId,
        CancellationToken ct)
    {
        var req = plan.SourceOnlyRequest;
        var storedPlan = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == plan.PlanId &&
                x.PreparationId == plan.PreparationId &&
                x.SourceConnectionId == req.SourceConnectionId, ct);
        var lastSnapshot = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == req.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var latest = await (
            from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join snap in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals snap.SnapshotId
            where cp.PlanId == plan.PlanId
            orderby snap.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        var pack = await db.GitLaterCheckpointWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == req.WorkpackId, ct);
        var source = await db.GitHubSourceCheckpointGitRefLedgers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == plan.CheckpointId &&
                x.SourceRepositoryId == req.SourceRepositoryId, ct);
        var target = await db.GitCheckpointTargetObservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == plan.TargetObservationId, ct);
        var human = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == plan.CheckpointId &&
                x.SourceRepositoryId == req.SourceRepositoryId, ct);
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == plan.PreparationId, ct);
        var dest = storedPlan is null ? null :
            await db.Connections.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id ==
                    storedPlan.DestinationConnectionId, ct);
        var now = clock.GetUtcNow();
        if (storedPlan is null ||
            storedPlan.SnapshotId != plan.BaselineSnapshotId ||
            storedPlan.SourceConnectionId != req.SourceConnectionId ||
            lastSnapshot != plan.SourceSnapshotId ||
            latest is null || latest.Id != plan.CheckpointId ||
            latest.SnapshotId != plan.SourceSnapshotId ||
            latest.PreparationId != plan.PreparationId ||
            latest.SourceConnectionId != req.SourceConnectionId ||
            latest.CanPush || latest.CanRetry ||
            latest.CanDispatch || latest.MigrationComplete ||
            pack is null || pack.PlanId != plan.PlanId ||
            pack.PreparationId != plan.PreparationId ||
            pack.CheckpointId != plan.CheckpointId ||
            pack.SourceRepositoryId != req.SourceRepositoryId ||
            pack.SourceSnapshotId != plan.SourceSnapshotId ||
            pack.TargetObservationId != plan.TargetObservationId ||
            pack.TargetReceiptSha256 != plan.TargetReceiptSha256 ||
            pack.FullRefProofSha256 != plan.SourceFullRefProofSha256 ||
            pack.EvidenceSha256 != plan.WorkpackEvidenceSha256 ||
            pack.RecordedBy != actorId ||
            pack.State != "Blocked" || pack.Phase != "WriteBlocked" ||
            pack.ReadyForExecution || pack.CanPush ||
            pack.CanRetry || pack.CanDispatch || pack.MigrationComplete ||
            source is null || source.PlanId != plan.PlanId ||
            source.SnapshotId != plan.SourceSnapshotId ||
            source.SourceConnectionId != req.SourceConnectionId ||
            source.EvidenceSha256 != plan.SourceFullRefProofSha256 ||
            source.SourceEvidenceSha256 != req.SourceEvidenceSha256 ||
            source.SourceManifestRootSha256 != plan.SourceManifestRootSha256 ||
            source.RecordedBy != actorId ||
            target is null || target.PlanId != plan.PlanId ||
            target.CheckpointId != plan.CheckpointId ||
            target.SourceSnapshotId != plan.SourceSnapshotId ||
            target.DestinationConnectionId != storedPlan.DestinationConnectionId ||
            target.SourceRepositoryId != req.SourceRepositoryId ||
            target.EvidenceSha256 != plan.TargetReceiptSha256 ||
            target.CanObserveTarget || target.CanPush ||
            target.CanRetry || target.CanDispatch || target.MigrationComplete ||
            human is null || human.PreparationId != plan.PreparationId ||
            human.PlanId != plan.PlanId ||
            human.CheckpointId != plan.CheckpointId ||
            human.SourceSnapshotId != plan.SourceSnapshotId ||
            human.SourceRepositoryId != req.SourceRepositoryId ||
            human.EvidenceSha256 != target.HumanReviewEvidenceSha256 ||
            human.Decision !=
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            human.ReviewedBy != target.ReviewedBy ||
            human.CanObserveTarget || human.CanPush ||
            human.CanRetry || human.CanDispatch || human.MigrationComplete ||
            review is null ||
            review.State != IncrementalReviewStates.ApprovedForPlanning ||
            review.DryRunSha256 != storedPlan.ReviewSha256 ||
            dest is null || dest.ValidationCode != "Verified" ||
            dest.ValidatedAtUtc is null || now < dest.ValidatedAtUtc ||
            now - dest.ValidatedAtUtc >= TimeSpan.FromMinutes(5) ||
            now < target.ObservedAtUtc ||
            now - target.ObservedAtUtc >= TimeSpan.FromMinutes(5))
            throw Refused("LaterCheckpointBundleLeaseContextChanged");
    }

    private static void RequirePlan(
        GitIncrementalLaterCheckpointBundlePlan? plan, Guid prep, Guid planId,
        Guid checkpoint, Guid observation, Guid workpack, string repository,
        string targetSha)
    {
        if (plan is null || plan.EvidenceVersion != 1 ||
            plan.PreparationId != prep || plan.PlanId != planId ||
            plan.CheckpointId != checkpoint ||
            plan.TargetObservationId != observation ||
            plan.BaselineSnapshotId == plan.SourceSnapshotId ||
            plan.SourceOnlyRequest is null ||
            plan.SourceOnlyRequest.WorkpackId != workpack ||
            plan.SourceOnlyRequest.SourceConnectionId == Guid.Empty ||
            plan.SourceOnlyRequest.SourceRepositoryId != repository ||
            plan.TargetReceiptSha256 != targetSha ||
            !GitSourceBundleStorageCheck.ValidSha(plan.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.WorkpackEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.SourceFullRefProofSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.TargetReceiptSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.SourceManifestRootSha256) ||
            plan.CanAcquireBundle || plan.CanPush || plan.CanRetry ||
            plan.CanDispatch || plan.MigrationComplete)
            throw Refused("LaterCheckpointBundleLeasePlanInvalid");
    }

    private static void RequireMatch(
        GitLaterCheckpointBundleLeaseRecord held,
        GitIncrementalLaterCheckpointBundlePlan plan)
    {
        var req = plan.SourceOnlyRequest;
        if (held.PreparationId != plan.PreparationId ||
            held.PlanId != plan.PlanId ||
            held.CheckpointId != plan.CheckpointId ||
            held.SourceSnapshotId != plan.SourceSnapshotId ||
            held.SourceConnectionId != req.SourceConnectionId ||
            held.TargetObservationId != plan.TargetObservationId ||
            held.SourceRepositoryId != req.SourceRepositoryId ||
            held.BundlePlanSha256 != plan.EvidenceSha256 ||
            held.WorkpackEvidenceSha256 != plan.WorkpackEvidenceSha256 ||
            held.FullRefProofSha256 != plan.SourceFullRefProofSha256 ||
            held.TargetReceiptSha256 != plan.TargetReceiptSha256 ||
            held.DurableEvidencePersisted || held.CanPush ||
            held.CanRetry || held.CanDispatch || held.MigrationComplete)
            throw Refused("LaterCheckpointBundleLeaseScopeChanged");
    }

    private static void RequireHandle(Guid workpack, Guid token, long generation,
        Guid actor, string planSha)
    {
        if (workpack == Guid.Empty || token == Guid.Empty ||
            generation < 1 || actor == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(planSha))
            throw Refused("LaterCheckpointBundleLeaseHandleInvalid");
    }

    private static GitIncrementalLaterCheckpointBundleLeaseClaim Claim(
        Guid workpack, Guid token, long generation, Guid actor,
        string sha, DateTimeOffset expires) =>
        new(workpack, token, generation, actor, sha, expires,
            false, false, false, false, false);

    private Task LockAsync(Guid connection, Guid plan, CancellationToken ct) =>
        LockBothAsync(connection, plan, ct);

    private async Task LockBothAsync(Guid connection, Guid plan, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({connection.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({plan.ToString()}), 732990)", ct);
    }

    private Task<int> EventAsync(Guid workpack, long generation, Guid actor,
        string type, string sha, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_bundle_lease_events (
                workpack_id, generation, actor_id, type,
                bundle_plan_sha256, occurred_at_utc)
            VALUES ({workpack}, {generation}, {actor},
                {type}, {sha}, {now})
            """, ct);
}
