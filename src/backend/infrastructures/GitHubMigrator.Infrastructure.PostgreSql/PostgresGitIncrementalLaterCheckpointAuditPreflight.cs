using System.Data;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #104: separate, OFF-by-default READ-ONLY v2 join of a genuinely later
// F-091/F-097b checkpoint and the independently authorized #109 F-031.
// No F-032/F-038 writes or authority for GitHub provider calls.
public sealed record GitIncrementalLaterCheckpointAuditOptions(bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointAuditPreflight(
    AppDbContext db, TimeProvider clock,
    GitIncrementalLaterCheckpointAuditOptions options,
    IIncrementalOperationPlanRepository plans,
    IGitHubSourceSyncCheckpointJournal checkpoints,
    PostgresGitHubSourceCheckpointGitRefLedger sourceLedger,
    PostgresGitCheckpointTargetObservationJournal targetJournal,
    PostgresGitCheckpointTargetReadPreflight humanGate)
{
    private static GitIncrementalCheckpointPreflightException Refused(
        string code) => new(code);

    private static bool Sha(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<GitIncrementalCheckpointAuditInputs> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("GitLaterCheckpointPreflightDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !Sha(expectedTargetReceiptSha256))
            throw Refused("GitLaterCheckpointPreflightInvalidScope");

        // Independent persisted evidence readers verify the F-091/F-097b
        // chain and the NEW F-031 receipt + exactly one immutable audit.
        IncrementalOperationPlan? plan;
        GitHubSourceSyncCheckpointReceipt? checkpoint;
        GitHubSourceCheckpointGitRefProof? source;
        GitCheckpointTargetObservation? target;
        try
        {
            plan = await plans.GetAsync(preparationId, planId, ct);
            checkpoint = await checkpoints.GetVerifiedAsync(
                preparationId, planId, checkpointId, ct);
            source = await sourceLedger.GetVerifiedAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
            target = await targetJournal.ReadScopedAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
        }
        catch (Exception ex) when (ex is GitHubSourceSyncCheckpointException
            or GitHubSourceCheckpointGitRefLedgerException
            or GitCheckpointTargetObservationException)
        { throw Refused("GitLaterCheckpointPreflightEvidenceUnverified"); }
        if (plan is null || checkpoint is null || source is null ||
            target is null || target.Id != targetObservationId ||
            target.EvidenceSha256 != expectedTargetReceiptSha256)
            throw Refused("GitLaterCheckpointPreflightEvidenceChanged");

        // A saved F-031 is historical evidence, never indefinite authority.
        // Re-run #106/#108 under its own source-first PostgreSQL locks:
        // current F-091/F-097b, independent reviewer/approval and
        // destination validation (5 minute TTL), without any provider GET.
        GitCheckpointTargetReadPreflight gate;
        try
        {
            gate = await humanGate.ReadAsync(
                preparationId, planId, checkpointId,
                sourceRepositoryId, target.ReviewedBy,
                target.CandidateEvidenceSha256,
                target.HumanReviewEvidenceSha256, ct);
        }
        catch (GitCheckpointTargetReadPreflightException)
        { throw Refused("GitLaterCheckpointPreflightApprovalUnverified"); }

        var now = clock.GetUtcNow();
        if (gate.CheckpointId != target.CheckpointId ||
            gate.SourceSnapshotId != target.SourceSnapshotId ||
            gate.DestinationConnectionId != target.DestinationConnectionId ||
            gate.SourceRepositoryId != target.SourceRepositoryId ||
            gate.TargetRepositoryName != target.TargetRepositoryName ||
            gate.Organization != target.Organization ||
            gate.ObjectFormat != target.ObjectFormat ||
            gate.ReviewedBy != target.ReviewedBy ||
            gate.CandidateEvidenceSha256 != target.CandidateEvidenceSha256 ||
            gate.HumanReviewEvidenceSha256 != target.HumanReviewEvidenceSha256 ||
            target.ObservedAtUtc > now ||
            now - target.ObservedAtUtc >= TimeSpan.FromMinutes(5) ||
            now >= gate.ExpiresAtUtc)
            throw Refused("GitLaterCheckpointPreflightTargetStale");

        GitIncrementalCheckpointAuditInputs joined;
        try
        {
            joined = GitIncrementalCheckpointTargetJoin.ForReadOnlyComparison(
                plan, checkpoint, source, target);
        }
        catch (GitIncrementalCheckpointPreflightException)
        { throw Refused("GitLaterCheckpointPreflightEvidenceInvalid"); }

        // Detect a superseding snapshot/checkpoint, review revocation or
        // target change even when it happens between the verified reads.
        // This read-only method DOES NOT mint a workpack capability:
        // a future v2 writer must repeat ALL checks under its own lock.
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({plan.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);

        var reloadedPlan = await plans.GetAsync(preparationId, planId, ct);
        var latestSnapshot = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == plan.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var latestCheckpoint = await (
            from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join snapshot in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals snapshot.SnapshotId
            where cp.PlanId == planId
            orderby snapshot.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        var sourceRow = await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var targetRow = await db.GitCheckpointTargetObservations
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == targetObservationId && x.PlanId == planId &&
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var human = await db.GitCheckpointTargetHumanReviews
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == target.DestinationConnectionId, ct);
        var currentTime = clock.GetUtcNow();

        if (reloadedPlan is null || reloadedPlan.IsCurrent ||
            reloadedPlan.ReadyForExecution ||
            reloadedPlan.ManifestSha256 != plan.ManifestSha256 ||
            reloadedPlan.ReviewSha256 != plan.ReviewSha256 ||
            reloadedPlan.SnapshotId != plan.SnapshotId ||
            reloadedPlan.SourceConnectionId != plan.SourceConnectionId ||
            reloadedPlan.DestinationConnectionId != plan.DestinationConnectionId ||
            !string.Equals(reloadedPlan.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !reloadedPlan.Steps.SequenceEqual(plan.Steps) ||
            latestSnapshot != source.SnapshotId ||
            latestCheckpoint is null ||
            latestCheckpoint.Id != checkpointId ||
            latestCheckpoint.SnapshotId != source.SnapshotId ||
            latestCheckpoint.RefChangeAttestationSha256 !=
                checkpoint.RefChangeAttestationSha256 ||
            latestCheckpoint.CanPush || latestCheckpoint.CanRetry ||
            latestCheckpoint.CanDispatch || latestCheckpoint.MigrationComplete ||
            sourceRow is null ||
            sourceRow.EvidenceSha256 != source.EvidenceSha256 ||
            sourceRow.SnapshotId != source.SnapshotId ||
            sourceRow.SourceManifestRootSha256 != source.SourceManifestRootSha256 ||
            sourceRow.CanPush || sourceRow.CanRetry ||
            sourceRow.CanDispatch || sourceRow.MigrationComplete ||
            targetRow is null ||
            targetRow.EvidenceSha256 != target.EvidenceSha256 ||
            targetRow.TargetEvidenceSha256 != target.TargetEvidenceSha256 ||
            targetRow.HumanReviewEvidenceSha256 != target.HumanReviewEvidenceSha256 ||
            targetRow.CandidateEvidenceSha256 != target.CandidateEvidenceSha256 ||
            targetRow.CanObserveTarget || targetRow.CanPush ||
            targetRow.CanRetry || targetRow.CanDispatch ||
            targetRow.MigrationComplete ||
            human is null ||
            human.EvidenceSha256 != target.HumanReviewEvidenceSha256 ||
            human.CandidateEvidenceSha256 != target.CandidateEvidenceSha256 ||
            human.ReviewedBy != target.ReviewedBy ||
            human.Decision != GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            human.CanObserveTarget || human.CanPush ||
            human.CanRetry || human.CanDispatch || human.MigrationComplete ||
            review is null ||
            review.State != IncrementalReviewStates.ApprovedForPlanning ||
            review.DryRunSha256 != plan.ReviewSha256 ||
            destination is null ||
            destination.ValidationCode != "Verified" ||
            destination.ValidatedAtUtc != review.DestinationValidatedAtUtc ||
            !string.Equals(destination.Organization, target.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            destination.ValidatedAtUtc is null ||
            currentTime < destination.ValidatedAtUtc ||
            currentTime - destination.ValidatedAtUtc >= TimeSpan.FromMinutes(5) ||
            target.ObservedAtUtc > currentTime ||
            currentTime - target.ObservedAtUtc >= TimeSpan.FromMinutes(5))
            throw Refused("GitLaterCheckpointPreflightContextChanged");

        await tx.CommitAsync(ct);
        return joined;
    }
}
