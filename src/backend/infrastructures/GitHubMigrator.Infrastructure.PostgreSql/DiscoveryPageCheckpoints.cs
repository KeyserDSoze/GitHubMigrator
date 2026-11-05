using System.Security.Cryptography;
using System.Text;
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
    : IDiscoveryPageCheckpointStore, IDiscoveryRefIngestProgress,
      IDiscoveryIdentityObservationStore, IDiscoveryGroupObservationStore,
      IDiscoveryMembershipEvidenceVerifier, IDiscoveryAclEvidenceStore
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
        else if (stored.ResourceKey == "graph-users")
            await IngestIdentityPageAsync(stored, ct);
        else if (stored.ResourceKey == "security-groups")
            await IngestGroupPageAsync(stored, ct);
        else if (stored.ResourceKey.StartsWith("security-members:", StringComparison.Ordinal))
            await IngestMembershipPageAsync(stored, ct);
        else if (stored.ResourceKey.StartsWith("security-acl:", StringComparison.Ordinal))
            await IngestAclPageAsync(stored, ct);

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
            // Preserve the reader's behavior: malformed/empty source refs
            // are ignored rather than turning an incomplete provider row into
            // a real asset. The final snapshot applies the same filtering.
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("objectId", out var objectId) ||
                objectId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()) ||
                string.IsNullOrWhiteSpace(objectId.GetString()))
                continue;
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

    // Graph user observations share the immutable page's transaction,
    // audit event and worker lease fencing. Only allowlisted fields enter
    // this table; provider JSON never passes through unfiltered.
    private async Task IngestGroupPageAsync(
        DiscoveryPageCheckpoint page, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(page.SafePayloadJson);
        if (!doc.RootElement.TryGetProperty("value", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 1_000)
            throw new DiscoveryCheckpointException("InvalidGroupObservation");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<StagingAssetInput>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new DiscoveryCheckpointException("InvalidGroupObservation");
            var descriptor = GetText(item, "descriptor");
            if (string.IsNullOrWhiteSpace(descriptor)) continue;
            StagingAssetInput asset;
            try
            {
                asset = StagingAssetNormalizer.NormalizeGroup(
                    descriptor, GetText(item, "displayName") ?? descriptor,
                    GetText(item, "legacyDescriptor"));
            }
            catch (StagingSyncInputException)
            {
                throw new DiscoveryCheckpointException("InvalidGroupObservation");
            }
            if (!seen.Add(asset.SourceId))
                throw new DiscoveryCheckpointException("DuplicateGroupObservation");
            normalized.Add(asset);
        }

        const int batchSize = 250;
        for (var i = 0; i < normalized.Count; i += batchSize)
        {
            var part = normalized.Skip(i).Take(batchSize).ToArray();
            var json = JsonSerializer.Serialize(part.Select(row => new {
                source_id = row.SourceId,
                fingerprint = row.Fingerprint,
                payload_json = row.PayloadJson
            }));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH incoming AS (
                    SELECT * FROM jsonb_to_recordset({json}::jsonb)
                        AS rows(source_id text, fingerprint text, payload_json text)
                )
                INSERT INTO discovery_group_observations (
                    job_id, source_id, fingerprint, payload_json, resource_key, page_index)
                SELECT {page.JobId}, source_id, fingerprint, payload_json::jsonb,
                    {page.ResourceKey}, {page.PageIndex}
                FROM incoming
                ON CONFLICT (job_id, source_id) DO NOTHING
                """, ct);

            var ids = part.Select(x => x.SourceId).ToArray();
            var persisted = await db.DiscoveryGroupObservations.AsNoTracking()
                .Where(x => x.JobId == page.JobId && ids.Contains(x.SourceId))
                .Select(x => new { x.SourceId, x.Fingerprint }).ToArrayAsync(ct);
            if (persisted.Length != part.Length ||
                persisted.Any(row => !part.Any(x =>
                    x.SourceId == row.SourceId && x.Fingerprint == row.Fingerprint)))
                throw new DiscoveryCheckpointException("GroupObservationConflict");
        }
    }

    async Task<DiscoveryGroupProgress> IDiscoveryGroupObservationStore.GetProgressAsync(
        Guid jobId, CancellationToken ct)
    {
        if (jobId == Guid.Empty)
            throw new DiscoveryCheckpointException("InvalidCheckpointKey");
        var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .CountAsync(x => x.JobId == jobId && x.ResourceKey == "security-groups", ct);
        var groups = await db.DiscoveryGroupObservations.AsNoTracking()
            .CountAsync(x => x.JobId == jobId, ct);
        return new(jobId, pages, groups);
    }

    async Task<DiscoveryGroupObservationPage> IDiscoveryGroupObservationStore.ListAsync(
        Guid jobId, string? after, int take, CancellationToken ct)
    {
        if (jobId == Guid.Empty || take is < 1 or > 100 ||
            (after?.Length ?? 0) > 1024 || (after?.Any(char.IsControl) ?? false))
            throw new DiscoveryCheckpointException("InvalidGroupObservationPage");
        var rows = db.DiscoveryGroupObservations.AsNoTracking()
            .Where(x => x.JobId == jobId);
        if (after is not null)
            rows = rows.Where(x => string.Compare(x.SourceId, after) > 0);
        var result = await rows.OrderBy(x => x.SourceId)
            .Take(take + 1).ToArrayAsync(ct);
        var items = result.Take(take).Select(x => new DiscoveryGroupObservation(
            x.SourceId, x.Fingerprint,
            JsonSerializer.Deserialize<JsonElement>(x.PayloadJson))).ToArray();
        return new(jobId, items,
            result.Length > take ? items[^1].SourceId : null);
    }

    async Task IDiscoveryGroupObservationStore.VerifySnapshotAsync(
        MigrationJobLease lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.Kind != MigrationJobKinds.DiscoverSource ||
            lease.SnapshotId != lease.Id ||
            !await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
                x.Id == lease.Id &&
                x.SourceConnectionId == lease.SourceConnectionId &&
                x.SnapshotId == lease.Id &&
                x.Kind == MigrationJobKinds.DiscoverSource &&
                x.State == MigrationJobStates.Running &&
                x.Attempts == lease.Attempt &&
                x.LeaseToken == lease.LeaseToken && x.LeaseUntilUtc > now, ct))
            throw new DiscoveryCheckpointException("DiscoveryLeaseLost");

        // Previously committed group checkpoints may predate normalized
        // observations. Replay them under the current fenced attempt.
        var cached = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .Where(x => x.JobId == lease.Id && x.ResourceKey == "security-groups")
            .OrderBy(x => x.PageIndex).ToArrayAsync(ct);
        foreach (var row in cached)
            await SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
                ToCheckpoint(row), ct);

        var snapshotExists = await db.Discoveries.AsNoTracking().AnyAsync(
            x => x.Id == lease.Id && x.ConnectionId == lease.SourceConnectionId, ct);
        if (!snapshotExists)
            throw new DiscoveryCheckpointException("GroupSnapshotMismatch");

        var shouldHavePage = await db.Database.SqlQuery<bool>($"""
            SELECT COALESCE(
                (inventory -> 'securityCoverage' ->> 'groupsComplete')::boolean,
                false) AS "Value"
            FROM migration_discoveries WHERE id = {lease.Id}
            """).SingleAsync(ct);
        if (shouldHavePage && cached.Length == 0)
            throw new DiscoveryCheckpointException("GroupSnapshotMismatch");

        // Both directions, including duplicate multisets, are checked:
        // equivalent totals are not enough to accept a replacement group.
        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH saved AS (
                SELECT g.descriptor,
                    g."displayName" AS display_name,
                    g."legacyDescriptor" AS legacy
                FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(d.inventory -> 'groups')
                    AS g(descriptor text, "displayName" text, "legacyDescriptor" text)
                WHERE d.id = {lease.Id} AND d.connection_id = {lease.SourceConnectionId}
            ),
            observed AS (
                SELECT source_id AS descriptor,
                    payload_json ->> 'DisplayName' AS display_name,
                    payload_json ->> 'LegacyDescriptor' AS legacy
                FROM discovery_group_observations WHERE job_id = {lease.Id}
            ),
            differences AS (
                (SELECT * FROM saved EXCEPT ALL SELECT * FROM observed)
                UNION ALL
                (SELECT * FROM observed EXCEPT ALL SELECT * FROM saved)
            )
            SELECT EXISTS(SELECT 1 FROM differences) AS "Value"
            """).SingleAsync(ct);
        if (mismatch)
            throw new DiscoveryCheckpointException("GroupSnapshotMismatch");
    }

    // Persist raw direct membership evidence atomically with its checkpoint.
    // Unrelated Graph containers and invalid edges are ignored exactly as the
    // source reader does; matching edges use the canonical staging-v1 hash.
    private async Task IngestMembershipPageAsync(
        DiscoveryPageCheckpoint page, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(page.SafePayloadJson);
        if (!doc.RootElement.TryGetProperty("value", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 20_000)
            throw new DiscoveryCheckpointException("InvalidMembershipObservation");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var assets = new List<(StagingAssetInput Asset, string Group)>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new DiscoveryCheckpointException("InvalidMembershipObservation");
            var group = GetText(entry, "containerDescriptor");
            var member = GetText(entry, "memberDescriptor");
            if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(member))
                continue;
            var key = "security-members:" +
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(group)))[..40];
            if (!string.Equals(key, page.ResourceKey, StringComparison.Ordinal))
                continue; // Only direct membership for this requested Graph group.
            StagingAssetInput asset;
            try { asset = StagingAssetNormalizer.NormalizeMembership(group, member); }
            catch (StagingSyncInputException)
            {
                throw new DiscoveryCheckpointException("InvalidMembershipObservation");
            }
            if (seen.Add(asset.SourceId))
                assets.Add((asset, group)); // provider duplicate edges are idempotent
        }

        const int batchSize = 250;
        for (var i = 0; i < assets.Count; i += batchSize)
        {
            var part = assets.Skip(i).Take(batchSize).ToArray();
            var json = JsonSerializer.Serialize(part.Select(x => new {
                source_id = x.Asset.SourceId,
                group_descriptor = x.Group,
                fingerprint = x.Asset.Fingerprint,
                payload_json = x.Asset.PayloadJson
            }));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH incoming AS (
                    SELECT * FROM jsonb_to_recordset({json}::jsonb)
                    AS items(source_id text, group_descriptor text,
                        fingerprint text, payload_json text)
                )
                INSERT INTO discovery_membership_observations (
                    job_id, source_id, group_descriptor, fingerprint, payload_json,
                    resource_key, page_index)
                SELECT {page.JobId}, source_id, group_descriptor, fingerprint,
                    payload_json::jsonb, {page.ResourceKey}, {page.PageIndex}
                FROM incoming
                ON CONFLICT (job_id, source_id) DO NOTHING
                """, ct);

            var ids = part.Select(x => x.Asset.SourceId).ToArray();
            var persisted = await db.DiscoveryMembershipObservations.AsNoTracking()
                .Where(x => x.JobId == page.JobId && ids.Contains(x.SourceId))
                .Select(x => new { x.SourceId, x.GroupDescriptor, x.Fingerprint })
                .ToArrayAsync(ct);
            if (persisted.Length != part.Length ||
                persisted.Any(row => !part.Any(x =>
                    row.SourceId == x.Asset.SourceId &&
                    row.GroupDescriptor == x.Group &&
                    row.Fingerprint == x.Asset.Fingerprint)))
                throw new DiscoveryCheckpointException("MembershipObservationConflict");
        }

        if (await db.DiscoveryMembershipObservations.AsNoTracking()
            .CountAsync(x => x.JobId == page.JobId, ct) > 20_000)
            throw new DiscoveryCheckpointException("MembershipObservationLimit");
    }

    async Task<DiscoveryMembershipProgress> IDiscoveryMembershipEvidenceVerifier.GetProgressAsync(
        Guid jobId, CancellationToken ct)
    {
        if (jobId == Guid.Empty)
            throw new DiscoveryCheckpointException("InvalidCheckpointKey");
        var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .CountAsync(x => x.JobId == jobId &&
                x.ResourceKey.StartsWith("security-members:"), ct);
        var edges = await db.DiscoveryMembershipObservations.AsNoTracking()
            .CountAsync(x => x.JobId == jobId, ct);
        return new(jobId, pages, edges);
    }

    async Task<DiscoveryMembershipObservationPage> IDiscoveryMembershipEvidenceVerifier.ListAsync(
        Guid jobId, string? after, int take, CancellationToken ct)
    {
        if (jobId == Guid.Empty || take is < 1 or > 100 ||
            (after?.Length ?? 0) > 1024 || (after?.Any(char.IsControl) ?? false))
            throw new DiscoveryCheckpointException("InvalidMembershipObservationPage");
        var query = db.DiscoveryMembershipObservations.AsNoTracking()
            .Where(x => x.JobId == jobId);
        if (after is not null)
            query = query.Where(x => string.Compare(x.SourceId, after) > 0);
        var rows = await query.OrderBy(x => x.SourceId)
            .Take(take + 1).ToArrayAsync(ct);
        var items = rows.Take(take).Select(x => {
            using var doc = JsonDocument.Parse(x.PayloadJson);
            return new DiscoveryMembershipObservation(
                x.SourceId, x.GroupDescriptor,
                doc.RootElement.GetProperty("MemberDescriptor").GetString()!,
                x.Fingerprint);
        }).ToArray();
        return new(jobId, items, rows.Length > take ? items[^1].SourceId : null);
    }

    // F-021: fail-closed source-vs-PostgreSQL reconciliation before staging.
    // Legacy inline snapshots still compare with their raw source checkpoints.
    public async Task VerifyMembershipsAsync(MigrationJobLease lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.Kind != MigrationJobKinds.DiscoverSource ||
            lease.SnapshotId != lease.Id ||
            !await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
                x.Id == lease.Id &&
                x.SourceConnectionId == lease.SourceConnectionId &&
                x.SnapshotId == lease.Id &&
                x.Kind == MigrationJobKinds.DiscoverSource &&
                x.State == MigrationJobStates.Running &&
                x.Attempts == lease.Attempt &&
                x.LeaseToken == lease.LeaseToken && x.LeaseUntilUtc > now, ct))
            throw new DiscoveryCheckpointException("DiscoveryLeaseLost");

        // Backfill checkpoints written before F-021 under the CURRENT lease:
        // no provider re-read and no duplicate PageSaved audit event.
        var cached = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .Where(x => x.JobId == lease.Id &&
                x.ResourceKey.StartsWith("security-members:"))
            .OrderBy(x => x.ResourceKey).ThenBy(x => x.PageIndex).ToArrayAsync(ct);
        foreach (var page in cached)
            await SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
                ToCheckpoint(page), ct);

        // Database-side comparison keeps source edge evidence out of the
        // worker's heap. Complete coverage additionally requires even empty
        // groups to have a contiguous, terminated page chain.
        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH source_snapshot AS (
                SELECT inventory FROM migration_discoveries
                WHERE id = {lease.Id} AND connection_id = {lease.SourceConnectionId}
            ),
            coverage AS (
                SELECT
                    COALESCE((inventory -> 'securityCoverage' ->> 'groupsComplete')::boolean, false)
                        AS groups_complete,
                    COALESCE((inventory -> 'securityCoverage' ->> 'membershipsComplete')::boolean, false)
                        AS memberships_complete,
                    COALESCE((inventory ->> 'membershipsExternalized')::boolean, false)
                        AS externalized,
                    COALESCE((inventory ->> 'membershipCount')::integer, -1)
                        AS expected_count,
                    COALESCE(jsonb_array_length(inventory -> 'memberships'), 0)
                        AS inline_count
                FROM source_snapshot
            ),
            expected_groups AS (
                SELECT g.descriptor,
                    'security-members:' ||
                        left(encode(sha256(convert_to(g.descriptor, 'UTF8')), 'hex'), 40)
                        AS resource_key
                FROM source_snapshot s
                CROSS JOIN LATERAL jsonb_to_recordset(s.inventory -> 'groups')
                    AS g(descriptor text)
                WHERE NULLIF(btrim(g.descriptor), '') IS NOT NULL
            ),
            pages AS (
                SELECT p.resource_key, p.page_index, p.next_continuation,
                    p.safe_payload_json
                FROM discovery_page_checkpoints p
                WHERE p.job_id = {lease.Id} AND p.resource_key LIKE 'security-members:%'
            ),
            page_sequence AS (
                SELECT g.resource_key, count(p.page_index) AS page_count,
                    min(p.page_index) AS first_page, max(p.page_index) AS last_page,
                    count(p.page_index) FILTER (WHERE p.next_continuation IS NULL)
                        AS terminal_count,
                    max(p.page_index) FILTER (WHERE p.next_continuation IS NULL)
                        AS terminal_page
                FROM expected_groups g
                LEFT JOIN pages p ON p.resource_key = g.resource_key
                GROUP BY g.resource_key
            ),
            saved AS (
                SELECT m."groupDescriptor" AS group_descriptor,
                    m."memberDescriptor" AS member_descriptor
                FROM source_snapshot s
                CROSS JOIN LATERAL jsonb_to_recordset(s.inventory -> 'memberships')
                    AS m("groupDescriptor" text, "memberDescriptor" text)
            ),
            observed AS (
                SELECT DISTINCT e."containerDescriptor" AS group_descriptor,
                    e."memberDescriptor" AS member_descriptor
                FROM pages p
                JOIN expected_groups g ON g.resource_key = p.resource_key
                CROSS JOIN LATERAL jsonb_to_recordset(
                    CASE WHEN jsonb_typeof(p.safe_payload_json -> 'value') = 'array'
                        THEN p.safe_payload_json -> 'value' ELSE '[]'::jsonb END)
                    AS e("containerDescriptor" text, "memberDescriptor" text)
                WHERE e."containerDescriptor" = g.descriptor
                    AND NULLIF(btrim(e."memberDescriptor"), '') IS NOT NULL
            ),
            persisted AS (
                SELECT group_descriptor,
                    payload_json ->> 'MemberDescriptor' AS member_descriptor
                FROM discovery_membership_observations WHERE job_id = {lease.Id}
            ),
            expected AS (
                SELECT * FROM saved WHERE NOT (SELECT externalized FROM coverage)
                UNION ALL
                SELECT * FROM persisted WHERE (SELECT externalized FROM coverage)
            ),
            differences AS (
                (SELECT * FROM expected EXCEPT ALL SELECT * FROM observed)
                UNION ALL
                (SELECT * FROM observed EXCEPT ALL SELECT * FROM expected)
            )
            SELECT (
                NOT EXISTS (SELECT 1 FROM source_snapshot)
                OR EXISTS (
                    SELECT 1 FROM pages p LEFT JOIN expected_groups g
                        ON g.resource_key = p.resource_key
                    WHERE g.resource_key IS NULL
                        OR jsonb_typeof(p.safe_payload_json -> 'value') IS DISTINCT FROM 'array'
                )
                OR EXISTS (SELECT 1 FROM differences)
                OR EXISTS (
                    SELECT 1 FROM coverage c
                    WHERE c.memberships_complete AND NOT c.groups_complete
                        OR c.externalized AND (c.inline_count <> 0
                            OR c.expected_count < 0 OR c.expected_count > 20_000
                            OR c.expected_count <> (SELECT count(*) FROM persisted))
                )
                OR EXISTS (
                    SELECT 1 FROM page_sequence seq CROSS JOIN coverage c
                    WHERE c.memberships_complete
                        AND (seq.page_count = 0 OR seq.first_page <> 0
                            OR seq.last_page + 1 <> seq.page_count
                            OR seq.terminal_count <> 1
                            OR seq.terminal_page <> seq.last_page)
                )
            ) AS "Value"
            """).SingleAsync(ct);
        if (mismatch)
            throw new DiscoveryCheckpointException("MembershipSnapshotMismatch");
    }

    // Source ACLs are observations only. Every ACE is stored under the same
    // PostgreSQL transaction and live lease as its sanitized source checkpoint.
    // The hash uses canonical normalized allow/deny/inheritance data, not a
    // provider-supplied raw response or arbitrary extension fields.
    private async Task IngestAclPageAsync(
        DiscoveryPageCheckpoint page, CancellationToken ct)
    {
        if (page.PageIndex != 0)
            throw new DiscoveryCheckpointException("InvalidAclObservation");
        using var doc = JsonDocument.Parse(page.SafePayloadJson);
        if (!doc.RootElement.TryGetProperty("value", out var values) ||
            values.ValueKind != JsonValueKind.Array)
            throw new DiscoveryCheckpointException("InvalidAclObservation");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(string SourceId, string Token, string Descriptor,
            string Fingerprint, string Payload)>();
        foreach (var acl in values.EnumerateArray())
        {
            if (acl.ValueKind != JsonValueKind.Object ||
                !acl.TryGetProperty("token", out var tokenValue) ||
                tokenValue.ValueKind != JsonValueKind.String ||
                !acl.TryGetProperty("inheritPermissions", out var inheritValue) ||
                inheritValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !acl.TryGetProperty("acesDictionary", out var dictionary) ||
                dictionary.ValueKind != JsonValueKind.Object)
                throw new DiscoveryCheckpointException("InvalidAclObservation");
            var token = tokenValue.GetString()!;
            if (string.IsNullOrWhiteSpace(token) || token.Length > 512 ||
                page.ResourceKey != "security-acl:" +
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..40])
                throw new DiscoveryCheckpointException("InvalidAclObservation");

            foreach (var entry in dictionary.EnumerateObject())
            {
                var ace = entry.Value;
                if (ace.ValueKind != JsonValueKind.Object ||
                    !ace.TryGetProperty("allow", out var allowValue) ||
                    allowValue.ValueKind != JsonValueKind.Number ||
                    !allowValue.TryGetInt64(out var allowMask) ||
                    !ace.TryGetProperty("deny", out var denyValue) ||
                    denyValue.ValueKind != JsonValueKind.Number ||
                    !denyValue.TryGetInt64(out var denyMask))
                    throw new DiscoveryCheckpointException("InvalidAclObservation");
                var descriptor = GetText(ace, "descriptor") ?? entry.Name;
                if (!string.Equals(descriptor, entry.Name, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(descriptor) || descriptor.Length > 512)
                    throw new DiscoveryCheckpointException("InvalidAclObservation");
                var sourceId = JsonSerializer.Serialize(new[] { token, descriptor });
                if (sourceId.Length > 1024 || !seen.Add(sourceId))
                    throw new DiscoveryCheckpointException("InvalidAclObservation");
                var payload = JsonSerializer.Serialize(new {
                    AllowMask = allowMask, DenyMask = denyMask,
                    InheritPermissions = inheritValue.ValueKind == JsonValueKind.True
                });
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes("acl-observation-v1\n" + payload)));
                rows.Add((sourceId, token, descriptor, fingerprint, payload));
                if (rows.Count > 30_000)
                    throw new DiscoveryCheckpointException("AclObservationLimit");
            }
        }

        const int batchSize = 250;
        for (var i = 0; i < rows.Count; i += batchSize)
        {
            var part = rows.Skip(i).Take(batchSize).ToArray();
            var batch = JsonSerializer.Serialize(part.Select(x => new {
                source_id = x.SourceId, security_token = x.Token,
                security_descriptor = x.Descriptor, fingerprint = x.Fingerprint,
                payload_json = x.Payload
            }));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH incoming AS (
                    SELECT * FROM jsonb_to_recordset({batch}::jsonb)
                    AS r(source_id text, security_token text, security_descriptor text,
                        fingerprint text, payload_json text)
                )
                INSERT INTO discovery_acl_observations (
                    job_id, source_id, security_token, security_descriptor,
                    fingerprint, payload_json, resource_key, page_index)
                SELECT {page.JobId}, source_id, security_token, security_descriptor,
                    fingerprint, payload_json::jsonb, {page.ResourceKey}, {page.PageIndex}
                FROM incoming
                ON CONFLICT (job_id, source_id) DO NOTHING
                """, ct);
            var ids = part.Select(x => x.SourceId).ToArray();
            var persisted = await db.DiscoveryAclObservations.AsNoTracking()
                .Where(x => x.JobId == page.JobId && ids.Contains(x.SourceId))
                .Select(x => new { x.SourceId, x.SecurityToken, x.SecurityDescriptor,
                    x.Fingerprint, x.ResourceKey, x.PageIndex }).ToArrayAsync(ct);
            if (persisted.Length != part.Length ||
                persisted.Any(row => !part.Any(x =>
                    x.SourceId == row.SourceId && x.Token == row.SecurityToken &&
                    x.Descriptor == row.SecurityDescriptor &&
                    x.Fingerprint == row.Fingerprint &&
                    row.ResourceKey == page.ResourceKey && row.PageIndex == page.PageIndex)))
                throw new DiscoveryCheckpointException("AclObservationConflict");
        }
        if (await db.DiscoveryAclObservations.AsNoTracking()
            .CountAsync(x => x.JobId == page.JobId, ct) > 30_000)
            throw new DiscoveryCheckpointException("AclObservationLimit");
    }

    async Task<DiscoveryAclProgress> IDiscoveryAclEvidenceStore.GetProgressAsync(
        Guid jobId, CancellationToken ct)
    {
        if (jobId == Guid.Empty)
            throw new DiscoveryCheckpointException("InvalidCheckpointKey");
        var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .CountAsync(x => x.JobId == jobId &&
                x.ResourceKey.StartsWith("security-acl:"), ct);
        var aces = await db.DiscoveryAclObservations.AsNoTracking()
            .CountAsync(x => x.JobId == jobId, ct);
        return new(jobId, pages, aces);
    }

    async Task<DiscoveryAclObservationPage> IDiscoveryAclEvidenceStore.ListAsync(
        Guid jobId, string? after, int take, CancellationToken ct)
    {
        if (jobId == Guid.Empty || take is < 1 or > 100 ||
            (after?.Length ?? 0) > 1024 || (after?.Any(char.IsControl) ?? false))
            throw new DiscoveryCheckpointException("InvalidAclObservationPage");
        var query = db.DiscoveryAclObservations.AsNoTracking().Where(x => x.JobId == jobId);
        if (after is not null)
            query = query.Where(x => string.Compare(x.SourceId, after) > 0);
        var rows = await query.OrderBy(x => x.SourceId).Take(take + 1).ToArrayAsync(ct);
        var items = rows.Take(take).Select(x => new DiscoveryAclObservation(
            x.SourceId, x.SecurityToken, x.SecurityDescriptor, x.Fingerprint,
            JsonSerializer.Deserialize<JsonElement>(x.PayloadJson))).ToArray();
        return new(jobId, items, rows.Length > take ? items[^1].SourceId : null);
    }

    async Task IDiscoveryAclEvidenceStore.VerifySnapshotAsync(
        MigrationJobLease lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.Kind != MigrationJobKinds.DiscoverSource ||
            lease.SnapshotId != lease.Id ||
            !await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
                x.Id == lease.Id && x.SourceConnectionId == lease.SourceConnectionId &&
                x.SnapshotId == lease.Id && x.Kind == MigrationJobKinds.DiscoverSource &&
                x.State == MigrationJobStates.Running && x.Attempts == lease.Attempt &&
                x.LeaseToken == lease.LeaseToken && x.LeaseUntilUtc > now, ct))
            throw new DiscoveryCheckpointException("DiscoveryLeaseLost");

        // Backfill pre-F-022 checkpoints under the active lease. Replaying a
        // valid page neither performs a provider GET nor duplicates PageSaved.
        var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .Where(x => x.JobId == lease.Id &&
                x.ResourceKey.StartsWith("security-acl:"))
            .OrderBy(x => x.ResourceKey).ThenBy(x => x.PageIndex).ToArrayAsync(ct);
        foreach (var page in pages)
            await SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
                ToCheckpoint(page), ct);

        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH source_snapshot AS (
                SELECT inventory FROM migration_discoveries
                WHERE id = {lease.Id} AND connection_id = {lease.SourceConnectionId}
            ),
            coverage AS (
                SELECT COALESCE((inventory -> 'securityCoverage' ->> 'aclsCollected')::boolean,
                    false) AS complete FROM source_snapshot
            ),
            git_repos AS (
                SELECT p.project ->> 'providerId' AS project_id,
                    r.repo ->> 'providerId' AS repo_id
                FROM source_snapshot s
                CROSS JOIN LATERAL jsonb_array_elements(
                    COALESCE(s.inventory -> 'snapshot' -> 'projects', '[]'::jsonb))
                    AS p(project)
                CROSS JOIN LATERAL jsonb_array_elements(
                    COALESCE(p.project -> 'repositories', '[]'::jsonb)) AS r(repo)
                WHERE r.repo ->> 'versionControl' IN ('0', 'Git')
            ),
            tokens AS (
                SELECT DISTINCT 'repoV2/' || project_id AS token FROM git_repos
                UNION
                SELECT 'repoV2/' || project_id || '/' || repo_id FROM git_repos
            ),
            expected_keys AS (
                SELECT 'security-acl:' ||
                    left(encode(sha256(convert_to(token, 'UTF8')), 'hex'), 40)
                    AS resource_key FROM tokens
            ),
            saved_pages AS (
                SELECT p.resource_key, p.page_index, p.safe_payload_json,
                    p.next_continuation
                FROM discovery_page_checkpoints p
                WHERE p.job_id = {lease.Id} AND p.resource_key LIKE 'security-acl:%'
            ),
            acl_entries AS (
                SELECT p.resource_key, p.page_index, acl.item ->> 'token' AS security_token,
                    COALESCE(ace.value ->> 'descriptor', ace.key) AS security_descriptor,
                    (ace.value ->> 'allow')::bigint AS allow_mask,
                    (ace.value ->> 'deny')::bigint AS deny_mask,
                    (acl.item ->> 'inheritPermissions')::boolean AS inherit_permissions
                FROM saved_pages p
                CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(p.safe_payload_json -> 'value') = 'array'
                        THEN p.safe_payload_json -> 'value' ELSE '[]'::jsonb END)
                    AS acl(item)
                CROSS JOIN LATERAL jsonb_each(
                    CASE WHEN jsonb_typeof(acl.item -> 'acesDictionary') = 'object'
                        THEN acl.item -> 'acesDictionary' ELSE jsonb_build_object() END)
                    AS ace(key, value)
            ),
            page_rows AS (
                SELECT security_token, security_descriptor, allow_mask,
                    deny_mask, inherit_permissions FROM acl_entries
            ),
            persisted AS (
                SELECT security_token, security_descriptor,
                    (payload_json ->> 'AllowMask')::bigint AS allow_mask,
                    (payload_json ->> 'DenyMask')::bigint AS deny_mask,
                    (payload_json ->> 'InheritPermissions')::boolean AS inherit_permissions
                FROM discovery_acl_observations WHERE job_id = {lease.Id}
            ),
            snapshot_rows AS (
                SELECT DISTINCT a."securityToken" AS security_token,
                    a."securityDescriptor" AS security_descriptor,
                    a."allowMask" AS allow_mask, a."denyMask" AS deny_mask,
                    a."inheritPermissions" AS inherit_permissions
                FROM source_snapshot s
                CROSS JOIN LATERAL jsonb_to_recordset(s.inventory -> 'aclEvidence')
                    AS a("securityToken" text, "securityDescriptor" text,
                        "allowMask" bigint, "denyMask" bigint, "inheritPermissions" boolean)
            ),
            differences AS (
                (SELECT * FROM page_rows EXCEPT ALL SELECT * FROM persisted)
                UNION ALL
                (SELECT * FROM persisted EXCEPT ALL SELECT * FROM page_rows)
                UNION ALL
                (SELECT * FROM snapshot_rows EXCEPT ALL SELECT * FROM persisted)
                UNION ALL
                (SELECT * FROM persisted EXCEPT ALL SELECT * FROM snapshot_rows)
            )
            SELECT (
                NOT EXISTS (SELECT 1 FROM source_snapshot)
                OR EXISTS (SELECT 1 FROM differences)
                OR EXISTS (
                    SELECT 1 FROM saved_pages p
                    LEFT JOIN expected_keys e ON p.resource_key = e.resource_key
                    WHERE e.resource_key IS NULL OR p.page_index <> 0
                        OR p.next_continuation IS NOT NULL
                        OR jsonb_typeof(p.safe_payload_json -> 'value') IS DISTINCT FROM 'array'
                )
                OR EXISTS (
                    SELECT 1 FROM saved_pages p
                    CROSS JOIN LATERAL jsonb_array_elements(
                        CASE WHEN jsonb_typeof(p.safe_payload_json -> 'value') = 'array'
                            THEN p.safe_payload_json -> 'value' ELSE '[]'::jsonb END)
                        AS acl(item)
                    WHERE jsonb_typeof(acl.item -> 'acesDictionary') IS DISTINCT FROM 'object'
                        OR 'security-acl:' || left(encode(sha256(
                            convert_to(COALESCE(acl.item ->> 'token', ''), 'UTF8')), 'hex'), 40)
                            <> p.resource_key
                )
                OR EXISTS (
                    SELECT 1 FROM discovery_acl_observations o
                    WHERE o.job_id = {lease.Id} AND
                        (o.page_index <> 0 OR o.resource_key <> 'security-acl:' ||
                            left(encode(sha256(convert_to(o.security_token, 'UTF8')),
                                'hex'), 40))
                )
                OR EXISTS (
                    SELECT 1 FROM coverage c WHERE c.complete AND
                        EXISTS (SELECT 1 FROM expected_keys e LEFT JOIN saved_pages p
                            ON p.resource_key = e.resource_key
                            WHERE p.resource_key IS NULL)
                )
            ) AS "Value"
            """).SingleAsync(ct);
        if (mismatch)
            throw new DiscoveryCheckpointException("AclSnapshotMismatch");
    }

    private async Task IngestIdentityPageAsync(
        DiscoveryPageCheckpoint page, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(page.SafePayloadJson);
        if (!doc.RootElement.TryGetProperty("value", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 15_000)
            throw new DiscoveryCheckpointException("InvalidIdentityObservation");

        var assets = new List<StagingAssetInput>(rows.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in rows.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) 
                throw new DiscoveryCheckpointException("InvalidIdentityObservation");
            var descriptor = GetText(entry, "descriptor");
            if (string.IsNullOrWhiteSpace(descriptor)) continue;
            StagingAssetInput asset;
            try
            {
                asset = StagingAssetNormalizer.NormalizeIdentity(
                    descriptor, GetText(entry, "displayName") ?? descriptor,
                    GetText(entry, "mailAddress"), GetText(entry, "legacyDescriptor"));
            }
            catch (StagingSyncInputException)
            {
                throw new DiscoveryCheckpointException("InvalidIdentityObservation");
            }
            if (!seen.Add(asset.SourceId))
                throw new DiscoveryCheckpointException("DuplicateIdentityObservation");
            assets.Add(asset);
        }

        const int batchSize = 250;
        for (var i = 0; i < assets.Count; i += batchSize)
        {
            var part = assets.Skip(i).Take(batchSize).ToArray();
            var payload = JsonSerializer.Serialize(part.Select(asset => new {
                source_id = asset.SourceId,
                fingerprint = asset.Fingerprint,
                payload_json = asset.PayloadJson
            }));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH incoming AS (
                    SELECT * FROM jsonb_to_recordset({payload}::jsonb)
                        AS items(source_id text, fingerprint text, payload_json text)
                )
                INSERT INTO discovery_identity_observations (
                    job_id, source_id, fingerprint, payload_json,
                    resource_key, page_index)
                SELECT {page.JobId}, source_id, fingerprint,
                    payload_json::jsonb, {page.ResourceKey}, {page.PageIndex}
                FROM incoming
                ON CONFLICT (job_id, source_id) DO NOTHING
                """, ct);
            // The same descriptor may recur on another provider page.
            // Identical observations are idempotent; contradictory user
            // information fails rather than silently overwriting evidence.
            var ids = part.Select(x => x.SourceId).ToArray();
            var existing = await db.DiscoveryIdentityObservations.AsNoTracking()
                .Where(x => x.JobId == page.JobId && ids.Contains(x.SourceId))
                .Select(x => new { x.SourceId, x.Fingerprint })
                .ToArrayAsync(ct);
            if (existing.Length != part.Length ||
                existing.Any(row => !part.Any(asset =>
                    asset.SourceId == row.SourceId &&
                    asset.Fingerprint == row.Fingerprint)))
                throw new DiscoveryCheckpointException("IdentityObservationConflict");
        }
    }

    private static string? GetText(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public async Task<DiscoveryIdentityProgress> GetProgressAsync(
        Guid jobId, CancellationToken ct)
    {
        if (jobId == Guid.Empty)
            throw new DiscoveryCheckpointException("InvalidCheckpointKey");
        var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .CountAsync(x => x.JobId == jobId && x.ResourceKey == "graph-users", ct);
        var users = await db.DiscoveryIdentityObservations.AsNoTracking()
            .CountAsync(x => x.JobId == jobId, ct);
        return new(jobId, pages, users);
    }

    public async Task<DiscoveryIdentityObservationPage> ListAsync(
        Guid jobId, string? after, int take, CancellationToken ct)
    {
        if (jobId == Guid.Empty || take is < 1 or > 100 ||
            (after?.Length ?? 0) > 1024)
            throw new DiscoveryCheckpointException("InvalidIdentityObservationPage");
        var query = db.DiscoveryIdentityObservations.AsNoTracking()
            .Where(x => x.JobId == jobId);
        if (after is not null)
            query = query.Where(x => string.Compare(x.SourceId, after) > 0);
        var rows = await query.OrderBy(x => x.SourceId)
            .Take(take + 1).ToArrayAsync(ct);
        var selected = rows.Take(take).Select(x => new DiscoveryIdentityObservation(
            x.SourceId, x.Fingerprint,
            JsonSerializer.Deserialize<JsonElement>(x.PayloadJson))).ToArray();
        return new(jobId, selected, rows.Length > take ? selected[^1].SourceId : null);
    }

    public async Task VerifySnapshotAsync(MigrationJobLease lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.Kind != MigrationJobKinds.DiscoverSource ||
            !await db.MigrationJobs.AsNoTracking().AnyAsync(job =>
                job.Id == lease.Id && job.Kind == MigrationJobKinds.DiscoverSource &&
                job.SourceConnectionId == lease.SourceConnectionId &&
                job.SnapshotId == lease.Id &&
                job.State == MigrationJobStates.Running &&
                job.Attempts == lease.Attempt &&
                job.LeaseToken == lease.LeaseToken &&
                job.LeaseUntilUtc > now, ct))
            throw new DiscoveryCheckpointException("DiscoveryLeaseLost");

        // Recovery from a deployment upgrade: the F-008 source page may be
        // committed already, even when observations were not materialized
        // before this feature existed. Backfill under the new live lease.
        var cached = await db.DiscoveryPageCheckpoints.AsNoTracking()
            .Where(x => x.JobId == lease.Id && x.ResourceKey == "graph-users")
            .OrderBy(x => x.PageIndex).ToArrayAsync(ct);
        foreach (var page in cached)
            await SaveAsync(new DiscoveryLeaseContext(
                lease.Id, lease.LeaseToken, lease.Attempt),
                ToCheckpoint(page), ct);

        var exists = await db.Discoveries.AsNoTracking()
            .AnyAsync(x => x.Id == lease.Id && x.ConnectionId == lease.SourceConnectionId, ct);
        if (!exists)
            throw new DiscoveryCheckpointException("IdentitySnapshotMismatch");

        var snapshotInfo = await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
                'external', COALESCE((inventory ->> 'identitiesExternalized')::boolean, false),
                'count', COALESCE((inventory ->> 'identityCount')::integer, -1),
                'inline', jsonb_array_length(inventory -> 'snapshot' -> 'identities'),
                'complete', COALESCE((inventory ->> 'identitiesComplete')::boolean, false)
            )::text AS "Value"
            FROM migration_discoveries WHERE id = {lease.Id}
            """).SingleAsync(ct);
        using var info = JsonDocument.Parse(snapshotInfo);
        var external = info.RootElement.GetProperty("external").GetBoolean();
        if (external)
        {
            var expected = info.RootElement.GetProperty("count").GetInt32();
            var inline = info.RootElement.GetProperty("inline").GetInt32();
            if (expected is < 0 or > 15_000 || inline != 0 ||
                await db.DiscoveryIdentityObservations.AsNoTracking()
                    .CountAsync(x => x.JobId == lease.Id, ct) != expected)
                throw new DiscoveryCheckpointException("IdentitySnapshotMismatch");

            var pageCount = await db.DiscoveryPageCheckpoints.AsNoTracking()
                .CountAsync(x => x.JobId == lease.Id && x.ResourceKey == "graph-users", ct);
            if (pageCount == 0 && info.RootElement.GetProperty("complete").GetBoolean())
                throw new DiscoveryCheckpointException("IdentitySnapshotMismatch");

            // Verify the exact sanitized Graph page evidence, rather than
            // trusting a snapshot count or a provisional record in isolation.
            // SQL handles even very large source pages without a user list in .NET.
            var pageMismatch = await db.Database.SqlQuery<bool>($"""
                WITH page_users AS (
                    SELECT DISTINCT item ->> 'descriptor' AS descriptor,
                        COALESCE(item ->> 'displayName', item ->> 'descriptor') AS display_name,
                        item ->> 'mailAddress' AS email,
                        item ->> 'legacyDescriptor' AS legacy
                    FROM discovery_page_checkpoints p
                    CROSS JOIN LATERAL jsonb_array_elements(
                        p.safe_payload_json -> 'value') AS item
                    WHERE p.job_id = {lease.Id} AND p.resource_key = 'graph-users'
                        AND NULLIF(btrim(item ->> 'descriptor'), '') IS NOT NULL
                ),
                observed AS (
                    SELECT source_id AS descriptor,
                        payload_json ->> 'DisplayName' AS display_name,
                        payload_json ->> 'Email' AS email,
                        payload_json ->> 'LegacyDescriptor' AS legacy
                    FROM discovery_identity_observations WHERE job_id = {lease.Id}
                ),
                differences AS (
                    (SELECT * FROM page_users EXCEPT ALL SELECT * FROM observed)
                    UNION ALL
                    (SELECT * FROM observed EXCEPT ALL SELECT * FROM page_users)
                )
                SELECT EXISTS(SELECT 1 FROM differences) AS "Value"
                """).SingleAsync(ct);
            if (pageMismatch)
                throw new DiscoveryCheckpointException("IdentitySnapshotMismatch");
            return;
        }

        // Legacy inline identities still use the original bidirectional
        // snapshot/observation evidence check.
        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH saved AS (
                SELECT i."providerId" AS descriptor, i."displayName" AS display_name,
                    i.email AS email, i."legacyDescriptor" AS legacy
                FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(d.inventory -> 'snapshot' -> 'identities')
                    AS i("providerId" text, "displayName" text, email text, "legacyDescriptor" text)
                WHERE d.id = {lease.Id} AND d.connection_id = {lease.SourceConnectionId}
            ),
            observed AS (
                SELECT source_id AS descriptor,
                    payload_json ->> 'DisplayName' AS display_name,
                    payload_json ->> 'Email' AS email,
                    payload_json ->> 'LegacyDescriptor' AS legacy
                FROM discovery_identity_observations
                WHERE job_id = {lease.Id}
            ),
            differences AS (
                (SELECT * FROM saved EXCEPT SELECT * FROM observed)
                UNION ALL
                (SELECT * FROM observed EXCEPT SELECT * FROM saved)
            )
            SELECT EXISTS (SELECT 1 FROM differences) AS "Value"
            """).SingleAsync(ct);
        if (mismatch)
            throw new DiscoveryCheckpointException("IdentitySnapshotMismatch");
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
