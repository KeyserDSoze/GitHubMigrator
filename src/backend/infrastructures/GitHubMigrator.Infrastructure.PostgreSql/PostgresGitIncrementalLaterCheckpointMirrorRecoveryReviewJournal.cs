using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #122: explicitly authenticated, internal review-only journal. The
// independent superadmin JWT gate is at the API endpoint. This service
// cannot move files, release a terminal F-061 lease, import or push.
public sealed record GitIncrementalLaterCheckpointMirrorRecoveryReviewOptions(
    bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorRecoveryReviewJournal(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorRecoveryReviewOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector inspector,
    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore leaseStore,
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight admission)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    public async Task<GitIncrementalLaterCheckpointMirrorRecoveryDecision>
        RecordAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, Guid authenticatedReviewerId,
            string decision, string expectedInspectionSha256,
            CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorRecoveryReviewDisabled");
        if (authenticatedReviewerId == Guid.Empty ||
            authenticatedReviewerId == originalBundleActorId ||
            !GitSourceBundleStorageCheck.ValidSha(expectedInspectionSha256) ||
            decision is not (
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.ApprovedForIsolationReview or
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Rejected))
            throw Refused("LaterCheckpointMirrorRecoveryReviewInvalid");

        // The candidate is NOT accepted from the request. Independently
        // rebuild current-epoch source and target proof; real trusted Git
        // ref/OID+fsck runs OUTSIDE the SQL transaction.
        var inspected = await inspector.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        if (inspected.EvidenceSha256 != expectedInspectionSha256 ||
            inspected.SourceSnapshotId == Guid.Empty ||
            inspected.DurableRecoveryApprovalPersisted ||
            inspected.CanReAdmit || inspected.CanMaterialize ||
            inspected.CanPush || inspected.CanRetry ||
            inspected.CanDispatch || inspected.MigrationComplete ||
            (decision ==
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.ApprovedForIsolationReview &&
             inspected.PhysicalState !=
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Intact))
            throw Refused("LaterCheckpointMirrorRecoveryReviewInspectionChanged");

        // A second independent admission verifies the latest F-091/
        // F-097b full-source + reviewed F-031 and physical F-035v2.
        var source = await admission.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        if (source.PhysicalMirrorId != inspected.PhysicalMirrorId ||
            source.StagingEvidenceSha256 != inspected.StagingEvidenceSha256 ||
            source.SourceSnapshotId != inspected.SourceSnapshotId ||
            source.BundleEvidenceSha256 != inspected.BundleEvidenceSha256 ||
            source.BundleSha256 != inspected.BundleSha256 ||
            source.SourceFullRefProofSha256 != inspected.FullRefProofSha256 ||
            source.SourceRefSetSha256 != inspected.SourceRefSetSha256)
            throw Refused("LaterCheckpointMirrorRecoveryReviewScopeChanged");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await leaseStore.LockAsync(source.SourceConnectionId, planId, ct);
        await leaseStore.RequireLiveLockedAsync(
            source, originalBundleActorId, ct);
        var now = await leaseStore.DatabaseNowAsync(ct);
        var current = await leaseStore.ReadLeaseAsync(
            inspected.PhysicalMirrorId, ct)
            ?? throw Refused("LaterCheckpointMirrorRecoveryReviewLeaseMissing");
        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore
            .RequireSameScope(current, source, originalBundleActorId);
        if (current.State != "RecoveryRequired" ||
            current.LeaseUntilUtc is not null ||
            current.FailureCode != inspected.FailureCode ||
            current.WorkerId != inspected.WorkerId ||
            current.LeaseToken != inspected.LeaseToken ||
            current.Generation != inspected.LeaseGeneration ||
            current.HasVerifiedLocalObjects ||
            current.CanPush || current.CanRetry ||
            current.CanDispatch || current.MigrationComplete)
            throw Refused("LaterCheckpointMirrorRecoveryReviewLeaseChanged");

        // Unlike the original worker, the reviewer must be the
        // independently approved F-031 human reviewer, AND the route
        // requires a superadmin JWT. Never accept an actor ID as
        // authentication.
        var human = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (human is null ||
            human.Decision !=
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            human.ReviewedBy != authenticatedReviewerId ||
            human.SourceSnapshotId != inspected.SourceSnapshotId ||
            authenticatedReviewerId == current.WorkerId ||
            authenticatedReviewerId == current.OriginalBundleActorId)
            throw Refused("LaterCheckpointMirrorRecoveryReviewIndependentReviewerRequired");

        var lastEvent = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_lease_events
            WHERE mirror_id = {inspected.PhysicalMirrorId}
                AND type = 'Quarantined'
                AND worker_id = {inspected.WorkerId}
                AND generation = {inspected.LeaseGeneration}
                AND staging_evidence_sha256 =
                    {inspected.StagingEvidenceSha256}
            """).SingleAsync(ct);
        if (lastEvent != 1 ||
            now < inspected.InspectedAtUtc ||
            now - inspected.InspectedAtUtc > TimeSpan.FromMinutes(2))
            throw Refused("LaterCheckpointMirrorRecoveryReviewInspectionExpired");

        var exists = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_recovery_decisions
            WHERE mirror_id = {inspected.PhysicalMirrorId}
                OR workpack_id = {inspected.WorkpackId}
            """).SingleAsync(ct);
        if (exists != 0)
            throw Refused("LaterCheckpointMirrorRecoveryReviewAlreadyRecorded");

        var receipt =
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Seal(
                inspected, originalBundleActorId,
                authenticatedReviewerId, decision, now);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_recovery_decisions (
                mirror_id, workpack_id, checkpoint_id, source_snapshot_id,
                evidence_version, original_worker_id,
                original_lease_token, original_lease_generation,
                original_bundle_actor_id, reviewer_id, decision,
                inspection_evidence_sha256, lease_event_history_sha256,
                staging_evidence_sha256, object_set_sha256,
                reviewed_at_utc, evidence_sha256,
                can_readmit, can_materialize, can_push, can_retry,
                can_dispatch, migration_complete)
            VALUES ({receipt.PhysicalMirrorId}, {receipt.WorkpackId},
                {receipt.CheckpointId}, {receipt.SourceSnapshotId},
                {receipt.EvidenceVersion}, {receipt.OriginalWorkerId},
                {receipt.OriginalLeaseToken},
                {receipt.OriginalLeaseGeneration},
                {receipt.OriginalBundleActorId}, {receipt.ReviewerId},
                {receipt.Decision}, {receipt.InspectionEvidenceSha256},
                {receipt.LeaseEventHistorySha256},
                {receipt.StagingEvidenceSha256},
                {receipt.ObjectSetSha256}, {receipt.ReviewedAtUtc},
                {receipt.EvidenceSha256}, {false}, {false}, {false},
                {false}, {false}, {false})
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO git_later_checkpoint_mirror_recovery_decision_events (
                mirror_id, reviewer_id, decision,
                evidence_sha256, occurred_at_utc)
            VALUES ({receipt.PhysicalMirrorId}, {receipt.ReviewerId},
                {receipt.Decision}, {receipt.EvidenceSha256},
                {receipt.ReviewedAtUtc})
            """, ct);
        await tx.CommitAsync(ct);
        return receipt;
    }
}
