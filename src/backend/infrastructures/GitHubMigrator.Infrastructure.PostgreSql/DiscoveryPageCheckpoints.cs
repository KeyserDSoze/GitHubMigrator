using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class DiscoveryPageCheckpointRecord
{
    public Guid JobId { get; set; }
    public string ResourceKey { get; set; } = "";
    public int PageIndex { get; set; }
    public string RequestHash { get; set; } = "";
    public string SafePayloadJson { get; set; } = "{}";
    public string? NextContinuation { get; set; }
    public DateTimeOffset SavedAtUtc { get; set; }
}

// Checkpoint ownership is bound to the durable job's active attempt. Older
// workers are never allowed to insert pages after another worker has reclaimed
// the lease. Every committed page is immutable and individually resumable.
public sealed class PostgresDiscoveryPageCheckpointStore(AppDbContext db, TimeProvider clock)
    : IDiscoveryPageCheckpointStore, IDiscoveryRefIngestProgress
{
    private const int MaxPageBytes = 1_048_576;
    private const int MaxResourceLength = 256;

    public async Task<DiscoveryPageCheckpoint?> GetAsync(
        Guid jobId, string resourceKey, int pageIndex, CancellationToken ct)
    {
        ValidateKey(jobId, resourceKey, pageIndex);
        var row = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == jobId &&
                x.ResourceKey == resourceKey && x.PageIndex == pageIndex, ct);
        return row is null ? null : ToCheckpoint(row);
    }

    public async Task<DiscoveryPageCheckpoint> SaveAsync(
        DiscoveryLeaseContext lease, DiscoveryPageCheckpoint page, CancellationToken ct)
    {
        ValidateKey(page.JobId, page.ResourceKey, page.PageIndex);
        if (lease.JobId != page.JobId || lease.LeaseToken == Guid.Empty || lease.Attempt < 1 ||
            page.RequestHash.Length != 64 ||
            !page.RequestHash.All(char.IsAsciiHexDigit) ||
            System.Text.Encoding.UTF8.GetByteCount(page.SafePayloadJson) > MaxPageBytes ||
            (page.NextContinuation?.Length ?? 0) > 4096)
            throw new DiscoveryCheckpointException("InvalidCheckpointPage");
        var now = clock.GetUtcNow();
        // The checkpoint, its audit event, and every bounded batch of observed
        // Git refs commit together. A crash cannot leave a page without its
        // provisional asset observations, or vice versa.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // A single SQL statement couples immutable insertion to a current,
        // unexpired job lease and emits an audit event only for a new page.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH owner AS (
                SELECT id, attempts FROM migration_jobs
                WHERE id = {lease.JobId} AND kind = {MigrationJobKinds.DiscoverSource}
                  AND state = 'Running' AND attempts = {lease.Attempt}
                  AND lease_token = {lease.LeaseToken} AND lease_until_utc > {now}
                FOR UPDATE
            ), inserted AS (
                INSERT INTO discovery_page_checkpoints (
                    job_id, resource_key, page_index, request_hash,
                    safe_payload_json, next_continuation, saved_at_utc)
                SELECT id, {page.ResourceKey}, {page.PageIndex},
                    {page.RequestHash}, {page.SafePayloadJson}::jsonb,
                    {page.NextContinuation}, {now}
                FROM owner
                ON CONFLICT (job_id, resource_key, page_index) DO NOTHING
                RETURNING job_id
            )
            INSERT INTO migration_job_events
                (job_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT job_id, 'PageSaved', {lease.Attempt}, {now}, NULL FROM inserted
            """, ct);

        var owns = await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
            x.Id == lease.JobId && x.Kind == MigrationJobKinds.DiscoverSource &&
            x.State == MigrationJobStates.Running &&
            x.Attempts == lease.Attempt && x.LeaseToken == lease.LeaseToken &&
            x.LeaseUntilUtc > now, ct);
        if (!owns) throw new DiscoveryCheckpointException("DiscoveryLeaseLost");

        var stored = await GetAsync(page.JobId, page.ResourceKey, page.PageIndex, ct)
            ?? throw new DiscoveryCheckpointException("CheckpointNotSaved");
        // jsonb canonicalizes key order and whitespace. Semantic equality,
        // rather than byte-for-byte string comparison, is required for replay.
        if (!string.Equals(stored.RequestHash, page.RequestHash, StringComparison.Ordinal) ||
            !JsonNode.DeepEquals(JsonNode.Parse(stored.SafePayloadJson),
                JsonNode.Parse(page.SafePayloadJson)))
            throw new DiscoveryCheckpointException("CheckpointMismatch");

        if (stored.ResourceKey.StartsWith("refs:", StringComparison.Ordinal))
            await IngestRefPageAsync(stored, ct);

        // Fencing must hold at commit time as well, even when a very large
        // source page took longer than expected to normalize and persist.
        var commitTime = clock.GetUtcNow();
        var stillOwned = await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
            x.Id == lease.JobId && x.Kind == MigrationJobKinds.DiscoverSource &&
            x.State == MigrationJobStates.Running &&
            x.Attempts == lease.Attempt && x.LeaseToken == lease.LeaseToken &&
            x.LeaseUntilUtc > commitTime, ct);
        if (!stillOwned)
            throw new DiscoveryCheckpointException("DiscoveryLeaseLost");

        await transaction.CommitAsync(ct);
        return stored;
    }

    private async Task IngestRefPageAsync(DiscoveryPageCheckpoint page, CancellationToken ct)
    {
        var repositoryId = page.ResourceKey["refs:".Length..];
        if (string.IsNullOrWhiteSpace(repositoryId) || repositoryId.Length > 256)
            throw new DiscoveryCheckpointException("InvalidRefObservation");

        using var doc = JsonDocument.Parse(page.SafePayloadJson);
        if (!doc.RootElement.TryGetProperty("value", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1_000)
            throw new DiscoveryCheckpointException("InvalidRefObservation");

        // Keep at most one provider page (1,000 refs) in temporary memory.
        var assets = new List<StagingAssetInput>(rows.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in rows.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("objectId", out var objectId) ||
                objectId.ValueKind != JsonValueKind.String)
                throw new DiscoveryCheckpointException("InvalidRefObservation");
            StagingAssetInput asset;
            try
            {
                asset = StagingAssetNormalizer.NormalizeRef(
                    repositoryId, name.GetString()!, objectId.GetString()!);
            }
            catch (StagingSyncInputException)
            {
                throw new DiscoveryCheckpointException("InvalidRefObservation");
            }
            if (!seen.Add(asset.SourceId))
                throw new DiscoveryCheckpointException("DuplicateRefObservation");
            assets.Add(asset);
        }

        // Bound PostgreSQL input and parameters: 250 rows per SQL command.
        // The page checkpoint row is protected by the same transaction.
        const int batchSize = 250;
        for (var i = 0; i < assets.Count; i += batchSize)
        {
            var part = assets.Skip(i).Take(batchSize).ToArray();
            var payload = JsonSerializer.Serialize(part.Select(asset => new
            {
                source_id = asset.SourceId,
                fingerprint = asset.Fingerprint,
                payload_json = asset.PayloadJson
            }));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH incoming AS (
                    SELECT * FROM jsonb_to_recordset({payload}::jsonb)
                    AS items(source_id text, fingerprint text, payload_json text)
                )
                INSERT INTO discovery_ref_observations (
                    job_id, source_id, repository_id, fingerprint, payload_json,
                    resource_key, page_index)
                SELECT {page.JobId}, source_id, {repositoryId}, fingerprint,
                    payload_json::jsonb, {page.ResourceKey}, {page.PageIndex}
                FROM incoming
                ON CONFLICT (job_id, source_id) DO NOTHING
                """, ct);

            // The same ref may appear twice on unstable provider pagination:
            // identical values are safe to deduplicate; different OIDs fail
            // rather than silently accepting a contradictory source snapshot.
            var ids = part.Select(x => x.SourceId).ToArray();
            var persisted = await db.DiscoveryRefObservations.AsNoTracking()
                .Where(x => x.JobId == page.JobId && ids.Contains(x.SourceId))
                .Select(x => new { x.SourceId, x.Fingerprint, x.RepositoryId })
                .ToArrayAsync(ct);
            if (persisted.Length != part.Length ||
                persisted.Any(row => row.RepositoryId != repositoryId ||
                    !part.Any(item => item.SourceId == row.SourceId &&
                        item.Fingerprint == row.Fingerprint)))
                throw new DiscoveryCheckpointException("RefObservationConflict");
        }
    }

    public async Task<DiscoveryRefIngestProgress> GetAsync(Guid jobId, CancellationToken ct)
    {
        if (jobId == Guid.Empty)
            throw new DiscoveryCheckpointException("InvalidCheckpointKey");
        var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .CountAsync(x => x.JobId == jobId && x.ResourceKey.StartsWith("refs:"), ct);
        var observed = await db.DiscoveryRefObservations.AsNoTracking()
            .CountAsync(x => x.JobId == jobId, ct);
        return new(jobId, pages, observed);
    }

    private static void ValidateKey(Guid jobId, string resourceKey, int pageIndex)
    {
        if (jobId == Guid.Empty || resourceKey.Length is < 1 or > MaxResourceLength ||
            pageIndex is < 0 or >= 30 || resourceKey.Any(char.IsControl))
            throw new DiscoveryCheckpointException("InvalidCheckpointKey");
    }

    private static DiscoveryPageCheckpoint ToCheckpoint(DiscoveryPageCheckpointRecord row) =>
        new(row.JobId, row.ResourceKey, row.PageIndex, row.RequestHash,
            row.SafePayloadJson, row.NextContinuation, row.SavedAtUtc);
}
