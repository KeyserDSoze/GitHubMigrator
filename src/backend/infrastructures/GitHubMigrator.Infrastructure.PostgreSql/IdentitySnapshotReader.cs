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
        var count = await db.DiscoveryIdentityObservations.AsNoTracking()
            .CountAsync(x => x.JobId == snapshotId, ct);
        if (count == 0)
        {
            // A snapshot written before F-015 has no observation rows. Its
            // immutable inline JSONB remains the read-only source of truth.
            return false;
        }

        var succeeded = await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
            x.Id == snapshotId && x.SnapshotId == snapshotId &&
            x.Kind == MigrationJobKinds.DiscoverSource &&
            x.State == MigrationJobStates.Succeeded, ct);
        if (!succeeded)
            throw new MappingInputException("IdentitySourceNotReady");

        var expected = await db.Database.SqlQuery<int>($"""
            SELECT jsonb_array_length(inventory -> 'snapshot' -> 'identities') AS "Value"
            FROM migration_discoveries WHERE id = {snapshotId}
            """).SingleOrDefaultAsync(ct);
        if (expected != count)
            throw new MappingInputException("IdentitySourceMismatch");
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
