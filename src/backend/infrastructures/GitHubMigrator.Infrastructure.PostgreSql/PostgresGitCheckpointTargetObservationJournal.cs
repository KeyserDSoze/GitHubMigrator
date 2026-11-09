using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #109: explicit, separate OFF-by-default fixed-host GitHub GET transport.
// There is NO destination write adapter, F-032, F-038 or migration worker.
// Preflight is NOT provider authorization: this service independently
// authorizes one bounded, reviewed GET and rechecks under PostgreSQL lock.
public sealed record GitCheckpointTargetObservationOptions(bool Enabled);

public sealed class PostgresGitCheckpointTargetObservationJournal(
    AppDbContext db, TimeProvider clock,
    GitCheckpointTargetObservationOptions options,
    PostgresGitCheckpointTargetReadPreflight prereq,
    IConnectionRepository connections, IConnectionSecretStore secrets,
    IGitHubRefObservationReader reader)
{
    private static GitCheckpointTargetObservationException Refused(string code) =>
        new(code);
    private static bool Sha(string? sha) =>
        sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<GitCheckpointTargetObservation> CollectAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, Guid authenticatedReviewerId,
        string expectedCandidateEvidenceSha256,
        string expectedHumanReviewEvidenceSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("CheckpointTargetObservationDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || authenticatedReviewerId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !Sha(expectedCandidateEvidenceSha256) ||
            !Sha(expectedHumanReviewEvidenceSha256))
            throw Refused("CheckpointTargetObservationInvalidRequest");

        // Independent #109 preflight re-verifies current F-014/F-091/F-097b,
        // #108 exact reviewer/approval/audit and F-029 reviewed mapping.
        var before = await RequirePreflightAsync(preparationId, planId,
            checkpointId, sourceRepositoryId, authenticatedReviewerId,
            expectedCandidateEvidenceSha256,
            expectedHumanReviewEvidenceSha256, ct);

        // Safe deterministic exact-scope replay: no repeat provider GET.
        var recorded = await ReadScopedAsync(preparationId, planId,
            checkpointId, sourceRepositoryId, ct);
        if (recorded is not null)
        {
            MatchRecorded(recorded, before);
            return recorded;
        }

        if (!GitHubRefObservationIntegrity.ValidOwner(before.Organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(
                before.TargetRepositoryName))
            throw Refused("CheckpointTargetObservationInvalidDestination");

        // The connection and secret are SERVER-owned, retrieved only after
        // the independent, authenticated PostgreSQL admission. No caller
        // URL/PAT/OID is used or returned. Do not fallback to another host.
        var destination = await connections.GetAsync(
            before.DestinationConnectionId, ct);
        if (destination is null ||
            destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            !string.Equals(destination.Organization, before.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            destination.ValidatedAtUtc is null ||
            destination.ValidatedAtUtc > clock.GetUtcNow() ||
            clock.GetUtcNow() - destination.ValidatedAtUtc >= TimeSpan.FromMinutes(5))
            throw Refused("CheckpointTargetObservationDestinationChanged");

        var credential = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(credential))
            throw Refused("CheckpointTargetObservationCredentialUnavailable");

        GitHubRefObservation response;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                // Existing hardened adapter: fixed api.github.com HTTPS,
                // exactly matching-refs/heads + tags, GET only, no redirect,
                // no partial Link response, max bytes and ref count enforced.
                response = await reader.ReadAsync(before.Organization,
                    before.TargetRepositoryName, credential, timeout.Token);
            }
            catch (GitHubRefObservationException)
            { throw Refused("CheckpointTargetObservationProviderUnverified"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw Refused("CheckpointTargetObservationProviderTimeout"); }
        }

        GitCheckpointTargetObservation sealedReceipt;
        try
        {
            sealedReceipt = GitCheckpointTargetObservationIntegrity.Seal(
                before, response, Guid.NewGuid());
        }
        catch (GitCheckpointTargetObservationException)
        { throw Refused("CheckpointTargetObservationProviderUnverified"); }

        // Recheck #106/#108 with a NEW independent read after the remote
        // GET. Observing GitHub does not make old F-029 current again.
        var after = await RequirePreflightAsync(preparationId, planId,
            checkpointId, sourceRepositoryId, authenticatedReviewerId,
            expectedCandidateEvidenceSha256,
            expectedHumanReviewEvidenceSha256, ct);
        MatchPreflight(after, before);

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        var plan = await db.IncrementalOperationPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == planId &&
                x.PreparationId == preparationId, ct);
        if (plan is null || plan.SourceConnectionId == Guid.Empty)
            throw Refused("CheckpointTargetObservationContextChanged");

        // Source FIRST; plan SECOND. Hold locks through the append-only
        // receipt+event commit so no concurrent staging supersession or
        // human-review transition can pass between recheck and append.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({plan.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);

        var sync = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == plan.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        var newest = await (
            from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join source in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals source.SnapshotId
            where cp.PlanId == planId
            orderby source.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        var sourceProof = await db.GitHubSourceCheckpointGitRefLedgers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        var oldReview = await db.IncrementalReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId, ct);
        var oldEvent = await db.IncrementalReviewEvents.AsNoTracking()
            .Where(x => x.PreparationId == preparationId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var human = await db.GitCheckpointTargetHumanReviews.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        GitCheckpointTargetHumanReviewEventRecord[] humanEvents =
            human is null
            ? [] : await db.GitCheckpointTargetHumanReviewEvents.AsNoTracking()
                .Where(x => x.DecisionId == human.Id)
                .Take(2).ToArrayAsync(ct);
        var connection = await db.Connections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == before.DestinationConnectionId, ct);
        var planView = await new PostgresIncrementalOperationPlanRepository(
            db, clock).GetAsync(preparationId, planId, ct);
        var mappingIsExact = await db.IncrementalOperationSteps.AsNoTracking()
            .AnyAsync(x => x.PlanId == planId &&
                x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.TargetRepositoryName == before.TargetRepositoryName, ct);
        var now = clock.GetUtcNow();

        if (planView is null || planView.IsCurrent ||
            planView.ReadyForExecution || !mappingIsExact ||
            plan.ManifestSha256 != planView.ManifestSha256 ||
            oldReview is null ||
            plan.ReviewSha256 != oldReview.DryRunSha256 ||
            plan.SourceConnectionId != planView.SourceConnectionId ||
            plan.PreparationId != preparationId ||
            plan.DestinationConnectionId != before.DestinationConnectionId ||
            !string.Equals(plan.Organization, before.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            sync != before.SourceSnapshotId ||
            newest is null || newest.Id != checkpointId ||
            newest.SnapshotId != before.SourceSnapshotId ||
            newest.CanPush || newest.CanRetry ||
            newest.CanDispatch || newest.MigrationComplete ||
            sourceProof is null || sourceProof.PlanId != planId ||
            sourceProof.SnapshotId != before.SourceSnapshotId ||
            sourceProof.CanPush || sourceProof.CanRetry ||
            sourceProof.CanDispatch || sourceProof.MigrationComplete ||
            oldReview is null ||
            oldReview.State != IncrementalReviewStates.ApprovedForPlanning ||
            oldEvent is null ||
            oldEvent.EventType != IncrementalReviewStates.ApprovedForPlanning ||
            oldEvent.DryRunSha256 != oldReview.DryRunSha256 ||
            human is null ||
            human.EvidenceSha256 != before.HumanReviewEvidenceSha256 ||
            human.CandidateEvidenceSha256 != before.CandidateEvidenceSha256 ||
            human.ReviewedBy != authenticatedReviewerId ||
            human.Decision != GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            human.PreparationId != preparationId || human.PlanId != planId ||
            human.SourceSnapshotId != before.SourceSnapshotId ||
            human.DestinationConnectionId != before.DestinationConnectionId ||
            human.TargetRepositoryName != before.TargetRepositoryName ||
            human.CanObserveTarget || human.CanPush || human.CanRetry ||
            human.CanDispatch || human.MigrationComplete ||
            humanEvents.Length != 1 ||
            humanEvents[0].Type != human.Decision ||
            humanEvents[0].ReviewerId != authenticatedReviewerId ||
            humanEvents[0].EvidenceSha256 != human.EvidenceSha256 ||
            humanEvents[0].OccurredAtUtc != human.ReviewedAtUtc ||
            connection is null ||
            connection.ProviderKey != "github-enterprise-cloud" ||
            connection.Direction != ConnectionDirection.Destination ||
            connection.ValidationCode != "Verified" ||
            connection.ValidatedAtUtc != oldReview.DestinationValidatedAtUtc ||
            !string.Equals(connection.Organization, before.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            now < before.ValidatedAtUtc || now >= before.ExpiresAtUtc ||
            sealedReceipt.ObservedAtUtc > now.AddSeconds(5))
            throw Refused("CheckpointTargetObservationContextChanged");

        var duplicate = await db.GitCheckpointTargetObservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (duplicate is not null)
        {
            var saved = await VerifiedAsync(duplicate, ct);
            MatchRecorded(saved, before);
            if (saved.TargetEvidenceSha256 != sealedReceipt.TargetEvidenceSha256)
                throw Refused("CheckpointTargetObservationConflict");
            await tx.CommitAsync(ct);
            return saved;
        }

        db.GitCheckpointTargetObservations.Add(new() {
            Id = sealedReceipt.Id, PreparationId = preparationId, PlanId = planId,
            CheckpointId = checkpointId, SourceSnapshotId = before.SourceSnapshotId,
            DestinationConnectionId = before.DestinationConnectionId,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryName = before.TargetRepositoryName,
            Organization = before.Organization, ObjectFormat = before.ObjectFormat,
            ReviewedBy = authenticatedReviewerId,
            CandidateEvidenceSha256 = before.CandidateEvidenceSha256,
            HumanReviewEvidenceSha256 = before.HumanReviewEvidenceSha256,
            ReadPreflightEvidenceSha256 = before.EvidenceSha256,
            TargetEvidenceSha256 = sealedReceipt.TargetEvidenceSha256,
            ObservedAtUtc = sealedReceipt.ObservedAtUtc,
            RefCount = sealedReceipt.RefCount,
            EvidenceSha256 = sealedReceipt.EvidenceSha256,
            ReceiptJson = JsonSerializer.Serialize(sealedReceipt),
            CanObserveTarget = false, CanPush = false, CanRetry = false,
            CanDispatch = false, MigrationComplete = false
        });
        db.GitCheckpointTargetObservationEvents.Add(new() {
            ObservationId = sealedReceipt.Id,
            ReviewedBy = authenticatedReviewerId, Type = "ObservedReadOnly",
            EvidenceSha256 = sealedReceipt.EvidenceSha256,
            OccurredAtUtc = sealedReceipt.ObservedAtUtc
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sealedReceipt;
    }

    public async Task<GitCheckpointTargetObservation?> ReadScopedAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("CheckpointTargetObservationDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId))
            throw Refused("CheckpointTargetObservationInvalidRequest");
        var row = await db.GitCheckpointTargetObservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PreparationId == preparationId &&
                x.PlanId == planId && x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        return row is null ? null : await VerifiedAsync(row, ct);
    }

    private async Task<GitCheckpointTargetObservation> VerifiedAsync(
        GitCheckpointTargetObservationRecord row, CancellationToken ct)
    {
        GitCheckpointTargetObservation receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<GitCheckpointTargetObservation>(
                row.ReceiptJson) ?? throw new JsonException();
            GitCheckpointTargetObservationIntegrity.VerifyStored(receipt);
        }
        catch (Exception ex) when (ex is JsonException or
            GitCheckpointTargetObservationException)
        { throw Refused("CheckpointTargetObservationHistoryCorrupt"); }

        if (receipt.Id != row.Id || receipt.PreparationId != row.PreparationId ||
            receipt.PlanId != row.PlanId || receipt.CheckpointId != row.CheckpointId ||
            receipt.SourceSnapshotId != row.SourceSnapshotId ||
            receipt.DestinationConnectionId != row.DestinationConnectionId ||
            receipt.SourceRepositoryId != row.SourceRepositoryId ||
            receipt.TargetRepositoryName != row.TargetRepositoryName ||
            receipt.Organization != row.Organization ||
            receipt.ObjectFormat != row.ObjectFormat ||
            receipt.ReviewedBy != row.ReviewedBy ||
            receipt.CandidateEvidenceSha256 != row.CandidateEvidenceSha256 ||
            receipt.HumanReviewEvidenceSha256 != row.HumanReviewEvidenceSha256 ||
            receipt.ReadPreflightEvidenceSha256 != row.ReadPreflightEvidenceSha256 ||
            receipt.TargetEvidenceSha256 != row.TargetEvidenceSha256 ||
            receipt.EvidenceSha256 != row.EvidenceSha256 ||
            receipt.ObservedAtUtc != row.ObservedAtUtc ||
            receipt.RefCount != row.RefCount ||
            row.CanObserveTarget || row.CanPush || row.CanRetry ||
            row.CanDispatch || row.MigrationComplete)
            throw Refused("CheckpointTargetObservationHistoryCorrupt");

        var events = await db.GitCheckpointTargetObservationEvents.AsNoTracking()
            .Where(x => x.ObservationId == receipt.Id).Take(2).ToArrayAsync(ct);
        if (events.Length != 1 || events[0].Type != "ObservedReadOnly" ||
            events[0].ReviewedBy != receipt.ReviewedBy ||
            events[0].EvidenceSha256 != receipt.EvidenceSha256 ||
            events[0].OccurredAtUtc != receipt.ObservedAtUtc)
            throw Refused("CheckpointTargetObservationHistoryCorrupt");
        return receipt;
    }

    private async Task<GitCheckpointTargetReadPreflight> RequirePreflightAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string sourceRepositoryId, Guid reviewer,
        string candidate, string human, CancellationToken ct)
    {
        try
        {
            return await prereq.ReadAsync(preparationId, planId,
                checkpointId, sourceRepositoryId, reviewer,
                candidate, human, ct);
        }
        catch (GitCheckpointTargetReadPreflightException)
        { throw Refused("CheckpointTargetObservationPreflightUnverified"); }
    }

    private static void MatchPreflight(GitCheckpointTargetReadPreflight current,
        GitCheckpointTargetReadPreflight original)
    {
        if (current.PreparationId != original.PreparationId ||
            current.PlanId != original.PlanId ||
            current.CheckpointId != original.CheckpointId ||
            current.SourceSnapshotId != original.SourceSnapshotId ||
            current.DestinationConnectionId != original.DestinationConnectionId ||
            current.SourceRepositoryId != original.SourceRepositoryId ||
            current.TargetRepositoryName != original.TargetRepositoryName ||
            current.Organization != original.Organization ||
            current.ObjectFormat != original.ObjectFormat ||
            current.SourceRefCount != original.SourceRefCount ||
            current.ReviewedBy != original.ReviewedBy ||
            current.CandidateEvidenceSha256 != original.CandidateEvidenceSha256 ||
            current.HumanReviewEvidenceSha256 != original.HumanReviewEvidenceSha256)
            throw Refused("CheckpointTargetObservationContextChanged");
    }

    private static void MatchRecorded(
        GitCheckpointTargetObservation receipt,
        GitCheckpointTargetReadPreflight before)
    {
        if (receipt.CheckpointId != before.CheckpointId ||
            receipt.PreparationId != before.PreparationId ||
            receipt.PlanId != before.PlanId ||
            receipt.SourceSnapshotId != before.SourceSnapshotId ||
            receipt.DestinationConnectionId != before.DestinationConnectionId ||
            receipt.SourceRepositoryId != before.SourceRepositoryId ||
            receipt.TargetRepositoryName != before.TargetRepositoryName ||
            receipt.Organization != before.Organization ||
            receipt.ObjectFormat != before.ObjectFormat ||
            receipt.ReviewedBy != before.ReviewedBy ||
            receipt.CandidateEvidenceSha256 != before.CandidateEvidenceSha256 ||
            receipt.HumanReviewEvidenceSha256 != before.HumanReviewEvidenceSha256)
            throw Refused("CheckpointTargetObservationConflict");
    }
}
