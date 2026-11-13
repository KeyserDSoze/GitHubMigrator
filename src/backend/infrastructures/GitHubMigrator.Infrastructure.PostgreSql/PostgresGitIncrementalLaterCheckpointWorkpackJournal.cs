using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #104: separately gated source-locked F-032v2/F-038v2 capture from
// genuine later F-091/F-097b + #109 checkpoint F-031. Never writes the
// historical F-032/F-038 tables used by dispatch or external GitHub.
public sealed record GitIncrementalLaterCheckpointWorkpackOptions(bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointWorkpackJournal(
    AppDbContext db, TimeProvider clock,
    GitIncrementalLaterCheckpointWorkpackOptions options,
    PostgresGitIncrementalLaterCheckpointAuditPreflight preflight,
    PostgresGitHubSourceCheckpointGitRefLedger ledger,
    IIncrementalOperationPlanRepository plans)
{
    private static GitIncrementalCheckpointWorkpackException Refused(string code) =>
        new(code);

    public async Task<GitIncrementalLaterCheckpointWorkpackProof> CaptureAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, Guid actorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointWorkpackDisabled");
        if (actorId == Guid.Empty)
            throw Refused("LaterCheckpointWorkpackActorInvalid");

        GitIncrementalCheckpointAuditInputs input;
        GitHubSourceCheckpointGitRefProof? source;
        try
        {
            input = await preflight.ReadAsync(preparationId, planId, checkpointId,
                targetObservationId, sourceRepositoryId,
                expectedTargetReceiptSha256, ct);
            source = await ledger.GetVerifiedAsync(preparationId, planId,
                checkpointId, sourceRepositoryId, ct);
        }
        catch (GitIncrementalCheckpointPreflightException)
        { throw Refused("LaterCheckpointWorkpackPreflightUnverified"); }
        catch (GitHubSourceCheckpointGitRefLedgerException)
        { throw Refused("LaterCheckpointWorkpackSourceUnverified"); }
        if (source is null || input.EvidenceVersion != 2 ||
            input.SourceFullRefProofSha256 != source.EvidenceSha256)
            throw Refused("LaterCheckpointWorkpackSourceUnverified");

        var originalPlan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("LaterCheckpointWorkpackPlanMissing");
        if (originalPlan.IsCurrent || originalPlan.ReadyForExecution ||
            originalPlan.SnapshotId == input.SourceSnapshotId)
            throw Refused("LaterCheckpointWorkpackNotLaterEpoch");

        GitMirrorRefReconciliation compared;
        try
        {
            compared = GitMirrorRefReconciler.Compare(new(
                planId, originalPlan.ManifestSha256, source.SourceEvidenceSha256,
                input.TargetEvidenceSha256, input.ExpectedSourceRefs,
                input.ExpectedTargetRefs));
        }
        catch (GitMirrorRefReconciliationException)
        { throw Refused("LaterCheckpointWorkpackReconciliationInvalid"); }

        // Source-first, plan-second locks prevent a newer staging snapshot,
        // reviewer revocation or another capture from racing the INSERT.
        // No external I/O or independently locking preflight inside tx.
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({originalPlan.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);

        var plan = await plans.GetAsync(preparationId, planId, ct);
        var lastSnapshot = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == originalPlan.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var newest = await (
            from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join sync in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals sync.SnapshotId
            where cp.PlanId == planId
            orderby sync.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        var full = await db.GitHubSourceCheckpointGitRefLedgers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var target = await db.GitCheckpointTargetObservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == targetObservationId &&
                x.PlanId == planId && x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var targetEvents = await db.GitCheckpointTargetObservationEvents
            .AsNoTracking().Where(x => x.ObservationId == targetObservationId)
            .Take(2).ToArrayAsync(ct);
        var human = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var humanEvents = human is null ? [] : await db
            .GitCheckpointTargetHumanReviewEvents.AsNoTracking()
            .Where(x => x.DecisionId == human.Id).Take(2).ToArrayAsync(ct);
        var review = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        var reviewEvent = await db.IncrementalReviewEvents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var sourceConnection = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == originalPlan.SourceConnectionId, ct);
        var destination = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == originalPlan.DestinationConnectionId, ct);
        var prep = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct);
        var now = clock.GetUtcNow();
        if (plan is null || plan.IsCurrent || plan.ReadyForExecution ||
            plan.SourceConnectionId != originalPlan.SourceConnectionId ||
            plan.DestinationConnectionId != originalPlan.DestinationConnectionId ||
            plan.SnapshotId != originalPlan.SnapshotId ||
            plan.ManifestSha256 != originalPlan.ManifestSha256 ||
            plan.ReviewSha256 != originalPlan.ReviewSha256 ||
            !plan.Steps.SequenceEqual(originalPlan.Steps) ||
            lastSnapshot != input.SourceSnapshotId ||
            newest is null || newest.Id != checkpointId ||
            newest.SnapshotId != input.SourceSnapshotId ||
            newest.RefChangeAttestationSha256 !=
                input.SourceCheckpointAttestationSha256 ||
            newest.PreparationId != preparationId ||
            newest.SourceConnectionId != plan.SourceConnectionId ||
            newest.CanPush || newest.CanRetry ||
            newest.CanDispatch || newest.MigrationComplete ||
            full is null || full.PreparationId != preparationId ||
            full.PlanId != planId ||
            full.SourceConnectionId != plan.SourceConnectionId ||
            full.SnapshotId != input.SourceSnapshotId ||
            full.EvidenceSha256 != source.EvidenceSha256 ||
            full.SourceManifestRootSha256 != source.SourceManifestRootSha256 ||
            full.RecordedBy != actorId ||
            full.CanPush || full.CanRetry ||
            full.CanDispatch || full.MigrationComplete ||
            target is null || target.PreparationId != preparationId ||
            target.SourceSnapshotId != input.SourceSnapshotId ||
            target.DestinationConnectionId != plan.DestinationConnectionId ||
            target.TargetRepositoryName != input.TargetRepositoryName ||
            !string.Equals(target.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            target.EvidenceSha256 != input.TargetEvidenceSha256 ||
            target.CanObserveTarget || target.CanPush || target.CanRetry ||
            target.CanDispatch || target.MigrationComplete ||
            targetEvents.Length != 1 ||
            targetEvents[0].Type != "ObservedReadOnly" ||
            targetEvents[0].EvidenceSha256 != target.EvidenceSha256 ||
            targetEvents[0].ReviewedBy != target.ReviewedBy ||
            targetEvents[0].OccurredAtUtc != target.ObservedAtUtc ||
            human is null ||
            human.EvidenceSha256 != target.HumanReviewEvidenceSha256 ||
            human.CandidateEvidenceSha256 != target.CandidateEvidenceSha256 ||
            human.ReviewedBy != target.ReviewedBy ||
            human.Decision != GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            human.CanObserveTarget || human.CanPush || human.CanRetry ||
            human.CanDispatch || human.MigrationComplete ||
            humanEvents.Length != 1 ||
            humanEvents[0].Type != human.Decision ||
            humanEvents[0].ReviewerId != target.ReviewedBy ||
            humanEvents[0].EvidenceSha256 != human.EvidenceSha256 ||
            humanEvents[0].OccurredAtUtc != human.ReviewedAtUtc ||
            prep is null || prep.State != IncrementalPreparationStates.Prepared ||
            prep.EnqueuedBy != human.RequestedBy ||
            review is null ||
            review.State != IncrementalReviewStates.ApprovedForPlanning ||
            review.DryRunSha256 != plan.ReviewSha256 ||
            reviewEvent is null ||
            reviewEvent.EventType != IncrementalReviewStates.ApprovedForPlanning ||
            reviewEvent.ActorId != review.ReviewerId ||
            reviewEvent.DryRunSha256 != review.DryRunSha256 ||
            sourceConnection is null ||
            sourceConnection.ValidationCode != "Verified" ||
            sourceConnection.Direction != ConnectionDirection.Source ||
            sourceConnection.ValidatedAtUtc != review.SourceValidatedAtUtc ||
            destination is null ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            destination.ValidatedAtUtc != review.DestinationValidatedAtUtc ||
            !string.Equals(destination.Organization, target.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            destination.ValidatedAtUtc is null ||
            now < destination.ValidatedAtUtc ||
            now - destination.ValidatedAtUtc >= TimeSpan.FromMinutes(5) ||
            target.ObservedAtUtc > now ||
            now - target.ObservedAtUtc >= TimeSpan.FromMinutes(5))
            throw Refused("LaterCheckpointWorkpackContextChanged");

        var saved = await db.GitLaterCheckpointWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.TargetObservationId == targetObservationId, ct);
        if (saved is not null)
        {
            GitIncrementalLaterCheckpointWorkpackProof proof;
            try
            {
                proof = JsonSerializer.Deserialize<
                    GitIncrementalLaterCheckpointWorkpackProof>(saved.ReceiptJson)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            { throw Refused("LaterCheckpointWorkpackHistoryCorrupt"); }
            try
            {
                GitIncrementalLaterCheckpointWorkpackIntegrity.Verify(
                    proof, plan, input, source, compared);
            }
            catch (GitIncrementalCheckpointWorkpackException)
            { throw Refused("LaterCheckpointWorkpackHistoryCorrupt"); }

            if (proof.RecordedBy != actorId ||
                saved.Id != proof.WorkpackId ||
                saved.ReconciliationId != proof.ReconciliationId ||
                saved.PreparationId != preparationId ||
                saved.PlanId != planId ||
                saved.SourceSnapshotId != source.SnapshotId ||
                saved.TargetReceiptSha256 != input.TargetEvidenceSha256 ||
                saved.FullRefProofSha256 != source.EvidenceSha256 ||
                saved.EvidenceSha256 != proof.EvidenceSha256 ||
                saved.RecordedBy != actorId ||
                saved.ReadyForExecution || saved.CanPush ||
                saved.CanRetry || saved.CanDispatch || saved.MigrationComplete ||
                saved.State != "Blocked" || saved.Phase != "WriteBlocked" ||
                saved.Attempts != 0)
                throw Refused("LaterCheckpointWorkpackReplayConflict");
            await VerifyChildrenAsync(proof, compared, ct);
            await tx.CommitAsync(ct);
            return proof;
        }

        var recId = Guid.NewGuid();
        var workpackId = Guid.NewGuid();
        GitIncrementalLaterCheckpointWorkpackProof sealedProof;
        try
        {
            sealedProof = GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
                plan, input, source, compared, recId, workpackId, actorId);
        }
        catch (GitIncrementalCheckpointWorkpackException)
        { throw Refused("LaterCheckpointWorkpackEvidenceInvalid"); }

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
            now.ToUnixTimeMilliseconds());
        db.GitLaterCheckpointReconciliations.Add(new() {
            Id = recId, PreparationId = preparationId, PlanId = planId,
            CheckpointId = checkpointId, SourceSnapshotId = source.SnapshotId,
            SourceRepositoryId = sourceRepositoryId,
            TargetObservationId = targetObservationId,
            ManifestSha256 = plan.ManifestSha256,
            SourceEvidenceSha256 = source.SourceEvidenceSha256,
            FullRefProofSha256 = source.EvidenceSha256,
            TargetReceiptSha256 = input.TargetEvidenceSha256,
            ReconciliationSha256 = compared.ReconciliationSha256,
            ItemsJson = JsonSerializer.Serialize(compared.Items),
            SourceRefCount = compared.SourceRefCount,
            TargetRefCount = compared.TargetRefCount,
            MatchingCount = compared.MatchingCount,
            CreateCandidateCount = compared.CreateCandidateCount,
            DivergedCount = compared.DivergedCount,
            TargetOnlyCount = compared.TargetOnlyCount,
            RecordedBy = actorId, RecordedAtUtc = timestamp,
            ReadyForExecution = false
        });
        db.GitLaterCheckpointWorkpacks.Add(new() {
            Id = workpackId, ReconciliationId = recId,
            PreparationId = preparationId, PlanId = planId,
            CheckpointId = checkpointId, SourceSnapshotId = source.SnapshotId,
            SourceRepositoryId = sourceRepositoryId,
            TargetObservationId = targetObservationId,
            TargetReceiptSha256 = input.TargetEvidenceSha256,
            FullRefProofSha256 = source.EvidenceSha256,
            ReceiptJson = JsonSerializer.Serialize(sealedProof),
            EvidenceSha256 = sealedProof.EvidenceSha256,
            State = "Blocked", Phase = "WriteBlocked",
            Attempts = 0, RecordedBy = actorId,
            RecordedAtUtc = timestamp, ReadyForExecution = false,
            CanPush = false, CanRetry = false, CanDispatch = false,
            MigrationComplete = false
        });
        db.GitLaterCheckpointWorkpackEvents.Add(new() {
            WorkpackId = workpackId, ActorId = actorId, Type = "Recorded",
            EvidenceSha256 = sealedProof.EvidenceSha256,
            OccurredAtUtc = timestamp
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sealedProof;
    }

    private async Task VerifyChildrenAsync(
        GitIncrementalLaterCheckpointWorkpackProof proof,
        GitMirrorRefReconciliation compared, CancellationToken ct)
    {
        var rec = await db.GitLaterCheckpointReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == proof.ReconciliationId, ct);
        var evs = await db.GitLaterCheckpointWorkpackEvents.AsNoTracking()
            .Where(x => x.WorkpackId == proof.WorkpackId).Take(2).ToArrayAsync(ct);
        if (rec is null || evs.Length != 1 ||
            rec.PreparationId != proof.PreparationId ||
            rec.PlanId != proof.PlanId ||
            rec.CheckpointId != proof.CheckpointId ||
            rec.SourceSnapshotId != proof.SourceSnapshotId ||
            rec.SourceRepositoryId != proof.SourceRepositoryId ||
            rec.TargetObservationId != proof.CheckpointTargetObservationId ||
            rec.ManifestSha256 != proof.OperationManifestSha256 ||
            rec.SourceEvidenceSha256 != proof.SourceEvidenceSha256 ||
            rec.FullRefProofSha256 != proof.SourceFullRefProofSha256 ||
            rec.TargetReceiptSha256 != proof.TargetReceiptSha256 ||
            rec.ReconciliationSha256 != proof.ReconciliationSha256 ||
            rec.RecordedBy != proof.RecordedBy ||
            rec.ReadyForExecution ||
            rec.SourceRefCount != compared.SourceRefCount ||
            rec.TargetRefCount != compared.TargetRefCount ||
            rec.MatchingCount != compared.MatchingCount ||
            rec.CreateCandidateCount != compared.CreateCandidateCount ||
            rec.DivergedCount != compared.DivergedCount ||
            rec.TargetOnlyCount != compared.TargetOnlyCount ||
            evs[0].Type != "Recorded" ||
            evs[0].ActorId != proof.RecordedBy ||
            evs[0].EvidenceSha256 != proof.EvidenceSha256 ||
            evs[0].OccurredAtUtc != rec.RecordedAtUtc)
            throw Refused("LaterCheckpointWorkpackHistoryCorrupt");
        try
        {
            var items = JsonSerializer.Deserialize<GitMirrorRefComparison[]>(
                rec.ItemsJson);
            if (items is null || !items.SequenceEqual(compared.Items))
                throw Refused("LaterCheckpointWorkpackHistoryCorrupt");
        }
        catch (JsonException)
        { throw Refused("LaterCheckpointWorkpackHistoryCorrupt"); }
    }
}
