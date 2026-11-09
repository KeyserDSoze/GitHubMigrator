using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #117: non-authorizing source-first PostgreSQL read fence. The existing
// F-091/F-097b/F-031 live verifier opens its own short transactions;
// never wrap those readers in a nested EF transaction. Instead verify
// before AND after one source-locked, exact SQL snapshot of the immutable
// v2 workpack + physical F-035v2 rows. No Git runs under this lock.
public sealed record GitIncrementalLaterCheckpointMirrorAdmissionOptions(
    bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight(
    AppDbContext db, TimeProvider clock,
    GitIncrementalLaterCheckpointMirrorAdmissionOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight staging,
    PostgresGitIncrementalLaterCheckpointBundleHistory bundleHistory)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    public async Task<GitIncrementalLaterCheckpointMirrorAdmissionCandidate>
        ReadAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorAdmissionDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            workpackId == Guid.Empty || originalBundleActorId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !GitSourceBundleStorageCheck.ValidSha(expectedTargetReceiptSha256))
            throw Refused("LaterCheckpointMirrorAdmissionInvalidScope");

        // Independent historical F-035v2 replay ALSO rehashes the exact
        // UUID.bundle bytes and checks its own immutable PostgreSQL events.
        var before = await staging.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        var durable = await bundleHistory.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct)
            ?? throw Refused("LaterCheckpointMirrorAdmissionBundleMissing");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(before, durable);

        // Lock order matches the F-035v2 writer exactly: source FIRST,
        // plan SECOND. Advisory transaction locks are short-lived.
        // No physical Git subprocess or file mutation runs in this tx.
        await using (var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({before.SourceConnectionId.ToString()}), 732911)",
                ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)",
                ct);
            var workpack = await db.GitLaterCheckpointWorkpacks
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == workpackId && x.PreparationId == preparationId &&
                    x.PlanId == planId && x.CheckpointId == checkpointId &&
                    x.TargetObservationId == targetObservationId &&
                    x.SourceRepositoryId == sourceRepositoryId, ct);
            var bundle = await db.GitLaterCheckpointBundleEvidence
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.WorkpackId == workpackId &&
                    x.PreparationId == preparationId &&
                    x.PlanId == planId &&
                    x.CheckpointId == checkpointId &&
                    x.TargetObservationId == targetObservationId &&
                    x.SourceRepositoryId == sourceRepositoryId, ct);
            var recordedLease = await db.GitLaterCheckpointBundleLeases
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.WorkpackId == workpackId &&
                    x.PreparationId == preparationId &&
                    x.PlanId == planId &&
                    x.CheckpointId == checkpointId &&
                    x.TargetObservationId == targetObservationId &&
                    x.SourceRepositoryId == sourceRepositoryId, ct);
            if (workpack is null || bundle is null ||
                recordedLease is null ||
                recordedLease.State != "Recorded" ||
                recordedLease.SourceConnectionId != before.SourceConnectionId ||
                recordedLease.SourceSnapshotId != before.SourceSnapshotId ||
                recordedLease.BundlePlanSha256 != before.BundlePlanSha256 ||
                recordedLease.WorkpackEvidenceSha256 !=
                    before.WorkpackEvidenceSha256 ||
                recordedLease.FullRefProofSha256 !=
                    before.SourceFullRefProofSha256 ||
                recordedLease.TargetReceiptSha256 !=
                    before.TargetReceiptSha256 ||
                recordedLease.ActorId != originalBundleActorId ||
                recordedLease.LeaseUntilUtc is not null ||
                recordedLease.DurableEvidencePersisted ||
                recordedLease.CanPush || recordedLease.CanRetry ||
                recordedLease.CanDispatch || recordedLease.MigrationComplete ||
                workpack.EvidenceSha256 != before.WorkpackEvidenceSha256 ||
                workpack.FullRefProofSha256 !=
                    before.SourceFullRefProofSha256 ||
                workpack.TargetReceiptSha256 != before.TargetReceiptSha256 ||
                workpack.SourceSnapshotId != before.SourceSnapshotId ||
                workpack.State != "Blocked" ||
                workpack.Phase != "WriteBlocked" ||
                workpack.ReadyForExecution || workpack.CanPush ||
                workpack.CanRetry || workpack.CanDispatch ||
                workpack.MigrationComplete ||
                bundle.SourceSnapshotId != before.SourceSnapshotId ||
                bundle.EvidenceSha256 != before.BundleEvidenceSha256 ||
                bundle.BundlePlanSha256 != before.BundlePlanSha256 ||
                bundle.WorkpackEvidenceSha256 != before.WorkpackEvidenceSha256 ||
                bundle.FullRefProofSha256 != before.SourceFullRefProofSha256 ||
                bundle.TargetReceiptSha256 != before.TargetReceiptSha256 ||
                bundle.BundleSha256 != before.BundleSha256 ||
                bundle.BundleBytes != before.BundleBytes ||
                bundle.RecordedBy != originalBundleActorId ||
                bundle.ReadyForExecution || bundle.CanPush ||
                bundle.CanRetry || bundle.CanDispatch ||
                bundle.MigrationComplete)
                throw Refused("LaterCheckpointMirrorAdmissionLockedRowsChanged");
            await tx.CommitAsync(ct);
        }

        // A short-lived SQL review is NOT a lease. Re-run all live
        // source/approval/snapshot checks after unlocking so a revocation
        // cannot turn a stale candidate into an F-061 authority.
        var after = await staging.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        var afterDurable = await bundleHistory.ReadAsync(preparationId,
            planId, checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct)
            ?? throw Refused("LaterCheckpointMirrorAdmissionBundleChanged");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(after, afterDurable);
        if (before with { SourceRefs = after.SourceRefs } != after ||
            !before.SourceRefs.SequenceEqual(after.SourceRefs) ||
            afterDurable != durable)
            throw Refused("LaterCheckpointMirrorAdmissionContextChanged");

        // Sealed for independent review; NOT returned to a worker or
        // exposed by HTTP, and cannot claim/renew/complete F-061.
        return GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Seal(
            after, afterDurable, clock.GetUtcNow());
    }
}
