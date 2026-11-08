using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-090: durable exactly-once source-staging checkpoint progression.
// Holds the SAME source lock as F-013 staging before looking for the next
// snapshot, plus a plan lock to prevent concurrent API replicas creating
// two successor epochs. No GitHub adapter/credential/sender is injected.
public sealed class PostgresGitHubSourceSyncCheckpointJournal(
    AppDbContext db, TimeProvider clock,
    GitHubSourceSyncCheckpointOptions options,
    IIncrementalOperationPlanRepository plans, IStagingRepository staging)
    : IGitHubSourceSyncCheckpointJournal
{
    private sealed record RefKinds(int Added, int Changed,
        int Restored, int Missing)
    {
        public int Total => checked(Added + Changed + Restored + Missing);
    }

    public async Task<GitHubSourceSyncCheckpointReceipt> CaptureAsync(
        Guid preparationId, Guid planId, Guid expectedSnapshotId,
        Guid? expectedPreviousCheckpointId, string expectedManifestSha256,
        Guid actorId, CancellationToken ct)
    {
        RequireEnabled();
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            expectedSnapshotId == Guid.Empty || actorId == Guid.Empty ||
            expectedPreviousCheckpointId == Guid.Empty ||
            string.IsNullOrWhiteSpace(expectedManifestSha256))
            throw Refused("SourceSyncCheckpointInvalidRequest");

        // Resolve the already-reviewed immutable plan; never accept source
        // repo identities, source connections or ref names from the caller.
        var plan = await RequirePlanAsync(preparationId, planId, ct);
        if (plan.ManifestSha256 != expectedManifestSha256)
            throw Refused("SourceSyncCheckpointManifestChanged");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Lock ordering: existing F-013 source staging lock FIRST,
        // then the new plan-specific lock, both held until commit.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({plan.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({plan.Id.ToString()}), 732990)", ct);

        plan = await RequirePlanAsync(preparationId, planId, ct);
        if (plan.ManifestSha256 != expectedManifestSha256)
            throw Refused("SourceSyncCheckpointManifestChanged");
        var prior = await LastRecordAsync(plan.Id, ct);

        // A concurrent operator may already have saved precisely THIS
        // snapshot. Return verified immutable evidence, never create a
        // new epoch or advance to another snapshot during its replay.
        var existing = await db.GitHubSourceSyncCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanId == plan.Id &&
                x.SnapshotId == expectedSnapshotId, ct);
        if (existing is not null)
        {
            if (existing.PreviousCheckpointId != expectedPreviousCheckpointId ||
                existing.ManifestSha256 != expectedManifestSha256)
                throw Refused("SourceSyncCheckpointReplayConflict");
            var known = await VerifiedAsync(existing, plan, ct);
            await tx.CommitAsync(ct);
            return known;
        }

        if (prior?.Id != expectedPreviousCheckpointId)
            throw Refused("SourceSyncCheckpointCursorChanged");

        var candidate = await NextSnapshotAsync(plan, prior, ct);
        if (candidate is null || candidate.SnapshotId != expectedSnapshotId)
            throw Refused("SourceSyncCheckpointOutOfOrder");

        var prevReceipt = prior is null
            ? null : await VerifiedAsync(prior, plan, ct);
        var proof = await BuildProofAsync(
            plan, candidate, prevReceipt, ct);
        var digest = GitHubSourceSyncCheckpointIntegrity.Digest(proof);
        var recordedAt = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var record = new GitHubSourceSyncCheckpointRecord
        {
            Id = Guid.NewGuid(), PreparationId = preparationId,
            PlanId = planId, SourceConnectionId = plan.SourceConnectionId,
            SnapshotId = candidate.SnapshotId,
            PreviousCheckpointId = prior?.Id,
            ManifestSha256 = plan.ManifestSha256,
            ProofJson = JsonSerializer.Serialize(proof),
            EvidenceSha256 = digest,
            RecordedBy = actorId, RecordedAtUtc = recordedAt,
            CanPush = false, CanRetry = false,
            CanDispatch = false, MigrationComplete = false
        };
        db.GitHubSourceSyncCheckpoints.Add(record);
        db.GitHubSourceSyncCheckpointEvents.Add(new()
        {
            CheckpointId = record.Id, Type = "Recorded",
            ActorId = actorId, EvidenceSha256 = digest,
            OccurredAtUtc = recordedAt
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return FromStored(record, proof);
    }

    public async Task<GitHubSourceSyncCheckpointPage> ListAsync(
        Guid preparationId, Guid planId, Guid? after, int take,
        CancellationToken ct)
    {
        RequireEnabled();
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            after == Guid.Empty || take is < 1 or > 10)
            throw Refused("SourceSyncCheckpointInvalidPage");
        var plan = await RequirePlanAsync(preparationId, planId, ct);
        var prior = await LastRecordAsync(plan.Id, ct);
        var next = await NextSnapshotAsync(plan, prior, ct);
        var source = from checkpoint in
                db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join stage in db.StagingSnapshotSyncs.AsNoTracking()
                on checkpoint.SnapshotId equals stage.SnapshotId
            where checkpoint.PlanId == plan.Id &&
                checkpoint.PreparationId == preparationId &&
                checkpoint.SourceConnectionId == plan.SourceConnectionId
            select new { Checkpoint = checkpoint,
                Collected = stage.SourceCollectedAtUtc };

        if (after is Guid cursor)
        {
            var at = await source.Where(x =>
                x.Checkpoint.Id == cursor)
                .Select(x => new { x.Collected, x.Checkpoint.Id })
                .SingleOrDefaultAsync(ct);
            if (at is null)
                throw Refused("SourceSyncCheckpointCursorInvalid");
            source = source.Where(x => x.Collected < at.Collected ||
                (x.Collected == at.Collected &&
                 x.Checkpoint.Id.CompareTo(at.Id) < 0));
        }

        var rows = await source
            .OrderByDescending(x => x.Collected)
            .ThenByDescending(x => x.Checkpoint.Id)
            .Take(take + 1)
            .ToArrayAsync(ct);
        var selected = rows.Take(take).ToArray();
        var results = new List<GitHubSourceSyncCheckpointReceipt>(
            selected.Length);
        foreach (var entry in selected)
            results.Add(await VerifiedAsync(entry.Checkpoint, plan, ct));
        if (prior is not null && !results.Any(x => x.Id == prior.Id))
            await VerifiedAsync(prior, plan, ct);
        return new(preparationId, planId, results,
            rows.Length > take ? results[^1].Id : null,
            prior?.Id, next?.SnapshotId, prior?.EvidenceSha256,
            false, false, false, false);
    }

    private void RequireEnabled()
    {
        if (!options.Enabled)
            throw Refused("SourceSyncCheckpointDisabled");
    }

    private async Task<IncrementalOperationPlan> RequirePlanAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        try
        {
            var plan = await plans.GetAsync(preparationId, planId, ct)
                ?? throw Refused("SourceSyncCheckpointPlanNotFound");
            if (plan.ReadyForExecution || plan.RepositoryCount is < 1 or > 10 ||
                plan.SourceConnectionId == Guid.Empty ||
                plan.RepositoryCount * 6 != plan.StepCount ||
                plan.ManifestSha256.Length != 64)
                throw Refused("SourceSyncCheckpointPlanCorrupt");
            return plan;
        }
        catch (IncrementalOperationPlanException)
        { throw Refused("SourceSyncCheckpointPlanCorrupt"); }
    }

    private async Task<GitHubSourceSyncCheckpointRecord?> LastRecordAsync(
        Guid planId, CancellationToken ct) =>
        await (from receipt in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join sync in db.StagingSnapshotSyncs.AsNoTracking()
                on receipt.SnapshotId equals sync.SnapshotId
            where receipt.PlanId == planId
            orderby sync.SourceCollectedAtUtc descending, receipt.Id descending
            select receipt).FirstOrDefaultAsync(ct);

    private async Task<StagingSnapshotSyncRecord?> NextSnapshotAsync(
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointRecord? prior, CancellationToken ct)
    {
        if (prior is null)
            return await db.StagingSnapshotSyncs.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.SnapshotId == plan.SnapshotId &&
                    x.SourceConnectionId == plan.SourceConnectionId, ct);
        var lastStage = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == prior.SnapshotId &&
                x.SourceConnectionId == plan.SourceConnectionId, ct)
            ?? throw Refused("SourceSyncCheckpointHistoryCorrupt");
        return await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == plan.SourceConnectionId &&
                x.SourceCollectedAtUtc > lastStage.SourceCollectedAtUtc)
            .OrderBy(x => x.SourceCollectedAtUtc)
            .ThenBy(x => x.SnapshotId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<GitHubSourceSyncCheckpointProof> BuildProofAsync(
        IncrementalOperationPlan plan, StagingSnapshotSyncRecord sync,
        GitHubSourceSyncCheckpointReceipt? previous, CancellationToken ct)
    {
        StagingChangeSummary journal;
        try
        {
            journal = await staging.GetChangeSummaryAsync(sync.SnapshotId, ct)
                ?? throw Refused("SourceSyncCheckpointJournalMissing");
        }
        catch (StagingChangeJournalUnavailableException)
        { throw Refused("SourceSyncCheckpointJournalMissing"); }
        catch (StagingChangeIntegrityException)
        { throw Refused("SourceSyncCheckpointJournalCorrupt"); }
        if (journal.SourceConnectionId != plan.SourceConnectionId ||
            journal.SnapshotId != sync.SnapshotId ||
            sync.JournalVersion != 1)
            throw Refused("SourceSyncCheckpointJournalCorrupt");

        var mappings = plan.Steps
            .Where(x => x.Kind == "ReviewTargetName")
            .OrderBy(x => x.SourceRepositoryId, StringComparer.Ordinal)
            .ToArray();
        if (mappings.Length != plan.RepositoryCount)
            throw Refused("SourceSyncCheckpointPlanCorrupt");

        var refs = await db.StagingAssetChanges.AsNoTracking()
            .Where(x => x.SnapshotId == sync.SnapshotId &&
                x.SourceConnectionId == plan.SourceConnectionId &&
                x.AssetType == "ref")
            .CountAsync(ct);
        var mapped = new List<GitHubSourceRefDelta>(mappings.Length);
        foreach (var step in mappings)
        {
            // GROUP BY is SQL-side: exact first element of the canonical
            // JSON ref key, not prefix matching or loaded Git OIDs.
            var json = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object(
                    'Added', count(*) FILTER (WHERE change_kind = 'Added'),
                    'Changed', count(*) FILTER (WHERE change_kind = 'Changed'),
                    'Restored', count(*) FILTER (WHERE change_kind = 'Restored'),
                    'Missing', count(*) FILTER (WHERE change_kind = 'Missing')
                )::text AS "Value"
                FROM staging_asset_changes
                WHERE snapshot_id = {sync.SnapshotId}
                  AND source_connection_id = {plan.SourceConnectionId}
                  AND asset_type = 'ref'
                  AND source_id::jsonb ->> 0 = {step.SourceRepositoryId}
                """).SingleAsync(ct);
            RefKinds counts;
            try { counts = JsonSerializer.Deserialize<RefKinds>(json)!; }
            catch (JsonException)
            { throw Refused("SourceSyncCheckpointJournalCorrupt"); }
            if (counts is null)
                throw Refused("SourceSyncCheckpointJournalCorrupt");
            mapped.Add(new(step.SourceRepositoryId,
                step.TargetRepositoryName, counts.Added,
                counts.Changed, counts.Restored, counts.Missing));
            if (sync.SnapshotId == plan.SnapshotId &&
                step.ChangedRefs != counts.Total)
                throw Refused("SourceSyncCheckpointPlanDeltasMismatch");
        }
        var unknown = refs - mapped.Sum(x => x.Total);
        if (unknown < 0)
            throw Refused("SourceSyncCheckpointJournalCorrupt");

        var proof = new GitHubSourceSyncCheckpointProof(
            plan.PreparationId, plan.Id, plan.SourceConnectionId,
            sync.SnapshotId, plan.SnapshotId,
            previous?.Id, previous?.EvidenceSha256,
            plan.ManifestSha256, sync.SourceCollectedAtUtc,
            sync.AppliedAtUtc, sync.SourceInventoryComplete,
            sync.SnapshotId == plan.SnapshotId,
            journal.Added, journal.Changed, journal.Restored,
            journal.Missing, refs, unknown, mapped);
        GitHubSourceSyncCheckpointIntegrity.Verify(proof);
        return proof;
    }

    private async Task<GitHubSourceSyncCheckpointReceipt> VerifiedAsync(
        GitHubSourceSyncCheckpointRecord row,
        IncrementalOperationPlan plan, CancellationToken ct)
    {
        GitHubSourceSyncCheckpointProof proof;
        try {
            proof = JsonSerializer.Deserialize<GitHubSourceSyncCheckpointProof>(
                row.ProofJson) ?? throw Refused("SourceSyncCheckpointHistoryCorrupt");
        }
        catch (JsonException)
        { throw Refused("SourceSyncCheckpointHistoryCorrupt"); }

        var receipt = FromStored(row, proof);
        try { GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(receipt); }
        catch (GitHubSourceSyncCheckpointException)
        { throw Refused("SourceSyncCheckpointHistoryCorrupt"); }
        if (row.PreparationId != plan.PreparationId ||
            row.PlanId != plan.Id ||
            row.SourceConnectionId != plan.SourceConnectionId ||
            row.ManifestSha256 != plan.ManifestSha256 ||
            proof.PreparationId != plan.PreparationId ||
            proof.PlanId != plan.Id ||
            proof.SourceConnectionId != plan.SourceConnectionId ||
            proof.SnapshotId != row.SnapshotId ||
            proof.BaselineSnapshotId != plan.SnapshotId ||
            proof.PreviousCheckpointId != row.PreviousCheckpointId ||
            proof.ManifestSha256 != plan.ManifestSha256)
            throw Refused("SourceSyncCheckpointHistoryCorrupt");

        var events = await db.GitHubSourceSyncCheckpointEvents.AsNoTracking()
            .Where(x => x.CheckpointId == row.Id).Take(2).ToArrayAsync(ct);
        if (events.Length != 1 || events[0].Type != "Recorded" ||
            events[0].ActorId != row.RecordedBy ||
            events[0].EvidenceSha256 != row.EvidenceSha256 ||
            events[0].OccurredAtUtc != row.RecordedAtUtc)
            throw Refused("SourceSyncCheckpointHistoryCorrupt");
        if (row.PreviousCheckpointId is Guid prevId)
        {
            var prior = await db.GitHubSourceSyncCheckpoints.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == prevId && x.PlanId == plan.Id, ct)
                ?? throw Refused("SourceSyncCheckpointHistoryCorrupt");
            if (proof.PreviousEvidenceSha256 != prior.EvidenceSha256)
                throw Refused("SourceSyncCheckpointHistoryCorrupt");
        }
        var stage = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == row.SnapshotId &&
                x.SourceConnectionId == plan.SourceConnectionId, ct)
            ?? throw Refused("SourceSyncCheckpointHistoryCorrupt");
        // Detect count/journal drift in existing checkpoint source metadata.
        var current = await BuildProofAsync(plan, stage,
            row.PreviousCheckpointId is null ? null :
            new GitHubSourceSyncCheckpointReceipt(
                row.PreviousCheckpointId.Value,
                // This proof is never used by BuildProofAsync; only ID+SHA.
                proof with { SnapshotId = proof.BaselineSnapshotId,
                    IsBaselineSnapshot = true,
                    PreviousCheckpointId = null,
                    PreviousEvidenceSha256 = null },
                proof.PreviousEvidenceSha256!,
                row.RecordedBy, row.RecordedAtUtc,
                false, false, false, false), ct);
        if (GitHubSourceSyncCheckpointIntegrity.Digest(current) !=
                row.EvidenceSha256)
            throw Refused("SourceSyncCheckpointJournalDrift");
        return receipt;
    }

    private static GitHubSourceSyncCheckpointReceipt FromStored(
        GitHubSourceSyncCheckpointRecord row,
        GitHubSourceSyncCheckpointProof proof) =>
        new(row.Id, proof, row.EvidenceSha256,
            row.RecordedBy, row.RecordedAtUtc,
            row.CanPush, row.CanRetry, row.CanDispatch,
            row.MigrationComplete);

    private static GitHubSourceSyncCheckpointException Refused(string code)
        => new(code);
}
