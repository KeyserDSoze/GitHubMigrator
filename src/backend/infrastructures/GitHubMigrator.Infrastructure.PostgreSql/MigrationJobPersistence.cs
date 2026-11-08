using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class MigrationJobRecord
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid SnapshotId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public Guid EnqueuedBy { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string State { get; set; } = MigrationJobStates.Queued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class MigrationJobEventRecord
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public string EventType { get; set; } = "";
    public int Attempt { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class PostgresMigrationJobRepository(AppDbContext db, TimeProvider clock)
    : IMigrationJobRepository
{
    private static DateTimeOffset UtcNow(TimeProvider clock) => clock.GetUtcNow();

    public async Task<MigrationJobView> EnqueueStagingAsync(
        Guid snapshotId, Guid sourceConnectionId, Guid actorId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || sourceConnectionId == Guid.Empty || actorId == Guid.Empty)
            throw new MigrationJobInputException("InvalidJobContext");
        var id = Guid.NewGuid();
        var now = UtcNow(clock);
        var key = MigrationJobKinds.StageSnapshot + ":" + snapshotId.ToString("N");
        // Insert + event share one SQL statement. Concurrent API instances converge
        // on the same unique idempotency key; no duplicate jobs or duplicate Queued events.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH inserted AS (
                INSERT INTO migration_jobs (
                    id, kind, snapshot_id, source_connection_id, enqueued_by, idempotency_key,
                    state, attempts, max_attempts, available_at_utc, lease_token, lease_until_utc,
                    created_at_utc, updated_at_utc, completed_at_utc, last_error_code, result_json)
                VALUES ({id}, {MigrationJobKinds.StageSnapshot}, {snapshotId}, {sourceConnectionId},
                    {actorId}, {key}, 'Queued', 0, 5, {now}, NULL, NULL,
                    {now}, {now}, NULL, NULL, NULL)
                ON CONFLICT (idempotency_key) DO NOTHING
                RETURNING id
            )
            INSERT INTO migration_job_events (job_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, 'Queued', 0, {now}, NULL FROM inserted
            """, ct);
        var row = await db.MigrationJobs.AsNoTracking().SingleAsync(x => x.IdempotencyKey == key, ct);
        if (row.SnapshotId != snapshotId || row.SourceConnectionId != sourceConnectionId)
            throw new MigrationJobInputException("JobIdempotencyMismatch");
        return View(row);
    }

    public async Task<MigrationJobView> EnqueueDiscoveryAsync(
        Guid sourceConnectionId, Guid actorId, Guid requestId, CancellationToken ct)
    {
        if (sourceConnectionId == Guid.Empty || actorId == Guid.Empty || requestId == Guid.Empty)
            throw new MigrationJobInputException("InvalidJobContext");
        var id = Guid.NewGuid();
        var now = UtcNow(clock);
        var key = MigrationJobKinds.DiscoverSource + ":" +
            sourceConnectionId.ToString("N") + ":" + requestId.ToString("N");
        // Two independent uniqueness guarantees:
        // (1) identical request IDs are idempotent, even after completion;
        // (2) only one *active* Azure DevOps discovery can exist per connection.
        // Atomic INSERT ... ON CONFLICT DO NOTHING works for both indexes.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH inserted AS (
                INSERT INTO migration_jobs (
                    id, kind, snapshot_id, source_connection_id, enqueued_by, idempotency_key,
                    state, attempts, max_attempts, available_at_utc, lease_token, lease_until_utc,
                    created_at_utc, updated_at_utc, completed_at_utc, last_error_code, result_json)
                VALUES ({id}, {MigrationJobKinds.DiscoverSource}, {Guid.Empty},
                    {sourceConnectionId}, {actorId}, {key},
                    'Queued', 0, 5, {now}, NULL, NULL, {now}, {now}, NULL, NULL, NULL)
                ON CONFLICT DO NOTHING
                RETURNING id
            )
            INSERT INTO migration_job_events (job_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, 'Queued', 0, {now}, NULL FROM inserted
            """, ct);

        var row = await db.MigrationJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        row ??= await db.MigrationJobs.AsNoTracking()
            .Where(x => x.SourceConnectionId == sourceConnectionId &&
                x.Kind == MigrationJobKinds.DiscoverSource &&
                (x.State == MigrationJobStates.Queued || x.State == MigrationJobStates.Running))
            .SingleOrDefaultAsync(ct);
        if (row is null)
            throw new MigrationJobInputException("DiscoveryEnqueueConflictRetry");
        if (row.SourceConnectionId != sourceConnectionId || row.Kind != MigrationJobKinds.DiscoverSource)
            throw new MigrationJobInputException("JobIdempotencyMismatch");
        return View(row);
    }

    public async Task<bool> CheckpointSnapshotAsync(
        MigrationJobLease lease, Guid snapshotId, CancellationToken ct)
    {
        if (lease.Kind != MigrationJobKinds.DiscoverSource || snapshotId != lease.Id ||
            lease.LeaseToken == Guid.Empty || lease.Attempt < 1)
            throw new MigrationJobInputException("InvalidDiscoveryCheckpoint");
        var now = UtcNow(clock);
        // A snapshot is committed in migration_discoveries before this checkpoint.
        // The event and new snapshot ID are atomic. A reclaimed worker cannot
        // mutate a checkpoint belonging to a newer attempt.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH saved AS (
                UPDATE migration_jobs SET snapshot_id = {snapshotId},
                    updated_at_utc = {now}
                WHERE id = {lease.Id} AND source_connection_id = {lease.SourceConnectionId}
                  AND kind = {MigrationJobKinds.DiscoverSource}
                  AND snapshot_id = {Guid.Empty} AND state = 'Running'
                  AND attempts = {lease.Attempt} AND lease_token = {lease.LeaseToken}
                  AND lease_until_utc > {now}
                RETURNING id, attempts
            )
            INSERT INTO migration_job_events (job_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, 'SnapshotSaved', attempts, {now}, NULL FROM saved
            """, ct);
        return inserted == 1;
    }

    public async Task<MigrationJobView?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var row = await db.MigrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId, ct);
        return row is null ? null : View(row);
    }

    public async Task<IReadOnlyList<MigrationJobView>> ListAsync(Guid sourceConnectionId, CancellationToken ct)
    {
        var rows = await db.MigrationJobs.AsNoTracking()
            .Where(x => x.SourceConnectionId == sourceConnectionId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(25).ToArrayAsync(ct);
        return rows.Select(View).ToArray();
    }

    public async Task<IReadOnlyList<MigrationJobEventView>> EventsAsync(Guid jobId, CancellationToken ct)
    {
        var rows = await db.MigrationJobEvents.AsNoTracking()
            .Where(x => x.JobId == jobId).OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct);
        return rows.OrderBy(x => x.Id)
            .Select(x => new MigrationJobEventView(x.Id, x.JobId, x.EventType,
                x.Attempt, x.OccurredAtUtc, x.ErrorCode)).ToArray();
    }

    public Task<MigrationJobLease?> ClaimAsync(
        Guid leaseToken, TimeSpan leaseDuration, CancellationToken ct) =>
        ClaimCoreAsync(leaseToken, leaseDuration, null, ct);

    // Used by source-bound callers that must not steal an unrelated job
    // from another replica or integration fixture. The ordinary worker
    // remains global and uses the same transactional claim algorithm.
    public Task<MigrationJobLease?> ClaimForSourceAsync(
        Guid sourceConnectionId, Guid leaseToken, TimeSpan leaseDuration,
        CancellationToken ct)
    {
        if (sourceConnectionId == Guid.Empty)
            throw new MigrationJobInputException("InvalidJobContext");
        return ClaimCoreAsync(leaseToken, leaseDuration, sourceConnectionId, ct);
    }

    private async Task<MigrationJobLease?> ClaimCoreAsync(
        Guid leaseToken, TimeSpan leaseDuration, Guid? sourceConnectionId,
        CancellationToken ct)
    {
        if (leaseToken == Guid.Empty || leaseDuration <= TimeSpan.Zero ||
            leaseDuration > TimeSpan.FromMinutes(5))
            throw new MigrationJobInputException("InvalidWorkerLease");
        var now = UtcNow(clock);
        var until = now.Add(leaseDuration);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // A worker that died on its last allowed attempt must terminate rather
        // than leaving an immortal Running row after its lease expires.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH exhausted AS (
                UPDATE migration_jobs SET
                    state = 'Failed', completed_at_utc = {now}, updated_at_utc = {now},
                    lease_token = NULL, lease_until_utc = NULL,
                    last_error_code = 'LeaseExpiredAfterMaxAttempts'
                WHERE state = 'Running' AND lease_until_utc <= {now}
                  AND attempts >= max_attempts
                  AND ({sourceConnectionId}::uuid IS NULL OR
                       source_connection_id = {sourceConnectionId})
                RETURNING id, attempts
            )
            INSERT INTO migration_job_events (job_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, 'Failed', attempts, {now}, 'LeaseExpiredAfterMaxAttempts' FROM exhausted
            """, ct);

        // PostgreSQL row locking with SKIP LOCKED is safe across API replicas.
        // A unique lease token fences old worker completions after reclaims.
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE migration_jobs
            SET state = 'Running', lease_token = {leaseToken},
                lease_until_utc = {until}, attempts = attempts + 1,
                updated_at_utc = {now}
            WHERE id = (
                SELECT id FROM migration_jobs
                WHERE (
                    (state = 'Queued' AND available_at_utc <= {now})
                    OR (state = 'Running' AND lease_until_utc <= {now})
                )
                AND attempts < max_attempts
                AND ({sourceConnectionId}::uuid IS NULL OR
                     source_connection_id = {sourceConnectionId})
                ORDER BY available_at_utc, created_at_utc, id
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            """, ct);
        if (claimed == 0)
        {
            await transaction.CommitAsync(ct);
            return null;
        }
        var row = await db.MigrationJobs.AsNoTracking()
            .SingleAsync(x => x.LeaseToken == leaseToken, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO migration_job_events (job_id, event_type, attempt, occurred_at_utc, error_code)
            VALUES ({row.Id}, 'Claimed', {row.Attempts}, {now}, NULL)
            """, ct);
        await transaction.CommitAsync(ct);
        return new(row.Id, row.SnapshotId, row.SourceConnectionId,
            row.Kind, leaseToken, row.Attempts);
    }

    public Task<bool> CompleteAsync(MigrationJobLease lease, StagingSyncResult result, CancellationToken ct)
    {
        if (result.SourceConnectionId != lease.SourceConnectionId ||
            (lease.Kind == MigrationJobKinds.StageSnapshot && result.SnapshotId != lease.SnapshotId) ||
            (lease.Kind == MigrationJobKinds.DiscoverSource && result.SnapshotId != lease.Id))
            throw new MigrationJobInputException("JobResultSourceMismatch");
        return FinishAsync(lease, null, false, result, ct);
    }

    public Task<bool> FailAsync(MigrationJobLease lease, string errorCode, bool permanent, CancellationToken ct)
    {
        // Never persist dynamic exception strings, provider responses or credentials.
        if (errorCode.Length is < 1 or > 70 || errorCode.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new MigrationJobInputException("UnsafeJobErrorCode");
        return FinishAsync(lease, errorCode, permanent, null, ct);
    }

    private async Task<bool> FinishAsync(MigrationJobLease lease, string? errorCode,
        bool permanent, StagingSyncResult? result, CancellationToken ct)
    {
        var now = UtcNow(clock);
        if (lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty || lease.Attempt < 1)
            throw new MigrationJobInputException("InvalidWorkerLease");
        // Exponential retry backoff; no immediate spin on temporary PostgreSQL/provider errors.
        var seconds = Math.Min(300, 5 * (1 << Math.Min(lease.Attempt, 6)));
        var retryAt = now.AddSeconds(seconds);
        var success = result is not null;
        var json = result is null ? null : JsonSerializer.Serialize(result);
        var completedSnapshot = result?.SnapshotId ?? Guid.Empty;
        // Atomic compare-and-swap: an expired/stolen lease is never allowed to mark a
        // newer attempt complete. The event is inserted from UPDATE RETURNING.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH finished AS (
                UPDATE migration_jobs SET
                    snapshot_id = CASE WHEN {success} AND kind = {MigrationJobKinds.DiscoverSource}
                        THEN {completedSnapshot} ELSE snapshot_id END,
                    state = CASE
                        WHEN {success} THEN 'Succeeded'
                        WHEN {permanent} OR attempts >= max_attempts THEN 'Failed'
                        ELSE 'Queued' END,
                    completed_at_utc = CASE
                        WHEN {success} OR {permanent} OR attempts >= max_attempts THEN {now}
                        ELSE NULL END,
                    available_at_utc = CASE
                        WHEN {success} OR {permanent} OR attempts >= max_attempts
                            THEN available_at_utc ELSE {retryAt} END,
                    result_json = CASE WHEN {success} THEN {json}::jsonb ELSE result_json END,
                    last_error_code = {errorCode},
                    lease_token = NULL, lease_until_utc = NULL,
                    updated_at_utc = {now}
                WHERE id = {lease.Id} AND lease_token = {lease.LeaseToken}
                    AND attempts = {lease.Attempt} AND state = 'Running'
                    AND lease_until_utc > {now}
                    AND ({!success} OR kind <> {MigrationJobKinds.DiscoverSource}
                        OR snapshot_id = {completedSnapshot})
                RETURNING id, state, attempts
            )
            INSERT INTO migration_job_events (job_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, CASE WHEN state = 'Succeeded' THEN 'Succeeded'
                        WHEN state = 'Failed' THEN 'Failed' ELSE 'RetryScheduled' END,
                   attempts, {now}, {errorCode} FROM finished
            """, ct);
        return updated == 1;
    }

    private static MigrationJobView View(MigrationJobRecord row) =>
        new(row.Id, row.Kind, row.SnapshotId, row.SourceConnectionId,
            row.EnqueuedBy, row.State, row.Attempts, row.MaxAttempts,
            row.AvailableAtUtc, row.LeaseUntilUtc, row.CreatedAtUtc,
            row.UpdatedAtUtc, row.CompletedAtUtc, row.LastErrorCode,
            row.ResultJson is null ? null : JsonSerializer.Deserialize<StagingSyncResult>(row.ResultJson));
}
