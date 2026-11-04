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
    : IDiscoveryPageCheckpointStore
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
        if (!string.Equals(stored.RequestHash, page.RequestHash, StringComparison.Ordinal) ||
            !string.Equals(stored.SafePayloadJson, page.SafePayloadJson, StringComparison.Ordinal))
            throw new DiscoveryCheckpointException("CheckpointMismatch");
        return stored;
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
