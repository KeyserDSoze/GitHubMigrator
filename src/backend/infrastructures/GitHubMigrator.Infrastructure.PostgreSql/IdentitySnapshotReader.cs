using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Snapshot-bound reads from either the F-015 post-success normalized Graph
// observations or the original F-003 inline identity JSONB (old snapshots).
// Every page is bounded at 100; no full identity list enters .NET.
public sealed class PostgresIdentitySnapshotReader(AppDbContext db) : IIdentitySnapshotReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<DiscoveryInventory?> GetMetadataAsync(Guid snapshotId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty) return null;
        var json = await db.Database.SqlQuery<string>($"""
            SELECT jsonb_set(
                jsonb_set(inventory, ARRAY['snapshot','identities'], '[]'::jsonb),
                ARRAY['refs'], '[]'::jsonb)::text AS "Value"
            FROM migration_discoveries
            WHERE id = {snapshotId}
            """).FirstOrDefaultAsync(ct);
        if (json is null) return null;
        await UseObservationsAsync(snapshotId, ct);
        return JsonSerializer.Deserialize<DiscoveryInventory>(json, Options);
    }

    private async Task<bool> UseObservationsAsync(Guid snapshotId, CancellationToken ct)
    {
        var config = await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
                'external', COALESCE((inventory ->> 'identitiesExternalized')::boolean, false),
                'expected', COALESCE((inventory ->> 'identityCount')::integer, -1),
                'inline', jsonb_array_length(inventory -> 'snapshot' -> 'identities'),
                'complete', COALESCE((inventory ->> 'identitiesComplete')::boolean, false)
            )::text AS "Value"
            FROM migration_discoveries WHERE id = {snapshotId}
            """).SingleOrDefaultAsync(ct);
        if (config is null)
            throw new MappingInputException("IdentitySourceNotFound");
        using var meta = JsonDocument.Parse(config);
        var externalized = meta.RootElement.GetProperty("external").GetBoolean();
        var count = await db.DiscoveryIdentityObservations.AsNoTracking()
            .CountAsync(x => x.JobId == snapshotId, ct);
        if (!externalized && count == 0)
        {
            // A pre-F-015 immutable snapshot has only inline identities.
            return false;
        }

        var succeeded = await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
            x.Id == snapshotId && x.SnapshotId == snapshotId &&
            x.Kind == MigrationJobKinds.DiscoverSource &&
            x.State == MigrationJobStates.Succeeded, ct);
        if (!succeeded)
            throw new MappingInputException("IdentitySourceNotReady");

        var expected = externalized
            ? meta.RootElement.GetProperty("expected").GetInt32()
            : meta.RootElement.GetProperty("inline").GetInt32();
        if (expected != count || expected < 0 || expected > StagingAssetNormalizer.MaxAssetsPerSnapshot ||
            (externalized && meta.RootElement.GetProperty("inline").GetInt32() != 0))
            throw new MappingInputException("IdentitySourceMismatch");

        if (externalized)
        {
            // Even a zero-user Graph discovery must have a source page if
            // the inventory claims its Graph user coverage is complete.
            var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
                .CountAsync(x => x.JobId == snapshotId && x.ResourceKey == "graph-users", ct);
            if (pages == 0 && meta.RootElement.GetProperty("complete").GetBoolean())
                throw new MappingInputException("IdentitySourceMismatch");

            // Read-only F-004/F-005 queries independently recheck snapshot
            // evidence AFTER job success: never silently accept tampering
            // that replaces one user with another but leaves totals equal.
            var pageMismatch = await db.Database.SqlQuery<bool>($"""
                WITH source_rows AS (
                    SELECT DISTINCT item ->> 'descriptor' AS descriptor,
                        COALESCE(item ->> 'displayName', item ->> 'descriptor') AS display_name,
                        item ->> 'mailAddress' AS email,
                        item ->> 'legacyDescriptor' AS legacy
                    FROM discovery_page_checkpoints p
                    CROSS JOIN LATERAL jsonb_array_elements(
                        p.safe_payload_json -> 'value') item
                    WHERE p.job_id = {snapshotId} AND p.resource_key = 'graph-users'
                        AND NULLIF(btrim(item ->> 'descriptor'), '') IS NOT NULL
                ),
                observed AS (
                    SELECT source_id AS descriptor,
                        payload_json ->> 'DisplayName' AS display_name,
                        payload_json ->> 'Email' AS email,
                        payload_json ->> 'LegacyDescriptor' AS legacy
                    FROM discovery_identity_observations WHERE job_id = {snapshotId}
                ),
                diff AS (
                    (SELECT * FROM source_rows EXCEPT ALL SELECT * FROM observed)
                    UNION ALL
                    (SELECT * FROM observed EXCEPT ALL SELECT * FROM source_rows)
                )
                SELECT EXISTS(SELECT 1 FROM diff) AS "Value"
                """).SingleAsync(ct);
            if (pageMismatch) throw new MappingInputException("IdentitySourceMismatch");
            return true;
        }

        // Legacy F-015 snapshots still carry inline identity data, so compare
        // its complete source metadata against all persisted observations.
        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH saved AS (
                SELECT i."providerId" AS id, i."displayName" AS display_name,
                    i.email, i."legacyDescriptor" AS legacy
                FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(
                    d.inventory -> 'snapshot' -> 'identities')
                    AS i("providerId" text, "displayName" text,
                        email text, "legacyDescriptor" text)
                WHERE d.id = {snapshotId}
            ),
            observed AS (
                SELECT source_id AS id, payload_json ->> 'DisplayName' AS display_name,
                    payload_json ->> 'Email' AS email,
                    payload_json ->> 'LegacyDescriptor' AS legacy
                FROM discovery_identity_observations WHERE job_id = {snapshotId}
            ),
            diff AS (
                (SELECT * FROM saved EXCEPT ALL SELECT * FROM observed)
                UNION ALL
                (SELECT * FROM observed EXCEPT ALL SELECT * FROM saved)
            )
            SELECT EXISTS(SELECT 1 FROM diff) AS "Value"
            """).SingleAsync(ct);
        if (mismatch) throw new MappingInputException("IdentitySourceMismatch");
        return true;
    }

    public async Task<int> CountAsync(Guid snapshotId, CancellationToken ct)
    {
        var observed = await UseObservationsAsync(snapshotId, ct);
        if (observed)
            return await db.DiscoveryIdentityObservations.AsNoTracking()
                .CountAsync(x => x.JobId == snapshotId, ct);
        return await db.Database.SqlQuery<int>($"""
            SELECT jsonb_array_length(inventory -> 'snapshot' -> 'identities') AS "Value"
            FROM migration_discoveries WHERE id = {snapshotId}
            """).SingleOrDefaultAsync(ct);
    }

    public async Task<IdentitySourcePage> ListAsync(
        Guid snapshotId, string? after, int take, CancellationToken ct)
    {
        ValidatePage(snapshotId, after, take);
        var observed = await UseObservationsAsync(snapshotId, ct);
        var rows = await db.Database.SqlQuery<string>($"""
            WITH identities AS (
                SELECT o.source_id AS id,
                    o.payload_json ->> 'DisplayName' AS display_name,
                    o.payload_json ->> 'Email' AS email,
                    o.payload_json ->> 'LegacyDescriptor' AS legacy
                FROM discovery_identity_observations o
                WHERE o.job_id = {snapshotId} AND {observed}
                UNION ALL
                SELECT i."providerId" AS id, i."displayName" AS display_name,
                    i.email AS email, i."legacyDescriptor" AS legacy
                FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(
                    d.inventory -> 'snapshot' -> 'identities')
                    AS i("providerId" text, "displayName" text, email text, "legacyDescriptor" text)
                WHERE d.id = {snapshotId} AND NOT {observed}
            )
            SELECT jsonb_build_object('sourceIdentityId', id,
                'displayName', display_name, 'email', email,
                'legacyDescriptor', legacy)::text AS "Value"
            FROM identities
            WHERE ({after}::text IS NULL OR id COLLATE "C" > {after}::text COLLATE "C")
            ORDER BY id COLLATE "C"
            LIMIT {take + 1}
            """).ToArrayAsync(ct);
        var items = rows.Take(take).Select(json =>
            JsonSerializer.Deserialize<IdentitySourceItem>(json, Options)!).ToArray();
        return new(snapshotId, items, rows.Length > take ? items[^1].SourceIdentityId : null);
    }

    public async Task<IReadOnlySet<string>> ResolveKnownAsync(
        Guid snapshotId, IReadOnlyList<string> sourceIds, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || sourceIds.Count > 10_000 ||
            sourceIds.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1024))
            throw new MappingInputException("InvalidMappingBatch");
        if (sourceIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var observed = await UseObservationsAsync(snapshotId, ct);
        var requested = sourceIds.Distinct(StringComparer.Ordinal).ToArray();
        var found = await db.Database.SqlQuery<string>($"""
            SELECT source_id AS "Value" FROM discovery_identity_observations
            WHERE job_id = {snapshotId} AND {observed}
                AND source_id = ANY({requested})
            UNION ALL
            SELECT i."providerId" AS "Value"
            FROM migration_discoveries d
            CROSS JOIN LATERAL jsonb_to_recordset(
                d.inventory -> 'snapshot' -> 'identities') AS i("providerId" text)
            WHERE d.id = {snapshotId} AND NOT {observed}
                AND i."providerId" = ANY({requested})
            """).ToArrayAsync(ct);
        return found.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListUnmappedAsync(
        Guid snapshotId, IReadOnlyList<string> mappedIds, int take, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || take is < 1 or > 100 ||
            mappedIds.Count > 10_000)
            throw new MappingInputException("InvalidMappingBatch");
        var observed = await UseObservationsAsync(snapshotId, ct);
        var known = mappedIds.ToArray();
        return await db.Database.SqlQuery<string>($"""
            WITH identities AS (
                SELECT source_id AS id FROM discovery_identity_observations
                WHERE job_id = {snapshotId} AND {observed}
                UNION ALL
                SELECT i."providerId" AS id FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(
                    d.inventory -> 'snapshot' -> 'identities') AS i("providerId" text)
                WHERE d.id = {snapshotId} AND NOT {observed}
            )
            SELECT id AS "Value" FROM identities
            WHERE NOT (id = ANY({known}))
            ORDER BY id COLLATE "C"
            LIMIT {take}
            """).ToArrayAsync(ct);
    }

    public async Task<int> CountUnresolvedAclPrincipalsAsync(
        Guid snapshotId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty)
            throw new MappingInputException("InvalidMappingContext");
        var observed = await UseObservationsAsync(snapshotId, ct);
        return await db.Database.SqlQuery<int>($"""
            WITH known AS (
                SELECT DISTINCT o.payload_json ->> 'LegacyDescriptor' AS legacy
                FROM discovery_identity_observations o
                WHERE o.job_id = {snapshotId} AND {observed}
                UNION
                SELECT i."legacyDescriptor" AS legacy FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(
                    d.inventory -> 'snapshot' -> 'identities') AS i("legacyDescriptor" text)
                WHERE d.id = {snapshotId} AND NOT {observed}
                UNION
                SELECT g."legacyDescriptor" AS legacy FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(
                    d.inventory -> 'groups') AS g("legacyDescriptor" text)
                WHERE d.id = {snapshotId}
            ),
            principals AS (
                SELECT DISTINCT a."securityDescriptor" AS descriptor
                FROM migration_discoveries d
                CROSS JOIN LATERAL jsonb_to_recordset(d.inventory -> 'aclEvidence')
                    AS a("securityDescriptor" text)
                WHERE d.id = {snapshotId}
            )
            SELECT COUNT(*)::int AS "Value"
            FROM principals a
            WHERE NOT EXISTS (SELECT 1 FROM known k WHERE k.legacy = a.descriptor)
            """).SingleAsync(ct);
    }

    private static void ValidatePage(Guid snapshotId, string? after, int take)
    {
        if (snapshotId == Guid.Empty || take is < 1 or > 100 ||
            (after?.Length ?? 0) > 1024 || (after?.Any(char.IsControl) ?? false))
            throw new MappingInputException("InvalidIdentitySourcePage");
    }
}
