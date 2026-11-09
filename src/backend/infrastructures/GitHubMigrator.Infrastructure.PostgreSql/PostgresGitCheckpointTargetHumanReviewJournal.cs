using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097c.4c.0b: off-by-default, explicit independent human review.
// No registration as an API route or worker. No network, credentials,
// F-031/F-032/F-038, F-095 or GitHub remote writes. A decision alone
// is NOT permission to make a destination GET: parent #105 must
// implement an authenticated and epoch-specific provider-read gate.
public sealed record GitCheckpointTargetHumanReviewOptions(bool Enabled);

public sealed class PostgresGitCheckpointTargetHumanReviewJournal(
    AppDbContext db, TimeProvider clock,
    GitCheckpointTargetHumanReviewOptions options,
    PostgresGitCheckpointTargetReReviewPrerequisites prerequisites)
{
    private static GitCheckpointTargetHumanReviewException Refused(
        string code) => new(code);
    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<GitCheckpointTargetHumanReviewDecision> RecordAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, Guid requestedBy, Guid reviewedBy,
        string decision, string expectedCandidateEvidenceSha256,
        CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("CheckpointHumanReviewDisabled");
        if (reviewedBy == Guid.Empty || requestedBy == Guid.Empty ||
            reviewedBy == requestedBy ||
            decision is not (
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation or
                GitCheckpointTargetHumanReviewStates.Rejected) ||
            !Sha(expectedCandidateEvidenceSha256))
            throw Refused("CheckpointHumanReviewInvalidRequest");

        // Verified F-014/F-091/F-097b, original F-027 reviewer,
        // plan mapping and destination validation originate ONLY in
        // the source-locked #106 reader, not a caller-supplied packet.
        GitCheckpointTargetReReviewCandidate candidate;
        try
        {
            candidate = await prerequisites.ReadAsync(
                preparationId, planId, checkpointId,
                sourceRepositoryId, requestedBy, ct);
        }
        catch (GitCheckpointTargetReReviewException)
        { throw Refused("CheckpointHumanReviewCandidateUnverified"); }
        if (candidate.EvidenceSha256 != expectedCandidateEvidenceSha256 ||
            candidate.CanObserveTarget || candidate.CanPush ||
            candidate.CanRetry || candidate.CanDispatch ||
            candidate.MigrationComplete)
            throw Refused("CheckpointHumanReviewCandidateChanged");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({candidate.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);

        // Source lock prevents another staging application/epoch
        // transition while the admission is being made. Reread the
        // *mutable* gates after the lock; never resurrect F-029.
        var plan = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == planId &&
                x.PreparationId == preparationId, ct);
        var preparation = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId, ct);
        var historical = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        var approval = await db.IncrementalReviewEvents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var latestSync = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == candidate.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var newest = await (from cp in
                db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join sync in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals sync.SnapshotId
            where cp.PlanId == planId
            orderby sync.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        var sourceProof = await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var sourceConn = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == candidate.SourceConnectionId, ct);
        var targetConn = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == candidate.DestinationConnectionId, ct);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());

        if (plan is null || preparation is null ||
            historical is null || approval is null ||
            newest is null || sourceProof is null ||
            sourceConn is null || targetConn is null ||
            plan.SnapshotId != candidate.BaselineSnapshotId ||
            plan.SourceConnectionId != candidate.SourceConnectionId ||
            plan.DestinationConnectionId != candidate.DestinationConnectionId ||
            plan.ManifestSha256 != candidate.OperationManifestSha256 ||
            plan.ReviewSha256 != candidate.OriginalReviewSha256 ||
            preparation.State != IncrementalPreparationStates.Prepared ||
            preparation.SnapshotId != candidate.BaselineSnapshotId ||
            preparation.EnqueuedBy != requestedBy ||
            historical.State != IncrementalReviewStates.ApprovedForPlanning ||
            historical.ReviewerId != candidate.OriginalReviewerId ||
            historical.DryRunSha256 != candidate.OriginalReviewSha256 ||
            approval.EventType != IncrementalReviewStates.ApprovedForPlanning ||
            approval.ActorId != candidate.OriginalReviewerId ||
            approval.DryRunSha256 != historical.DryRunSha256 ||
            latestSync != candidate.SourceSnapshotId ||
            newest.Id != checkpointId ||
            newest.SnapshotId != candidate.SourceSnapshotId ||
            newest.RefChangeAttestationSha256 !=
                candidate.CheckpointAttestationSha256 ||
            newest.CanPush || newest.CanRetry ||
            newest.CanDispatch || newest.MigrationComplete ||
            sourceProof.SourceConnectionId != candidate.SourceConnectionId ||
            sourceProof.PlanId != planId ||
            sourceProof.SnapshotId != candidate.SourceSnapshotId ||
            sourceProof.EvidenceSha256 != candidate.SourceFullRefProofSha256 ||
            sourceProof.SourceManifestRootSha256 !=
                candidate.SourceManifestRootSha256 ||
            sourceProof.CanPush || sourceProof.CanRetry ||
            sourceProof.CanDispatch || sourceProof.MigrationComplete ||
            sourceConn.ValidationCode != "Verified" ||
            targetConn.ValidationCode != "Verified" ||
            sourceConn.ValidatedAtUtc != historical.SourceValidatedAtUtc ||
            targetConn.ValidatedAtUtc != historical.DestinationValidatedAtUtc ||
            targetConn.Direction != ConnectionDirection.Destination ||
            targetConn.ProviderKey != "github-enterprise-cloud" ||
            !string.Equals(targetConn.Organization, candidate.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            targetConn.ValidatedAtUtc is null ||
            now < targetConn.ValidatedAtUtc ||
            now - targetConn.ValidatedAtUtc > TimeSpan.FromMinutes(5))
            throw Refused("CheckpointHumanReviewContextChanged");

        var previous = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (previous is not null)
        {
            if (previous.CandidateEvidenceSha256 != candidate.EvidenceSha256 ||
                previous.RequestedBy != requestedBy ||
                previous.ReviewedBy != reviewedBy ||
                previous.Decision != decision ||
                previous.PlanId != planId ||
                previous.PreparationId != preparationId ||
                previous.SourceSnapshotId != candidate.SourceSnapshotId ||
                previous.CanObserveTarget || previous.CanPush ||
                previous.CanRetry || previous.CanDispatch ||
                previous.MigrationComplete)
                throw Refused("CheckpointHumanReviewConflict");

            GitCheckpointTargetHumanReviewDecision stored;
            try
            {
                stored = JsonSerializer.Deserialize<
                    GitCheckpointTargetHumanReviewDecision>(
                    previous.ReceiptJson)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            { throw Refused("CheckpointHumanReviewHistoryCorrupt"); }
            try
            {
                GitCheckpointTargetHumanReviewIntegrity.Verify(
                    stored, candidate);
            }
            catch (GitCheckpointTargetHumanReviewException)
            { throw Refused("CheckpointHumanReviewHistoryCorrupt"); }
            if (stored.Id != previous.Id ||
                stored.EvidenceSha256 != previous.EvidenceSha256 ||
                stored.ReviewedAtUtc != previous.ReviewedAtUtc ||
                stored.CandidateEvidenceSha256 !=
                    previous.CandidateEvidenceSha256)
                throw Refused("CheckpointHumanReviewHistoryCorrupt");
            await VerifyEventAsync(stored, ct);
            await tx.CommitAsync(ct);
            return stored;
        }

        var sealedDecision = GitCheckpointTargetHumanReviewIntegrity.Seal(
            candidate, Guid.NewGuid(), reviewedBy, decision, now);
        db.GitCheckpointTargetHumanReviews.Add(new()
        {
            Id = sealedDecision.Id,
            PreparationId = preparationId, PlanId = planId,
            CheckpointId = checkpointId,
            SourceSnapshotId = candidate.SourceSnapshotId,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryName = candidate.TargetRepositoryName,
            DestinationConnectionId = candidate.DestinationConnectionId,
            CandidateEvidenceSha256 = candidate.EvidenceSha256,
            RequestedBy = requestedBy, ReviewedBy = reviewedBy,
            Decision = decision, ReviewedAtUtc = now,
            ReceiptJson = JsonSerializer.Serialize(sealedDecision),
            EvidenceSha256 = sealedDecision.EvidenceSha256,
            CanObserveTarget = false, CanPush = false,
            CanRetry = false, CanDispatch = false,
            MigrationComplete = false
        });
        db.GitCheckpointTargetHumanReviewEvents.Add(new()
        {
            DecisionId = sealedDecision.Id,
            ReviewerId = reviewedBy, Type = decision,
            EvidenceSha256 = sealedDecision.EvidenceSha256,
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sealedDecision;
    }

    private async Task VerifyEventAsync(
        GitCheckpointTargetHumanReviewDecision saved, CancellationToken ct)
    {
        var events = await db.GitCheckpointTargetHumanReviewEvents
            .AsNoTracking().Where(x => x.DecisionId == saved.Id)
            .Take(2).ToArrayAsync(ct);
        if (events.Length != 1 ||
            events[0].Type != saved.Decision ||
            events[0].ReviewerId != saved.ReviewedBy ||
            events[0].EvidenceSha256 != saved.EvidenceSha256 ||
            events[0].OccurredAtUtc != saved.ReviewedAtUtc)
            throw Refused("CheckpointHumanReviewHistoryCorrupt");
    }
}
