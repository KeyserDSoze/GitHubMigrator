using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// Ref counts are always tied to one immutable discovery snapshot. Externalized
// jobs must be fully succeeded and have a matching sealed manifest; legacy
// source snapshots are counted in SQL from their inline JSONB array.
public sealed class PostgresPlanRefEvidenceReader(AppDbContext db) : IPlanRefEvidenceReader
{
    public async Task<IReadOnlyDictionary<string, int>> CountByRepositoryAsync(
        Guid snapshotId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty)
            throw new PlanInputException("InvalidPlanContext");

        var details = await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
                'externalized', COALESCE((inventory ->> 'refsExternalized')::boolean, false),
                'inlineCount', COALESCE(jsonb_array_length(inventory -> 'refs'), 0),
                'refCount', COALESCE((inventory ->> 'refCount')::integer, 0),
                'connectionId', connection_id
            )::text AS "Value"
            FROM migration_discoveries
            WHERE id = {snapshotId}
            """).SingleOrDefaultAsync(ct);
        if (details is null)
            throw new KeyNotFoundException("SnapshotNotFound");

        using var json = JsonDocument.Parse(details);
        var root = json.RootElement;
        var external = root.GetProperty("externalized").GetBoolean();
        var inlineCount = root.GetProperty("inlineCount").GetInt32();
        if (!external)
        {
            // SQL-side aggregation of historical F-003 inline ref metadata:
            // never materialize the full ref array into the API process.
            var raw = await db.Database.SqlQuery<string>($"""
                SELECT jsonb_object_agg(repository_id, count)::text AS "Value"
                FROM (
                    SELECT r."repositoryId" AS repository_id, COUNT(*)::int AS count
                    FROM migration_discoveries d
                    CROSS JOIN LATERAL jsonb_to_recordset(d.inventory -> 'refs')
                        AS r("repositoryId" text)
                    WHERE d.id = {snapshotId}
                    GROUP BY r."repositoryId"
                ) rows
                """).SingleOrDefaultAsync(ct);
            var totals = Decode(raw);
            if (totals.Values.Sum(x => (long)x) != inlineCount)
                throw new PlanInputException("PlanRefEvidenceMismatch");
            return totals;
        }

        var expected = root.GetProperty("refCount").GetInt32();
        var source = root.GetProperty("connectionId").GetGuid();
        if (inlineCount != 0 || expected < 0 ||
            expected > StagingAssetNormalizer.MaxAssetsPerSnapshot)
            throw new PlanInputException("PlanRefEvidenceMismatch");

        var job = await db.MigrationJobs.AsNoTracking()
            .AnyAsync(x => x.Id == snapshotId && x.SnapshotId == snapshotId &&
                x.SourceConnectionId == source &&
                x.Kind == MigrationJobKinds.DiscoverSource &&
                x.State == MigrationJobStates.Succeeded, ct);
        var manifest = await db.DiscoveryRefManifests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == snapshotId, ct);
        if (!job || manifest is null || manifest.SourceConnectionId != source ||
            manifest.AssetCount != expected ||
            manifest.ChunkSize != PostgresDiscoveryRefManifestStore.ChunkSize ||
            manifest.ChunkCount != (expected + manifest.ChunkSize - 1) / manifest.ChunkSize)
            throw new PlanInputException("PlanRefEvidenceUnavailable");

        var descriptors = await db.DiscoveryRefChunks.AsNoTracking()
            .Where(x => x.JobId == snapshotId)
            .OrderBy(x => x.Index)
            .Select(x => new { x.Index, x.AssetCount, x.Sha256 }).ToArrayAsync(ct);
        if (descriptors.Length != manifest.ChunkCount ||
            descriptors.Sum(x => (long)x.AssetCount) != expected ||
            descriptors.Where((x, i) => x.Index != i ||
                x.AssetCount is < 1 or > PostgresDiscoveryRefManifestStore.ChunkSize ||
                x.Sha256.Length != 64).Any() ||
            ManifestDigest(descriptors.Select(x => x.Sha256)) != manifest.RootSha256)
            throw new PlanInputException("PlanRefEvidenceMismatch");

        // Check checkpointed page evidence against normalized ref observations
        // in BOTH directions. Counts alone cannot detect a ref replaced with
        // another repo/ref/OID after a job finishes. This work is SQL-side;
        // no source ref list or raw provider page is returned to the API.
        var mismatch = await db.Database.SqlQuery<bool>($"""
            WITH source_refs AS (
                SELECT substring(p.resource_key from 6) AS repository_id,
                    item ->> 'name' AS ref_name,
                    item ->> 'objectId' AS oid
                FROM discovery_page_checkpoints p
                CROSS JOIN LATERAL jsonb_array_elements(
                    p.safe_payload_json -> 'value') AS item
                WHERE p.job_id = {snapshotId}
                  AND p.resource_key LIKE 'refs:%'
                  AND COALESCE(item ->> 'name', '') <> ''
                  AND COALESCE(item ->> 'objectId', '') <> ''
            ),
            observations AS (
                SELECT o.repository_id,
                    o.source_id::jsonb ->> 1 AS ref_name,
                    o.payload_json ->> 'ObjectId' AS oid
                FROM discovery_ref_observations o
                WHERE o.job_id = {snapshotId}
            ),
            difference AS (
                (SELECT * FROM source_refs EXCEPT ALL SELECT * FROM observations)
                UNION ALL
                (SELECT * FROM observations EXCEPT ALL SELECT * FROM source_refs)
            )
            SELECT EXISTS(SELECT 1 FROM difference) AS "Value"
            """).SingleAsync(ct);
        if (mismatch)
            throw new PlanInputException("PlanRefEvidenceMismatch");

        var grouped = await db.DiscoveryRefObservations.AsNoTracking()
            .Where(x => x.JobId == snapshotId)
            .GroupBy(x => x.RepositoryId)
            .Select(x => new { RepositoryId = x.Key, Count = x.Count() })
            .ToArrayAsync(ct);
        if (grouped.Sum(x => (long)x.Count) != expected ||
            grouped.Any(x => string.IsNullOrEmpty(x.RepositoryId)))
            throw new PlanInputException("PlanRefEvidenceMismatch");
        return grouped.ToDictionary(x => x.RepositoryId, x => x.Count, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, int> Decode(string? json)
    {
        if (json is null) return new Dictionary<string, int>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in doc.RootElement.EnumerateObject())
        {
            if (string.IsNullOrEmpty(item.Name) || !item.Value.TryGetInt32(out var count) ||
                count < 0 || !result.TryAdd(item.Name, count))
                throw new PlanInputException("PlanRefEvidenceMismatch");
        }
        return result;
    }

    private static string ManifestDigest(IEnumerable<string> hashes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "ref-manifest-v1\n" + string.Join("\n", hashes) + "\n")));
}
