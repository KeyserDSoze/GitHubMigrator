using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// The entire F-026 workflow is PostgreSQL-only. It NEVER resolves a GitHub
// credential, HttpClient, destination writer, or deployment adapter.
// Production uses PostgreSQL as the single authority for lease decisions
// and append-only events. The explicit false override is reserved for
// deterministic legacy test clocks; DI always uses the database default.
public sealed class PostgresIncrementalPreparationRepository(
    AppDbContext db, TimeProvider clock, bool useDatabaseClock = true)
    : IIncrementalPreparationRepository
{
    private Task<DateTimeOffset> UtcNowAsync(CancellationToken ct) =>
        useDatabaseClock
            ? db.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct)
            : Task.FromResult(clock.GetUtcNow());

    public async Task<IncrementalPreparationView> EnqueueAsync(
        Guid snapshotId, Guid sourceConnectionId, Guid destinationConnectionId,
        Guid actorId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || sourceConnectionId == Guid.Empty ||
            destinationConnectionId == Guid.Empty || actorId == Guid.Empty ||
            sourceConnectionId == destinationConnectionId)
            throw new IncrementalPreparationException("InvalidPreparationContext");

        var sync = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == snapshotId, ct);
        if (sync is null || sync.SourceConnectionId != sourceConnectionId ||
            sync.JournalVersion != 1)
            throw new IncrementalPreparationException("IncrementalJournalUnavailable");
        var recent = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == sourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        if (recent != snapshotId)
            throw new IncrementalPreparationException("IncrementalSnapshotSuperseded");

        var id = Guid.NewGuid();
        var now = await UtcNowAsync(ct);
        // Concurrent administrators and API replicas converge on exactly one
        // (snapshot, destination) preparation, including an audit event.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH enqueued AS (
                INSERT INTO incremental_preparations (
                    id, snapshot_id, source_connection_id, destination_connection_id,
                    enqueued_by, state, attempts, max_attempts,
                    available_at_utc, lease_token, lease_until_utc,
                    created_at_utc, updated_at_utc, prepared_at_utc, last_error_code,
                    item_count, sensitive_item_count, evidence_sha256)
                VALUES ({id}, {snapshotId}, {sourceConnectionId}, {destinationConnectionId},
                    {actorId}, 'Queued', 0, 5, {now}, NULL, NULL,
                    {now}, {now}, NULL, NULL, 0, 0, NULL)
                ON CONFLICT (snapshot_id, destination_connection_id) DO NOTHING
                RETURNING id
            )
            INSERT INTO incremental_preparation_events (
                preparation_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, 'Queued', 0, {now}, NULL FROM enqueued
            """, ct);

        var record = await db.IncrementalPreparations.AsNoTracking()
            .SingleAsync(x => x.SnapshotId == snapshotId &&
                x.DestinationConnectionId == destinationConnectionId, ct);
        if (record.SourceConnectionId != sourceConnectionId)
            throw new IncrementalPreparationException("IncrementalSourceMismatch");
        return View(record);
    }

    public async Task<IncrementalPreparationView?> GetAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) return null;
        var record = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        return record is null ? null : View(record);
    }

    public async Task<IncrementalPreparationItemPage?> ListItemsAsync(
        Guid id, string? afterType, string? afterId, int take, CancellationToken ct)
    {
        if (id == Guid.Empty || take is < 1 or > 100 ||
            ((afterType is null) != (afterId is null)) ||
            (afterId?.Length ?? 0) > 1024 ||
            (afterId?.Any(char.IsControl) ?? false))
            throw new IncrementalPreparationException("InvalidPreparationPage");
        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null) return null;
        if (run.State != IncrementalPreparationStates.Prepared)
            throw new IncrementalPreparationException("PreparationNotReady");
        var rows = await db.IncrementalPreparationItems.FromSqlInterpolated($"""
            SELECT * FROM incremental_preparation_items
            WHERE preparation_id = {id}
              AND (asset_type, source_id) > ({afterType ?? ""}, {afterId ?? ""})
            ORDER BY asset_type, source_id
            LIMIT {take + 1}
            """).AsNoTracking().ToArrayAsync(ct);
        var items = rows.Take(take).Select(x => new IncrementalPreparationItem(
            x.AssetType, x.SourceId, x.ChangeKind, x.PreviousFingerprint,
            x.CurrentFingerprint, x.Revision, x.ReviewCategory, x.ReviewState)).ToArray();
        return new(id, items, rows.Length > take ? items[^1].AssetType : null,
            rows.Length > take ? items[^1].SourceId : null);
    }

    public async Task<IReadOnlyList<IncrementalPreparationEvent>> EventsAsync(
        Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) return [];
        var rows = await db.IncrementalPreparationEvents.AsNoTracking()
            .Where(x => x.PreparationId == id)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct);
        return rows.OrderBy(x => x.Id).Select(x => new IncrementalPreparationEvent(
            x.Id, x.PreparationId, x.EventType, x.Attempt, x.OccurredAtUtc,
            x.ErrorCode)).ToArray();
    }

    public async Task<IncrementalPreparationLease?> ClaimAsync(
        Guid token, TimeSpan duration, CancellationToken ct,
        Guid sourceConnectionId = default)
    {
        if (token == Guid.Empty || duration <= TimeSpan.Zero ||
            duration > TimeSpan.FromMinutes(5))
            throw new IncrementalPreparationException("InvalidPreparationLease");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Observe a fresh PostgreSQL instant *inside* the transaction,
        // not before its start. Do not use now() (transaction-start time).
        var now = await UtcNowAsync(ct);
        var until = now.Add(duration);
        // Recovery of a crashed fifth attempt must not leave a stuck Running
        // record forever, and the terminal failure is audited exactly once.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH exhausted AS (
                UPDATE incremental_preparations SET
                    state = 'Failed', last_error_code = 'LeaseExpiredAfterMaxAttempts',
                    updated_at_utc = {now}, lease_token = NULL, lease_until_utc = NULL
                WHERE state = 'Running' AND lease_until_utc <= {now}
                  AND attempts >= max_attempts
                  AND ({sourceConnectionId} = {Guid.Empty}
                      OR source_connection_id = {sourceConnectionId})
                RETURNING id, attempts
            )
            INSERT INTO incremental_preparation_events (
                preparation_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, 'Failed', attempts, {now}, 'LeaseExpiredAfterMaxAttempts'
            FROM exhausted
            """, ct);

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations
            SET state = 'Running', lease_token = {token},
                lease_until_utc = {until}, attempts = attempts + 1,
                updated_at_utc = {now}
            WHERE id = (
                SELECT id FROM incremental_preparations
                WHERE (
                    (state = 'Queued' AND available_at_utc <= {now})
                    OR (state = 'Running' AND lease_until_utc <= {now})
                )
                AND attempts < max_attempts
                AND ({sourceConnectionId} = {Guid.Empty}
                    OR source_connection_id = {sourceConnectionId})
                ORDER BY available_at_utc, created_at_utc, id
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            """, ct);
        if (affected == 0)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var run = await db.IncrementalPreparations.AsNoTracking()
            .SingleAsync(x => x.LeaseToken == token, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO incremental_preparation_events (
                preparation_id, event_type, attempt, occurred_at_utc, error_code)
            VALUES ({run.Id}, 'Claimed', {run.Attempts}, {now}, NULL)
            """, ct);
        await transaction.CommitAsync(ct);
        return new(run.Id, run.SnapshotId, run.SourceConnectionId,
            run.DestinationConnectionId, token, run.Attempts);
    }

    public async Task<bool> PrepareAsync(IncrementalPreparationLease lease, CancellationToken ct)
    {
        if (lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.Attempt < 1 || lease.SnapshotId == Guid.Empty ||
            lease.SourceConnectionId == Guid.Empty || lease.DestinationConnectionId == Guid.Empty)
            throw new IncrementalPreparationException("InvalidPreparationLease");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Same advisory lock and key as F-013 staging. It prevents a new
        // snapshot from replacing the current source catalog mid-preparation.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lease.SourceConnectionId.ToString()}), 732911)", ct);
        var began = await UtcNowAsync(ct);
        var locked = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_preparations SET updated_at_utc = {began}
            WHERE id = {lease.Id} AND snapshot_id = {lease.SnapshotId}
              AND source_connection_id = {lease.SourceConnectionId}
              AND destination_connection_id = {lease.DestinationConnectionId}
              AND state = 'Running' AND lease_token = {lease.LeaseToken}
              AND attempts = {lease.Attempt} AND lease_until_utc > {began}
            """, ct);
        if (locked != 1)
            throw new IncrementalPreparationException("IncrementalLeaseLost");

        var sync = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SnapshotId == lease.SnapshotId, ct);
        if (sync is null || sync.SourceConnectionId != lease.SourceConnectionId)
            throw new IncrementalPreparationException("IncrementalSourceMismatch");
        if (sync.JournalVersion != 1)
            throw new IncrementalPreparationException("IncrementalJournalUnavailable");
        var latest = await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SourceConnectionId == lease.SourceConnectionId)
            .OrderByDescending(x => x.SourceCollectedAtUtc)
            .Select(x => x.SnapshotId).FirstOrDefaultAsync(ct);
        if (latest != lease.SnapshotId)
            throw new IncrementalPreparationException("IncrementalSnapshotSuperseded");

        // F-025 aggregates are checked BEFORE ANY output writes. Corrupted
        // journal counts fail closed even when all rows individually look valid.
        var summary = await new PostgresStagingRepository(db, clock)
            .GetChangeSummaryAsync(lease.SnapshotId, ct)
            ?? throw new IncrementalPreparationException("IncrementalJournalUnavailable");
        if (summary.SourceConnectionId != lease.SourceConnectionId ||
            summary.Total > 200_000)
            throw new IncrementalPreparationException("IncrementalJournalMismatch");

        // One set-based, source-snapshot-fenced INSERT. All rows initially
        // require review, including refs and project metadata; ACL/group/user
        // changes and removals are specially flagged for manual examination.
        // There is no "Approved", "ReadyToWrite" or target command state.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO incremental_preparation_items (
                preparation_id, asset_type, source_id, change_kind,
                previous_fingerprint, current_fingerprint, revision,
                review_category, review_state)
            SELECT {lease.Id}, asset_type, source_id, change_kind,
                previous_fingerprint, current_fingerprint, revision,
                CASE WHEN change_kind = 'Missing' THEN 'Removal'
                     WHEN asset_type IN ('acl', 'identity', 'group', 'membership')
                         THEN 'Security'
                     ELSE 'SourceChange' END, 'NeedsReview'
            FROM staging_asset_changes
            WHERE snapshot_id = {lease.SnapshotId}
              AND source_connection_id = {lease.SourceConnectionId}
            """, ct);

        var actual = await db.IncrementalPreparationItems.AsNoTracking()
            .CountAsync(x => x.PreparationId == lease.Id, ct);
        if (actual != summary.Total)
            throw new IncrementalPreparationException("IncrementalJournalMismatch");

        // Stable SHA-256 across a keyset-ordered stream, regardless of source
        // size. The worker never allocates all change rows in .NET memory.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var reviewed = 0;
        var sensitive = 0;
        var rows = db.IncrementalPreparationItems.AsNoTracking()
            .Where(x => x.PreparationId == lease.Id)
            .OrderBy(x => x.AssetType).ThenBy(x => x.SourceId);
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (row.ReviewState != "NeedsReview" ||
                row.ChangeKind is not ("Added" or "Changed" or "Restored" or "Missing"))
                throw new IncrementalPreparationException("IncrementalJournalMismatch");
            var preimage = JsonSerializer.Serialize(new object?[] {
                row.AssetType, row.SourceId, row.ChangeKind, row.PreviousFingerprint,
                row.CurrentFingerprint, row.Revision, row.ReviewCategory, row.ReviewState
            }) + "\n";
            digest.AppendData(Encoding.UTF8.GetBytes(preimage));
            reviewed++;
            if (row.ReviewCategory != "SourceChange") sensitive++;
        }
        if (reviewed != actual)
            throw new IncrementalPreparationException("IncrementalJournalMismatch");
        var root = Convert.ToHexStringLower(digest.GetHashAndReset());
        var now = await UtcNowAsync(ct);
        var finished = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH completed AS (
                UPDATE incremental_preparations SET
                    state = 'Prepared', prepared_at_utc = {now},
                    updated_at_utc = {now}, lease_token = NULL,
                    lease_until_utc = NULL, last_error_code = NULL,
                    item_count = {reviewed}, sensitive_item_count = {sensitive},
                    evidence_sha256 = {root}
                WHERE id = {lease.Id} AND snapshot_id = {lease.SnapshotId}
                  AND source_connection_id = {lease.SourceConnectionId}
                  AND destination_connection_id = {lease.DestinationConnectionId}
                  AND state = 'Running' AND lease_token = {lease.LeaseToken}
                  AND attempts = {lease.Attempt} AND lease_until_utc > {now}
                RETURNING id, attempts
            )
            INSERT INTO incremental_preparation_events (
                preparation_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, 'Prepared', attempts, {now}, NULL FROM completed
            """, ct);
        if (finished != 1)
            throw new IncrementalPreparationException("IncrementalLeaseLost");
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> FailAsync(
        IncrementalPreparationLease lease, string errorCode, bool permanent, CancellationToken ct)
    {
        if (lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty || lease.Attempt < 1)
            throw new IncrementalPreparationException("InvalidPreparationLease");
        if (errorCode.Length is < 1 or > 70 ||
            errorCode.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new IncrementalPreparationException("UnsafePreparationErrorCode");
        // Another replica might be completing or renewing this row.
        // Wait for its row lock BEFORE sampling the authoritative time.
        // Otherwise an expiring lease could be accepted using stale now.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM incremental_preparations WHERE id = {lease.Id} FOR UPDATE",
            ct);
        var now = await UtcNowAsync(ct);
        var seconds = Math.Min(300, 5 * (1 << Math.Min(lease.Attempt, 6)));
        var retryAt = now.AddSeconds(seconds);
        var finished = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH failed AS (
                UPDATE incremental_preparations SET
                    state = CASE WHEN {permanent} OR attempts >= max_attempts
                        THEN 'Failed' ELSE 'Queued' END,
                    available_at_utc = CASE WHEN {permanent} OR attempts >= max_attempts
                        THEN available_at_utc ELSE {retryAt} END,
                    last_error_code = {errorCode}, updated_at_utc = {now},
                    lease_token = NULL, lease_until_utc = NULL
                WHERE id = {lease.Id} AND snapshot_id = {lease.SnapshotId}
                  AND source_connection_id = {lease.SourceConnectionId}
                  AND destination_connection_id = {lease.DestinationConnectionId}
                  AND state = 'Running' AND lease_token = {lease.LeaseToken}
                  AND attempts = {lease.Attempt} AND lease_until_utc > {now}
                RETURNING id, attempts, state
            )
            INSERT INTO incremental_preparation_events (
                preparation_id, event_type, attempt, occurred_at_utc, error_code)
            SELECT id, CASE WHEN state = 'Queued' THEN 'RetryScheduled' ELSE 'Failed' END,
                attempts, {now}, {errorCode} FROM failed
            """, ct);
        await transaction.CommitAsync(ct);
        return finished == 1;
    }

    private static IncrementalPreparationView View(IncrementalPreparationRecord row) =>
        new(row.Id, row.SnapshotId, row.SourceConnectionId, row.DestinationConnectionId,
            row.EnqueuedBy, row.State, row.Attempts, row.MaxAttempts,
            row.AvailableAtUtc, row.LeaseUntilUtc, row.CreatedAtUtc, row.UpdatedAtUtc,
            row.PreparedAtUtc, row.LastErrorCode, row.ItemCount, row.SensitiveItemCount,
            row.EvidenceSha256);
}
