using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-089: O(1) global journal/sync reads and at most ten server-side
// PostgreSQL aggregate queries. Never scans refs into application RAM.
// Each row is tied to the immutable F-029 manifest and F-025 change
// journal, not arbitrary caller-provided repository names/IDs.
public sealed class PostgresGitHubMultiRepositorySyncPulseReader(
    AppDbContext db,
    GitHubMultiRepositorySyncPulseOptions options,
    IIncrementalOperationPlanRepository plans,
    IStagingRepository staging,
    GitHubRemoteRecoveryWorkbenchOptions recoveryOptions,
    PostgresGitHubRemoteRecoveryWorkbenchReader recovery)
{
    private sealed record RefKinds(int Added, int Changed,
        int Restored, int Missing)
    {
        public int Total => Added + Changed + Restored + Missing;
    }

    public async Task<GitHubMultiRepositorySyncPulse> InspectAsync(
        Guid preparationId, Guid planId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("MultiRepositoryPulseDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty)
            throw Refused("MultiRepositoryPulseQueryInvalid");

        // F-029 verifies original immutable manifest digest, DAG and
        // preparation linkage. Historical plans are still inspectable.
        IncrementalOperationPlan plan;
        try
        {
            plan = await plans.GetAsync(preparationId, planId, ct)
                ?? throw Refused("MultiRepositoryPulseNotFound");
        }
        catch (IncrementalOperationPlanException)
        { throw Refused("MultiRepositoryPulsePlanCorrupt"); }

        var prepared = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == preparationId &&
                x.SnapshotId == plan.SnapshotId &&
                x.SourceConnectionId == plan.SourceConnectionId &&
                x.DestinationConnectionId == plan.DestinationConnectionId, ct)
            ?? throw Refused("MultiRepositoryPulseHistoryInvalid");
        if (prepared.State != IncrementalPreparationStates.Prepared ||
            prepared.EvidenceSha256 != plan.PreparationEvidenceSha256 ||
            plan.ReadyForExecution || plan.RepositoryCount is < 1 or > 10 ||
            plan.RepositoryCount * 6 != plan.StepCount)
            throw Refused("MultiRepositoryPulseHistoryInvalid");

        StagingSyncResult synced;
        StagingChangeSummary journal;
        try
        {
            synced = await staging.GetSyncAsync(plan.SnapshotId, ct)
                ?? throw Refused("MultiRepositoryPulseSyncMissing");
            journal = await staging.GetChangeSummaryAsync(plan.SnapshotId, ct)
                ?? throw Refused("MultiRepositoryPulseJournalMissing");
        }
        catch (StagingChangeIntegrityException)
        { throw Refused("MultiRepositoryPulseJournalCorrupt"); }
        catch (StagingChangeJournalUnavailableException)
        { throw Refused("MultiRepositoryPulseJournalMissing"); }

        if (synced.SourceConnectionId != plan.SourceConnectionId ||
            journal.SourceConnectionId != plan.SourceConnectionId ||
            journal.SnapshotId != plan.SnapshotId ||
            journal.Total != prepared.ItemCount ||
            synced.Observed < 0 || synced.Added < 0 || synced.Changed < 0 ||
            synced.Unchanged < 0 || synced.MarkedMissing < 0)
            throw Refused("MultiRepositoryPulseJournalCorrupt");

        // Latest *committed PostgreSQL* snapshot; NOT a claim that the
        // external Azure DevOps/TFS source has no newer changes.
        var latest = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == plan.SourceConnectionId)
            .OrderByDescending(x => x.AppliedAtUtc)
            .ThenByDescending(x => x.SnapshotId)
            .Select(x => new { x.SnapshotId, x.AppliedAtUtc })
            .FirstOrDefaultAsync(ct)
            ?? throw Refused("MultiRepositoryPulseSyncMissing");
        var job = await db.MigrationJobs.AsNoTracking()
            .Where(x => x.SourceConnectionId == plan.SourceConnectionId &&
                x.SnapshotId == plan.SnapshotId &&
                (x.Kind == MigrationJobKinds.StageSnapshot ||
                 x.Kind == MigrationJobKinds.DiscoverSource))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        var mappings = plan.Steps
            .Where(x => x.Kind == "ReviewTargetName")
            .OrderBy(x => x.SourceRepositoryId, StringComparer.Ordinal)
            .ToArray();
        if (mappings.Length != plan.RepositoryCount)
            throw Refused("MultiRepositoryPulsePlanCorrupt");

        // At most one explicitly reviewed sandbox can have historical
        // F-082 evidence inspected by F-088. Other mapped repositories
        // are visible as SOURCE synchronization only, without guessing
        // numeric GitHub IDs or weakening the independent allowlists.
        var monitored = mappings.SingleOrDefault(x =>
            recoveryOptions.Enabled && recoveryOptions.AllowedRepositoryId > 0 &&
            string.Equals(
                plan.Organization + "/" + x.TargetRepositoryName,
                recoveryOptions.AllowedRepository,
                StringComparison.OrdinalIgnoreCase));
        GitHubRemoteRecoveryWorkbenchItem? latestRecovery = null;
        if (monitored is not null)
        {
            var history = await recovery.ListAsync(
                preparationId, planId, null, 1, ct);
            latestRecovery = history.Items.SingleOrDefault();
        }

        var result = new List<GitHubRepositorySourceDelta>(mappings.Length);
        foreach (var mapping in mappings)
        {
            // The ref natural key is canonical JSON [repositoryId,refName].
            // PostgreSQL aggregates by exact decoded first element; there
            // is no LIKE wildcard/prefix collision, and no ref/OID payload
            // is ever materialized in the API process.
            var json = await db.Database.SqlQuery<string>($"""
                SELECT json_build_object(
                    'Added', count(*) FILTER (WHERE change_kind = 'Added'),
                    'Changed', count(*) FILTER (WHERE change_kind = 'Changed'),
                    'Restored', count(*) FILTER (WHERE change_kind = 'Restored'),
                    'Missing', count(*) FILTER (WHERE change_kind = 'Missing')
                )::text AS "Value"
                FROM incremental_preparation_items
                WHERE preparation_id = {preparationId}
                  AND asset_type = 'ref'
                  AND source_id::jsonb ->> 0 = {mapping.SourceRepositoryId}
                """).SingleAsync(ct);
            RefKinds refKinds;
            try { refKinds = JsonSerializer.Deserialize<RefKinds>(json)!; }
            catch (JsonException) { throw Refused("MultiRepositoryPulseJournalCorrupt"); }
            if (refKinds is null || refKinds.Total != mapping.ChangedRefs)
                throw Refused("MultiRepositoryPulseJournalCorrupt");

            var inSandbox = monitored is not null &&
                string.Equals(mapping.SourceRepositoryId,
                    monitored.SourceRepositoryId, StringComparison.Ordinal);
            var tracking = !inSandbox
                ? GitHubMultiRepositoryRecoveryTracking.NotMonitored
                : latestRecovery is null
                    ? GitHubMultiRepositoryRecoveryTracking.NoMarkerRecorded
                    : GitHubMultiRepositoryRecoveryTracking.PossibleSendEvidence;
            result.Add(new(mapping.SourceRepositoryId,
                mapping.TargetRepositoryName, refKinds.Added,
                refKinds.Changed, refKinds.Restored, refKinds.Missing,
                mapping.ChangedRefs, mapping.State, mapping.BlockerCode,
                tracking, inSandbox ? latestRecovery?.State : null,
                inSandbox ? latestRecovery?.BarrierId : null,
                inSandbox ? latestRecovery?.PossibleSendRecordedAtUtc : null,
                tracking == GitHubMultiRepositoryRecoveryTracking.PossibleSendEvidence,
                false, false, false));
        }
        var pulse = new GitHubMultiRepositorySyncPulse(
            preparationId, planId, plan.SourceConnectionId,
            plan.SnapshotId, plan.Organization, plan.ManifestSha256,
            plan.IsCurrent, false, prepared.State,
            latest.SnapshotId == plan.SnapshotId
                ? GitHubMultiRepositoryStagingStates.LatestRecordedSnapshot
                : GitHubMultiRepositoryStagingStates.SupersededInPostgres,
            latest.SnapshotId, synced.SourceCollectedAtUtc,
            synced.AppliedAtUtc, latest.AppliedAtUtc,
            synced.SourceInventoryComplete, synced.Observed,
            synced.Added, synced.Changed, synced.Unchanged,
            synced.MarkedMissing, journal.Added, journal.Changed,
            journal.Restored, journal.Missing, job?.Id,
            job?.State ?? "DirectStagingWithoutJob",
            job?.LastErrorCode, job?.UpdatedAtUtc,
            result, false, false, false, false);
        GitHubMultiRepositorySyncPulseIntegrity.Verify(pulse);
        return pulse;
    }

    private static GitHubMultiRepositorySyncPulseException Refused(string code)
        => new(code);
}
