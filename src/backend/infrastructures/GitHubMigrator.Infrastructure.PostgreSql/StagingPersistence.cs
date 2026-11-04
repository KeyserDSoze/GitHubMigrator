using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class StagingAssetRecord
{
    public Guid SourceConnectionId { get; set; }
    public string AssetType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string? ProjectId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public Guid FirstSnapshotId { get; set; }
    public Guid LastSeenSnapshotId { get; set; }
    public Guid LastChangedSnapshotId { get; set; }
    public int Revision { get; set; }
    public bool Missing { get; set; }
}

public sealed class StagingSnapshotSyncRecord
{
    public Guid SnapshotId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public DateTimeOffset SourceCollectedAtUtc { get; set; }
    public DateTimeOffset AppliedAtUtc { get; set; }
    public int Observed { get; set; }
    public int Added { get; set; }
    public int Changed { get; set; }
    public int Unchanged { get; set; }
    public int MarkedMissing { get; set; }
    public bool SourceInventoryComplete { get; set; }
}

public sealed class PostgresStagingRepository(AppDbContext db, TimeProvider clock) : IStagingRepository
{
    private const int BatchSize = 250;

    public async Task<StagingSyncResult> ApplyAsync(DiscoveryInventory inventory,
        IReadOnlyList<StagingAssetInput> assets, IReadOnlySet<string> completeTypes,
        CancellationToken ct)
    {
        if (inventory.Id != inventory.Snapshot.SnapshotId ||
            assets.Count > StagingAssetNormalizer.MaxAssetsPerSnapshot)
            throw new StagingSyncInputException("InvalidStagingSnapshot");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize imports for the same source connection, including across API instances.
        // The lock is held until the commit/rollback, protecting ordering + idempotency.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({inventory.ConnectionId.ToString()}), 732911)", ct);

        var previous = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == inventory.Id, ct);
        if (previous is not null)
        {
            if (previous.SourceConnectionId != inventory.ConnectionId)
                throw new StagingSyncInputException("SnapshotConnectionMismatch");
            await transaction.CommitAsync(ct);
            return ToResult(previous);
        }

        var mostRecent = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == inventory.ConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .Select(x => (DateTimeOffset?)x.SourceCollectedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (mostRecent is not null && inventory.CollectedAtUtc <= mostRecent)
            throw new StagingStaleSnapshotException();

        // One SQL upsert for each bounded JSONB batch; no per-asset database round trips.
        // Revisions advance only for actual fingerprint changes or reappearing missing assets.
        for (var i = 0; i < assets.Count; i += BatchSize)
        {
            var batch = JsonSerializer.Serialize(assets.Skip(i).Take(BatchSize)
                .Select(x => new { asset_type = x.AssetType, source_id = x.SourceId,
                    project_id = x.ProjectId, fingerprint = x.Fingerprint,
                    payload_json = x.PayloadJson }));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH incoming AS (
                    SELECT * FROM jsonb_to_recordset({batch}::jsonb) AS items(
                        asset_type text, source_id text, project_id text,
                        fingerprint text, payload_json text)
                )
                INSERT INTO staging_assets (
                    source_connection_id, asset_type, source_id, project_id, fingerprint,
                    payload_json, first_snapshot_id, last_seen_snapshot_id,
                    last_changed_snapshot_id, revision, missing)
                SELECT {inventory.ConnectionId}, asset_type, source_id, project_id, fingerprint,
                    payload_json::jsonb, {inventory.Id}, {inventory.Id}, {inventory.Id}, 1, false
                FROM incoming
                ON CONFLICT (source_connection_id, asset_type, source_id) DO UPDATE SET
                    project_id = EXCLUDED.project_id,
                    fingerprint = EXCLUDED.fingerprint,
                    payload_json = CASE WHEN staging_assets.fingerprint IS DISTINCT FROM EXCLUDED.fingerprint
                        THEN EXCLUDED.payload_json ELSE staging_assets.payload_json END,
                    last_seen_snapshot_id = EXCLUDED.last_seen_snapshot_id,
                    last_changed_snapshot_id = CASE WHEN
                        staging_assets.fingerprint IS DISTINCT FROM EXCLUDED.fingerprint OR staging_assets.missing
                        THEN EXCLUDED.last_changed_snapshot_id ELSE staging_assets.last_changed_snapshot_id END,
                    revision = staging_assets.revision + CASE WHEN
                        staging_assets.fingerprint IS DISTINCT FROM EXCLUDED.fingerprint OR staging_assets.missing
                        THEN 1 ELSE 0 END,
                    missing = false
                """, ct);
        }

        if (completeTypes.Count > 0)
        {
            var kinds = completeTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE staging_assets
                SET missing = true, revision = revision + 1,
                    last_changed_snapshot_id = {inventory.Id}
                WHERE source_connection_id = {inventory.ConnectionId}
                  AND asset_type = ANY({kinds})
                  AND last_seen_snapshot_id <> {inventory.Id}
                  AND missing = false
                """, ct);
        }

        // Fast index-backed counts: no materialization of the entire catalog into API memory.
        var added = await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == inventory.ConnectionId && x.FirstSnapshotId == inventory.Id, ct);
        var changed = await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == inventory.ConnectionId && x.FirstSnapshotId != inventory.Id &&
            x.LastChangedSnapshotId == inventory.Id && !x.Missing, ct);
        var missing = await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == inventory.ConnectionId &&
            x.LastChangedSnapshotId == inventory.Id && x.Missing, ct);

        var fullyCovered = inventory.Completeness == DiscoveryCompleteness.Complete &&
            inventory.IdentitiesComplete && inventory.PermissionsComplete &&
            inventory.SecurityCoverage.GroupsComplete &&
            inventory.SecurityCoverage.MembershipsComplete &&
            inventory.SecurityCoverage.AclsCollected &&
            inventory.SecurityCoverage.EffectivePermissionsVerified;

        var saved = new StagingSnapshotSyncRecord
        {
            SnapshotId = inventory.Id, SourceConnectionId = inventory.ConnectionId,
            // PostgreSQL timestamptz uses microseconds; normalize BEFORE returning
            // so the first request and all idempotent replays serialize identically.
            SourceCollectedAtUtc = PostgreSqlTimestamp(inventory.CollectedAtUtc),
            AppliedAtUtc = PostgreSqlTimestamp(clock.GetUtcNow()), Observed = assets.Count,
            Added = added, Changed = changed, Unchanged = assets.Count - added - changed,
            MarkedMissing = missing, SourceInventoryComplete = fullyCovered
        };
        db.StagingSnapshotSyncs.Add(saved);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToResult(saved);
    }

    public async Task<StagingSyncResult?> GetSyncAsync(Guid snapshotId, CancellationToken ct)
    {
        var row = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == snapshotId, ct);
        return row is null ? null : ToResult(row);
    }

    public async Task<StagingCatalogSummary> GetSummaryAsync(Guid sourceConnectionId, CancellationToken ct)
    {
        var latest = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == sourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc).FirstOrDefaultAsync(ct);
        var active = await db.StagingAssets.AsNoTracking()
            .CountAsync(x => x.SourceConnectionId == sourceConnectionId && !x.Missing, ct);
        var missing = await db.StagingAssets.AsNoTracking()
            .CountAsync(x => x.SourceConnectionId == sourceConnectionId && x.Missing, ct);
        var revisions = await db.StagingAssets.AsNoTracking()
            .Where(x => x.SourceConnectionId == sourceConnectionId)
            .SumAsync(x => (long?)x.Revision, ct) ?? 0;
        return new(sourceConnectionId, latest?.SnapshotId, latest?.AppliedAtUtc,
            active, missing, checked((int)Math.Min(revisions, int.MaxValue)));
    }

    public async Task<StagingAssetPage> ListAssetsAsync(Guid sourceConnectionId,
        string assetType, string? after, int take, CancellationToken ct)
    {
        // Keyset pagination uses the PK index (source_connection_id, asset_type, source_id).
        // No OFFSET scan and no materialization of the entire asset catalog.
        var rows = await db.StagingAssets.FromSqlInterpolated($"""
                SELECT * FROM staging_assets
                WHERE source_connection_id = {sourceConnectionId}
                  AND asset_type = {assetType}
                  AND source_id > {after ?? ""}
                ORDER BY source_id
                LIMIT {take + 1}
                """).AsNoTracking().ToListAsync(ct);
        var hasNext = rows.Count > take;
        var page = rows.Take(take).Select(row => new StagingAssetView(
            row.AssetType, row.SourceId, row.ProjectId, row.Fingerprint,
            row.Revision, row.Missing, row.FirstSnapshotId,
            row.LastSeenSnapshotId, row.LastChangedSnapshotId,
            JsonSerializer.Deserialize<JsonElement>(row.PayloadJson))).ToArray();
        return new(sourceConnectionId, assetType, page,
            hasNext ? page[^1].SourceId : null);
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10)); // 10 ticks = 1 microsecond
    }

    private static StagingSyncResult ToResult(StagingSnapshotSyncRecord row) =>
        new(row.SnapshotId, row.SourceConnectionId, row.SourceCollectedAtUtc,
            row.AppliedAtUtc, row.Observed, row.Added, row.Changed, row.Unchanged,
            row.MarkedMissing, row.SourceInventoryComplete);
}
