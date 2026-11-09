using System.Data;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097c.4c.0a: OFF by default. Unlike old F-031 it can examine the
// reviewed *historical* F-029 mapping after a genuine later F-091 epoch.
// It never grants F-031 target collection: a NEW independent checkpoint
// review and GitHub GET must still be implemented (parent #105).
public sealed record GitCheckpointTargetReReviewOptions(bool Enabled);

public sealed class PostgresGitCheckpointTargetReReviewPrerequisites(
    AppDbContext db, TimeProvider clock,
    GitCheckpointTargetReReviewOptions options,
    IIncrementalOperationPlanRepository plans,
    IGitHubSourceSyncCheckpointJournal checkpoints,
    PostgresGitHubSourceCheckpointGitRefLedger sourceLedger)
{
    private static GitCheckpointTargetReReviewException Refused(
        string code) => new(code);

    public async Task<GitCheckpointTargetReReviewCandidate> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, Guid requestedBy, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("CheckpointTargetReReviewDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || requestedBy == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId))
            throw Refused("CheckpointTargetReReviewInvalidScope");

        // The independently verified F-091 checkpoint and F-097b full
        // F-014 Git ref/OID chain are computed outside the write lock:
        // their readers manage their own repeatable-read transactions.
        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("CheckpointTargetReReviewPlanNotFound");
        if (plan.IsCurrent || plan.ReadyForExecution)
            throw Refused("CheckpointTargetReReviewLaterEpochRequired");

        GitHubSourceSyncCheckpointReceipt? cp;
        GitHubSourceCheckpointGitRefProof? proof;
        try
        {
            cp = await checkpoints.GetVerifiedAsync(
                preparationId, planId, checkpointId, ct);
            proof = await sourceLedger.GetVerifiedAsync(
                preparationId, planId, checkpointId,
                sourceRepositoryId, ct);
        }
        catch (Exception ex) when (ex is
            GitHubSourceSyncCheckpointException or
            GitHubSourceCheckpointGitRefLedgerException or
            IncrementalOperationPlanException)
        {
            throw Refused("CheckpointTargetReReviewSourceUnverified");
        }
        if (cp is null || proof is null)
            throw Refused("CheckpointTargetReReviewSourceUnverified");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Same source-first lock order as staging, F-090 and F-097.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({plan.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);

        var after = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("CheckpointTargetReReviewPlanChanged");
        if (after.IsCurrent || after.ReadyForExecution ||
            after.ManifestSha256 != plan.ManifestSha256 ||
            after.ReviewSha256 != plan.ReviewSha256 ||
            after.SnapshotId != plan.SnapshotId ||
            after.SourceConnectionId != plan.SourceConnectionId ||
            after.DestinationConnectionId != plan.DestinationConnectionId)
            throw Refused("CheckpointTargetReReviewPlanChanged");

        var latestSync = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == plan.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var newestCheckpoint = await (from ch in
                db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join sync in db.StagingSnapshotSyncs.AsNoTracking()
                on ch.SnapshotId equals sync.SnapshotId
            where ch.PlanId == planId
            orderby sync.SourceCollectedAtUtc descending, ch.Id descending
            select ch).FirstOrDefaultAsync(ct);
        if (newestCheckpoint is null ||
            latestSync != cp.Proof.SnapshotId ||
            newestCheckpoint.Id != checkpointId ||
            newestCheckpoint.SnapshotId != cp.Proof.SnapshotId ||
            newestCheckpoint.EvidenceVersion != 2 ||
            newestCheckpoint.EvidenceSha256 != cp.EvidenceSha256 ||
            newestCheckpoint.RefChangeAttestationSha256 !=
                cp.RefChangeAttestationSha256 ||
            newestCheckpoint.CanPush || newestCheckpoint.CanRetry ||
            newestCheckpoint.CanDispatch || newestCheckpoint.MigrationComplete)
            throw Refused("CheckpointTargetReReviewSourceSuperseded");

        var sourceRow = await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (sourceRow is null ||
            sourceRow.PreparationId != preparationId ||
            sourceRow.PlanId != planId ||
            sourceRow.SourceConnectionId != plan.SourceConnectionId ||
            sourceRow.SnapshotId != cp.Proof.SnapshotId ||
            sourceRow.EvidenceSha256 != proof.EvidenceSha256 ||
            sourceRow.SourceManifestRootSha256 !=
                proof.SourceManifestRootSha256 ||
            sourceRow.CanPush || sourceRow.CanRetry ||
            sourceRow.CanDispatch || sourceRow.MigrationComplete)
            throw Refused("CheckpointTargetReReviewSourceChanged");

        // Historical F-029 *reviewed mapping* is still cryptographically
        // stable even when its old current flag has expired. Never
        // resurrect that flag or reuse F-031 observations from the past.
        var prepared = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct);
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        var priorTarget = await db.IncrementalTargetPreflights.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        if (prepared is null || review is null || priorTarget is null ||
            prepared.State != IncrementalPreparationStates.Prepared ||
            prepared.SnapshotId != plan.SnapshotId ||
            prepared.SourceConnectionId != plan.SourceConnectionId ||
            prepared.DestinationConnectionId != plan.DestinationConnectionId ||
            prepared.EvidenceSha256 != plan.PreparationSha256 ||
            prepared.EnqueuedBy != requestedBy ||
            review.State != IncrementalReviewStates.ApprovedForPlanning ||
            review.ReviewerId is not Guid reviewerId ||
            reviewerId == Guid.Empty || reviewerId == requestedBy ||
            review.ReviewedAtUtc is null ||
            review.EvidenceSha256 != prepared.EvidenceSha256 ||
            review.DryRunSha256 != plan.ReviewSha256 ||
            review.SensitiveItemCount > 0 && !review.SensitiveAcknowledged ||
            priorTarget.EvidenceSha256 != plan.PreflightSha256 ||
            priorTarget.ReviewSha256 != plan.ReviewSha256 ||
            priorTarget.Organization != plan.Organization)
            throw Refused("CheckpointTargetReReviewHistoricalReviewInvalid");

        var approval = await db.IncrementalReviewEvents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (approval is null ||
            approval.EventType != IncrementalReviewStates.ApprovedForPlanning ||
            approval.ActorId != review.ReviewerId ||
            approval.DryRunSha256 != review.DryRunSha256)
            throw Refused("CheckpointTargetReReviewHistoricalApprovalInvalid");

        var sourceConnection = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == plan.SourceConnectionId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == plan.DestinationConnectionId, ct);
        if (sourceConnection is null || destination is null ||
            sourceConnection.Direction != ConnectionDirection.Source ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            sourceConnection.ValidationCode != "Verified" ||
            destination.ValidationCode != "Verified" ||
            sourceConnection.ValidatedAtUtc != review.SourceValidatedAtUtc ||
            destination.ValidatedAtUtc != review.DestinationValidatedAtUtc ||
            destination.ValidatedAtUtc != priorTarget.DestinationValidatedAtUtc ||
            sourceConnection.ValidatedAtUtc != priorTarget.SourceValidatedAtUtc ||
            !string.Equals(destination.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw Refused("CheckpointTargetReReviewConnectionChanged");

        // The old review is evidence of MAPPING, not permission to
        // observe/push. Its connection must still be validated recently
        // or a new explicit validation is needed (the next #105 slice).
        var now = clock.GetUtcNow();
        var age = now - destination.ValidatedAtUtc;
        if (destination.ValidatedAtUtc is null ||
            age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5))
            throw Refused("CheckpointTargetReReviewDestinationValidationExpired");

        GitCheckpointTargetReReviewCandidate candidate;
        try
        {
            candidate = GitCheckpointTargetReReviewIntegrity.Seal(
                after, cp, proof, reviewerId, requestedBy);
        }
        catch (GitCheckpointTargetReReviewException)
        { throw Refused("CheckpointTargetReReviewEvidenceInvalid"); }
        await tx.CommitAsync(ct);
        return candidate;
    }
}
