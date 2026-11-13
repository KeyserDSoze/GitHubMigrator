using System.Data;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #109 phase 1: explicit, OFF-by-default, PostgreSQL-only, independently
// checked prerequisite witness. It NEVER reads connection credentials,
// calls a GitHub provider, persists F-031 or grants provider-read authority.
// Future actual GET transport requires a different gate and post-GET lock.
public sealed record GitCheckpointTargetReadPreflightOptions(bool Enabled);

public sealed class PostgresGitCheckpointTargetReadPreflight(
    AppDbContext db, TimeProvider clock,
    GitCheckpointTargetReadPreflightOptions options,
    IIncrementalOperationPlanRepository plans,
    PostgresGitCheckpointTargetHumanReviewJournal reviews)
{
    private static GitCheckpointTargetReadPreflightException Refused(
        string code) => new(code);
    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<GitCheckpointTargetReadPreflight> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, Guid authenticatedReviewerId,
        string expectedCandidateEvidenceSha256,
        string expectedHumanReviewEvidenceSha256,
        CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("CheckpointTargetReadPreflightDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || authenticatedReviewerId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !Sha(expectedCandidateEvidenceSha256) ||
            !Sha(expectedHumanReviewEvidenceSha256))
            throw Refused("CheckpointTargetReadPreflightInvalidRequest");

        // #106 verifies complete F-014/F-091/F-097b and the historical
        // F-029 mapping under a source lock. #108 verifies the complete
        // persisted immutable SHA-256 decision and its audit event.
        // Never use a caller-provided "approved" Boolean or reviewer UUID.
        GitCheckpointTargetReReviewCandidate candidate;
        GitCheckpointTargetHumanReviewDecision? human;
        try
        {
            candidate = await reviews.ReadAuthenticatedCandidateAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
            human = await reviews.InspectScopedAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
        }
        catch (GitCheckpointTargetHumanReviewException)
        { throw Refused("CheckpointTargetReadPreflightSourceUnverified"); }

        if (candidate.EvidenceSha256 != expectedCandidateEvidenceSha256)
            throw Refused("CheckpointTargetReadPreflightCandidateChanged");
        if (human is null ||
            human.Decision != GitCheckpointTargetHumanReviewStates.ApprovedForObservation)
            throw Refused("CheckpointTargetReadPreflightHumanApprovalRequired");
        if (human.EvidenceSha256 != expectedHumanReviewEvidenceSha256)
            throw Refused("CheckpointTargetReadPreflightReviewChanged");
        if (human.ReviewedBy != authenticatedReviewerId ||
            human.RequestedBy == authenticatedReviewerId)
            throw Refused("CheckpointTargetReadPreflightIndependentReviewerRequired");
        try { GitCheckpointTargetHumanReviewIntegrity.Verify(human, candidate); }
        catch (GitCheckpointTargetHumanReviewException)
        { throw Refused("CheckpointTargetReadPreflightReviewUnverified"); }

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Source-first lock order exactly matches staging, F-090 and #108.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({candidate.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);

        var plan = await plans.GetAsync(preparationId, planId, ct);
        var prep = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct);
        var approval = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        var approvalEvent = await db.IncrementalReviewEvents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var latestSource = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == candidate.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var newestCheckpoint = await (
            from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join sync in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals sync.SnapshotId
            where cp.PlanId == planId
            orderby sync.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        var proof = await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var receipt = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var reviewEvents = await db.GitCheckpointTargetHumanReviewEvents
            .AsNoTracking().Where(x => x.DecisionId == human.Id)
            .Take(2).ToArrayAsync(ct);
        var sourceConnection = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == candidate.SourceConnectionId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == candidate.DestinationConnectionId, ct);

        var now = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());
        if (plan is null || plan.IsCurrent || plan.ReadyForExecution ||
            plan.Id != candidate.PlanId ||
            plan.PreparationId != candidate.PreparationId ||
            plan.SourceConnectionId != candidate.SourceConnectionId ||
            plan.DestinationConnectionId != candidate.DestinationConnectionId ||
            plan.ManifestSha256 != candidate.OperationManifestSha256 ||
            plan.ReviewSha256 != candidate.OriginalReviewSha256 ||
            plan.SnapshotId != candidate.BaselineSnapshotId ||
            !string.Equals(plan.Organization, candidate.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == candidate.SourceRepositoryId &&
                x.TargetRepositoryName == candidate.TargetRepositoryName) ||
            prep is null || prep.State != IncrementalPreparationStates.Prepared ||
            prep.EnqueuedBy != human.RequestedBy ||
            prep.SnapshotId != candidate.BaselineSnapshotId ||
            approval is null ||
            approval.State != IncrementalReviewStates.ApprovedForPlanning ||
            approval.ReviewerId != candidate.OriginalReviewerId ||
            approval.DryRunSha256 != candidate.OriginalReviewSha256 ||
            approvalEvent is null ||
            approvalEvent.EventType != IncrementalReviewStates.ApprovedForPlanning ||
            approvalEvent.ActorId != candidate.OriginalReviewerId ||
            approvalEvent.DryRunSha256 != candidate.OriginalReviewSha256 ||
            latestSource != candidate.SourceSnapshotId ||
            newestCheckpoint is null ||
            newestCheckpoint.Id != candidate.CheckpointId ||
            newestCheckpoint.SnapshotId != candidate.SourceSnapshotId ||
            newestCheckpoint.RefChangeAttestationSha256 !=
                candidate.CheckpointAttestationSha256 ||
            newestCheckpoint.CanPush || newestCheckpoint.CanRetry ||
            newestCheckpoint.CanDispatch || newestCheckpoint.MigrationComplete ||
            proof is null ||
            proof.SourceConnectionId != candidate.SourceConnectionId ||
            proof.PlanId != planId ||
            proof.SnapshotId != candidate.SourceSnapshotId ||
            proof.EvidenceSha256 != candidate.SourceFullRefProofSha256 ||
            proof.SourceManifestRootSha256 != candidate.SourceManifestRootSha256 ||
            proof.CanPush || proof.CanRetry ||
            proof.CanDispatch || proof.MigrationComplete ||
            receipt is null || receipt.Id != human.Id ||
            receipt.PreparationId != preparationId ||
            receipt.PlanId != planId ||
            receipt.DestinationConnectionId != candidate.DestinationConnectionId ||
            receipt.SourceSnapshotId != candidate.SourceSnapshotId ||
            receipt.CandidateEvidenceSha256 != expectedCandidateEvidenceSha256 ||
            receipt.EvidenceSha256 != expectedHumanReviewEvidenceSha256 ||
            receipt.Decision != GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            receipt.ReviewedBy != authenticatedReviewerId ||
            receipt.RequestedBy != candidate.RequestedBy ||
            receipt.CanObserveTarget || receipt.CanPush || receipt.CanRetry ||
            receipt.CanDispatch || receipt.MigrationComplete ||
            reviewEvents.Length != 1 ||
            reviewEvents[0].Type != human.Decision ||
            reviewEvents[0].ReviewerId != human.ReviewedBy ||
            reviewEvents[0].EvidenceSha256 != human.EvidenceSha256 ||
            reviewEvents[0].OccurredAtUtc != human.ReviewedAtUtc ||
            sourceConnection is null ||
            sourceConnection.Direction != ConnectionDirection.Source ||
            sourceConnection.ValidationCode != "Verified" ||
            sourceConnection.ValidatedAtUtc != approval.SourceValidatedAtUtc ||
            destination is null ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            destination.ValidatedAtUtc != approval.DestinationValidatedAtUtc ||
            !string.Equals(destination.Organization, candidate.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            destination.ValidatedAtUtc is null ||
            now < destination.ValidatedAtUtc ||
            now - destination.ValidatedAtUtc >= TimeSpan.FromMinutes(5))
            throw Refused("CheckpointTargetReadPreflightContextChanged");

        var expires = destination.ValidatedAtUtc.Value.AddMinutes(5);
        GitCheckpointTargetReadPreflight witness;
        try
        {
            witness = GitCheckpointTargetReadPreflightIntegrity.Seal(
                candidate, human, now, expires);
        }
        catch (GitCheckpointTargetReadPreflightException)
        { throw Refused("CheckpointTargetReadPreflightContextChanged"); }

        await tx.CommitAsync(ct);
        return witness;
    }
}
