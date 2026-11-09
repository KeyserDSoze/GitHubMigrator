using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #119: distinct source-only, independently OFF-by-default final F-061v2
// evidence writer. The physical Git importer/auditor NEVER runs inside
// this SQL transaction and no GitHub destination API is reachable.
public sealed record GitIncrementalLaterCheckpointMirrorVerifiedOptions(
    bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorVerifiedJournal(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorVerifiedOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore leaseStore,
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight admission,
    PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight staging,
    PostgresGitIncrementalLaterCheckpointBundleHistory durableHistory,
    IGitIncrementalLaterCheckpointMirrorPhysicalAuditor physical,
    IGitSourceBundleArtifactVerifier fileVerifier)
{
    public static readonly TimeSpan MaximumPhysicalProofAge =
        TimeSpan.FromMinutes(2);

    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    public async Task<GitIncrementalLaterCheckpointMirrorVerifiedReceipt>
        CompleteAsync(
            GitIncrementalLaterCheckpointMirrorLeaseClaim lease,
            Guid preparationId, Guid planId,
            Guid targetObservationId, string sourceRepositoryId,
            string expectedTargetReceiptSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorVerifiedDisabled");
        if (lease is null || lease.MirrorId == Guid.Empty ||
            lease.WorkpackId == Guid.Empty || lease.CheckpointId == Guid.Empty ||
            lease.SourceSnapshotId == Guid.Empty ||
            lease.LeaseToken == Guid.Empty || lease.WorkerId == Guid.Empty ||
            lease.BundleActorId == Guid.Empty ||
            lease.Generation is < 1 or > 2000 ||
            lease.CanMaterialize || lease.CanPush || lease.CanRetry ||
            lease.CanDispatch || lease.MigrationComplete ||
            !GitSourceBundleStorageCheck.ValidSha(
                lease.StagingEvidenceSha256))
            throw Refused("LaterCheckpointMirrorVerifiedLeaseInvalid");

        // Never take a caller-supplied path, SHA-only offline receipt, ref
        // list or Git object OID as evidence. The trusted readers fetch
        // sealed PostgreSQL stage and F-035v2; history physically rehashes
        // UUID.bundle and verifies immutable prior F-035v2 events.
        var stage = await staging.ReadAsync(preparationId, planId,
            lease.CheckpointId, targetObservationId, lease.WorkpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            lease.BundleActorId, ct);
        var durable = await durableHistory.ReadAsync(preparationId,
            planId, lease.CheckpointId, targetObservationId,
            lease.WorkpackId, sourceRepositoryId,
            expectedTargetReceiptSha256, lease.BundleActorId, ct)
            ?? throw Refused("LaterCheckpointMirrorVerifiedBundleMissing");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        RequireStage(stage, lease);

        var original = await leaseStore.ReadLeaseAsync(lease.MirrorId, ct)
            ?? throw Refused("LaterCheckpointMirrorVerifiedLeaseMissing");
        RequireExactLease(original, lease);
        if (original.State != "Held" ||
            original.LeaseUntilUtc != lease.ExpiresAtUtc)
            throw Refused("LaterCheckpointMirrorVerifiedLeaseFenced");

        // Real bare-Git fsck and exact full F-097b ref/OID closure is
        // done TWICE by the trusted physical auditor, without any SQL
        // transaction/long-held source advisory lock. It checks local
        // UUID.bundle bytes before AND after Git and uses safe roots.
        var observed = await physical.AuditAsync(stage, durable, ct);
        GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
            .Verify(observed, stage, durable);
        if (observed.VerifiedAtUtc < original.AcquiredAtUtc)
            throw Refused("LaterCheckpointMirrorVerifiedProofPreLease");

        // Source or target approval could have been revoked while Git
        // was being verified. Re-run the entire independent #117 live
        // gate AND physical F-035v2 file read before final transaction.
        var latest = await admission.ReadAsync(preparationId, planId,
            lease.CheckpointId, targetObservationId, lease.WorkpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            lease.BundleActorId, ct);
        if (latest.PhysicalMirrorId != stage.PhysicalMirrorId ||
            latest.SourceSnapshotId != stage.SourceSnapshotId ||
            latest.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            latest.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            latest.BundleSha256 != durable.BundleSha256 ||
            latest.SourceFullRefProofSha256 != stage.SourceFullRefProofSha256 ||
            latest.SourceRefSetSha256 != stage.SourceRefSetSha256)
            throw Refused("LaterCheckpointMirrorVerifiedAdmissionChanged");

        // Only short DB and on-disk bundle SHA rehash work under source
        // lock; never execute a Git subprocess within a DB transaction.
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await leaseStore.LockAsync(stage.SourceConnectionId, planId, ct);
        await leaseStore.RequireLiveLockedAsync(
            latest, lease.BundleActorId, ct);
        var now = await leaseStore.DatabaseNowAsync(ct);
        var current = await leaseStore.ReadLeaseAsync(lease.MirrorId, ct)
            ?? throw Refused("LaterCheckpointMirrorVerifiedLeaseMissing");
        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore
            .RequireSameScope(current, latest, lease.BundleActorId);
        RequireExactLease(current, lease);
        if (current.State != "Held" ||
            current.LeaseUntilUtc != lease.ExpiresAtUtc ||
            current.LeaseUntilUtc <= now ||
            now < observed.VerifiedAtUtc ||
            now - observed.VerifiedAtUtc > MaximumPhysicalProofAge ||
            observed.VerifiedAtUtc < current.AcquiredAtUtc)
            throw Refused("LaterCheckpointMirrorVerifiedLeaseFenced");

        // This verifier only reopens trusted UUID.bundle and rehashes
        // its bytes, no Git. A saved SHA or prior filesystem check is
        // not enough at the last source-locked PostgreSQL boundary.
        var measured = await fileVerifier.VerifyAsync(
            new GitSourceBundleView(lease.WorkpackId, planId, Guid.Empty,
                sourceRepositoryId, durable.SourceEvidenceSha256,
                durable.ReconciliationSha256, durable.BundleFileName,
                durable.BundleSha256, durable.BundleBytes, durable.RefCount,
                durable.AcquiredAtUtc, false, false), ct);
        if (measured.WorkpackId != lease.WorkpackId ||
            measured.BundleSha256 != durable.BundleSha256 ||
            measured.BundleBytes != durable.BundleBytes ||
            measured.IsCurrent || measured.ReadyForExecution)
            throw Refused("LaterCheckpointMirrorVerifiedBundleChanged");

        var proof = GitIncrementalLaterCheckpointMirrorVerifiedIntegrity.Seal(
            stage, durable, observed, lease.WorkerId,
            lease.LeaseToken, lease.Generation, now);
        var existing = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_verified_evidence
            WHERE mirror_id = {lease.MirrorId} OR
                workpack_id = {lease.WorkpackId}
            """).SingleAsync(ct);
        if (existing != 0)
            throw Refused("LaterCheckpointMirrorVerifiedAlreadyRecorded");

        // PostgreSQL BEFORE INSERT verifies every immutable property
        // against the Held lease + its exact unexpired generation.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_verified_evidence (
                mirror_id, workpack_id, evidence_version,
                preparation_id, plan_id, source_snapshot_id,
                checkpoint_id, target_observation_id,
                worker_id, lease_token, lease_generation,
                staging_evidence_sha256, full_ref_proof_sha256,
                bundle_evidence_sha256, bundle_sha256,
                source_ref_set_sha256, git_object_format,
                object_set_sha256, object_count,
                physical_verified_at_utc, recorded_at_utc,
                evidence_sha256, durable_evidence_persisted,
                can_materialize, can_push, can_retry, can_dispatch,
                migration_complete)
            VALUES ({proof.PhysicalMirrorId}, {proof.WorkpackId},
                {proof.EvidenceVersion}, {proof.PreparationId},
                {proof.PlanId}, {proof.SourceSnapshotId},
                {proof.CheckpointId}, {proof.TargetObservationId},
                {proof.WorkerId}, {proof.LeaseToken},
                {proof.LeaseGeneration}, {proof.StagingEvidenceSha256},
                {proof.FullRefProofSha256}, {proof.BundleEvidenceSha256},
                {proof.BundleSha256}, {proof.SourceRefSetSha256},
                {proof.GitObjectFormat}, {proof.ObjectSetSha256},
                {proof.ObjectCount}, {proof.PhysicalVerifiedAtUtc},
                {proof.RecordedAtUtc}, {proof.EvidenceSha256},
                {true}, {false}, {false}, {false}, {false}, {false})
            """, ct);

        // Fenced transition to a TERMINAL Verified state and the single
        // immutable Verified event are committed with the receipt.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE git_later_checkpoint_mirror_leases
            SET state = 'Verified', lease_until_utc = NULL,
                object_set_sha256 = {proof.ObjectSetSha256},
                object_count = {proof.ObjectCount},
                mirror_proof_sha256 = {proof.EvidenceSha256},
                verified_at_utc = {proof.PhysicalVerifiedAtUtc},
                has_verified_local_objects = true,
                updated_at_utc = {proof.RecordedAtUtc}
            WHERE mirror_id = {lease.MirrorId}
                AND workpack_id = {lease.WorkpackId}
                AND state = 'Held'
                AND worker_id = {lease.WorkerId}
                AND lease_token = {lease.LeaseToken}
                AND generation = {lease.Generation}
                AND staging_evidence_sha256 = {lease.StagingEvidenceSha256}
                AND lease_until_utc = {lease.ExpiresAtUtc}
                AND lease_until_utc > {proof.RecordedAtUtc}
                AND NOT has_verified_local_objects
                AND NOT can_materialize
                AND NOT can_push AND NOT can_retry
                AND NOT can_dispatch AND NOT migration_complete
            """, ct);
        if (updated != 1)
            throw Refused("LaterCheckpointMirrorVerifiedLeaseFenced");

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_lease_events (
                mirror_id, generation, worker_id, type,
                staging_evidence_sha256, occurred_at_utc)
            VALUES ({lease.MirrorId}, {lease.Generation},
                {lease.WorkerId}, {"Verified"},
                {lease.StagingEvidenceSha256}, {proof.RecordedAtUtc})
            """, ct);
        await tx.CommitAsync(ct);
        return proof;
    }

    private static void RequireStage(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointMirrorLeaseClaim lease)
    {
        if (stage.PhysicalMirrorId != lease.MirrorId ||
            stage.WorkpackId != lease.WorkpackId ||
            stage.SourceSnapshotId != lease.SourceSnapshotId ||
            stage.CheckpointId != lease.CheckpointId ||
            stage.EvidenceSha256 != lease.StagingEvidenceSha256 ||
            stage.CanMaterialize || stage.CanPush || stage.CanRetry ||
            stage.CanDispatch || stage.MigrationComplete)
            throw Refused("LaterCheckpointMirrorVerifiedScopeChanged");
    }

    private static void RequireExactLease(
        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore.LeaseRow row,
        GitIncrementalLaterCheckpointMirrorLeaseClaim lease)
    {
        if (row.MirrorId != lease.MirrorId ||
            row.WorkpackId != lease.WorkpackId ||
            row.SourceSnapshotId != lease.SourceSnapshotId ||
            row.CheckpointId != lease.CheckpointId ||
            row.StagingEvidenceSha256 != lease.StagingEvidenceSha256 ||
            row.WorkerId != lease.WorkerId ||
            row.OriginalBundleActorId != lease.BundleActorId ||
            row.Generation != lease.Generation ||
            row.LeaseToken != lease.LeaseToken)
            throw Refused("LaterCheckpointMirrorVerifiedLeaseFenced");
    }
}
