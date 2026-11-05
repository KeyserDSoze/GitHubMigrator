using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class DiscoveryRefManifestRecord
{
    public Guid JobId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public int AssetCount { get; set; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }
    public string RootSha256 { get; set; } = "";
    public DateTimeOffset PreparedAtUtc { get; set; }
}

public sealed class DiscoveryRefChunkRecord
{
    public Guid JobId { get; set; }
    public int Index { get; set; }
    public int AssetCount { get; set; }
    public string FirstSourceId { get; set; } = "";
    public string LastSourceId { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

// Bounded, index-backed reads of individual ref chunks. A manifest is prepared
// while the source job owns a live lease, but is never exposed before the job
// completes. Canonical staging_assets remains the authoritative catalog.
public sealed class PostgresDiscoveryRefManifestStore(AppDbContext db, TimeProvider clock)
    : IDiscoveryRefManifestStore
{
    public const int ChunkSize = 250;

    public async Task<DiscoveryRefManifest> PrepareAsync(
        MigrationJobLease lease, int expectedRefCount, CancellationToken ct)
    {
        if (lease.Kind != MigrationJobKinds.DiscoverSource ||
            lease.Id == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.Attempt < 1 || expectedRefCount is < 0 or > StagingAssetNormalizer.MaxAssetsPerSnapshot)
            throw new DiscoveryRefManifestException("InvalidRefManifestContext");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize attempts and concurrent manifest preparations for this job.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lease.Id.ToString()}), 734210)", ct);

        var now = clock.GetUtcNow();
        var job = await db.MigrationJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == lease.Id, ct);
        if (job is null || job.Kind != MigrationJobKinds.DiscoverSource ||
            job.State != MigrationJobStates.Running ||
            job.SourceConnectionId != lease.SourceConnectionId ||
            job.SnapshotId != lease.Id || job.Attempts != lease.Attempt ||
            job.LeaseToken != lease.LeaseToken || job.LeaseUntilUtc <= now)
            throw new DiscoveryRefManifestException("DiscoveryLeaseLost");

        // Validate the immutable PostgreSQL snapshot's ref count in SQL:
        // no deserialization of its potentially very large JSONB array.
        var savedRefs = await db.Database.SqlQuery<int>($"""
            SELECT COALESCE(jsonb_array_length(inventory -> 'refs'), 0) AS "Value"
            FROM migration_discoveries
            WHERE id = {lease.Id} AND connection_id = {lease.SourceConnectionId}
            """).SingleOrDefaultAsync(ct);
        var snapshotExists = await db.Discoveries.AsNoTracking()
            .AnyAsync(x => x.Id == lease.Id && x.ConnectionId == lease.SourceConnectionId, ct);
        if (!snapshotExists || savedRefs != expectedRefCount)
            throw new DiscoveryRefManifestException("RefManifestSnapshotMismatch");

        // Count equality alone cannot detect one ref silently replaced with
        // another. Compare the canonical snapshot's entire (repo,name,OID)
        // multiset with the provisional observations in PostgreSQL, without
        // deserializing the snapshot's ref array into process memory.
        var mismatch = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM (
                    SELECT r."repositoryId", r.name, r."objectId"
                    FROM migration_discoveries d
                    CROSS JOIN LATERAL jsonb_to_recordset(d.inventory -> 'refs')
                    AS r("repositoryId" text, name text, "objectId" text)
                    WHERE d.id = {lease.Id}
                    EXCEPT ALL
                    SELECT o.source_id::jsonb ->> 0, o.source_id::jsonb ->> 1,
                        o.payload_json ->> 'ObjectId'
                    FROM discovery_ref_observations o
                    WHERE o.job_id = {lease.Id}
                ) differences
                LIMIT 1
            ) AS "Value"
            """).SingleAsync(ct);
        if (mismatch)
            throw new DiscoveryRefManifestException("RefManifestSnapshotMismatch");

        var existing = await db.DiscoveryRefManifests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == lease.Id, ct);
        if (existing is not null)
        {
            if (existing.SourceConnectionId != lease.SourceConnectionId ||
                existing.AssetCount != expectedRefCount || existing.ChunkSize != ChunkSize)
                throw new DiscoveryRefManifestException("RefManifestMismatch");
            await transaction.CommitAsync(ct);
            return Manifest(existing);
        }

        // AsAsyncEnumerable streams the source-id ordered cursor from Npgsql.
        // In memory we only hold at most 250 source-id/fingerprint pairs and
        // one compact descriptor per chunk, never all ref payloads.
        var chunks = new List<DiscoveryRefChunkRecord>();
        var current = new List<(string SourceId, string Fingerprint)>(ChunkSize);
        var observed = 0;
        var rows = db.DiscoveryRefObservations.AsNoTracking()
            .Where(x => x.JobId == lease.Id)
            .OrderBy(x => x.SourceId)
            .Select(x => new { x.SourceId, x.Fingerprint })
            .AsAsyncEnumerable();

        await foreach (var row in rows.WithCancellation(ct))
        {
            current.Add((row.SourceId, row.Fingerprint));
            observed++;
            if (observed > expectedRefCount)
                throw new DiscoveryRefManifestException("RefManifestCountMismatch");
            if (current.Count == ChunkSize)
            {
                chunks.Add(Chunk(lease.Id, chunks.Count, current));
                current.Clear();
            }
        }
        if (current.Count > 0)
            chunks.Add(Chunk(lease.Id, chunks.Count, current));
        if (observed != expectedRefCount)
            throw new DiscoveryRefManifestException("RefManifestCountMismatch");

        var root = RootDigest(chunks.Select(x => x.Sha256));
        var record = new DiscoveryRefManifestRecord
        {
            JobId = lease.Id, SourceConnectionId = lease.SourceConnectionId,
            AssetCount = observed, ChunkSize = ChunkSize, ChunkCount = chunks.Count,
            RootSha256 = root, PreparedAtUtc = clock.GetUtcNow()
        };
        db.DiscoveryRefManifests.Add(record);
        db.DiscoveryRefChunks.AddRange(chunks);
        await db.SaveChangesAsync(ct);

        var commitTime = clock.GetUtcNow();
        if (!await db.MigrationJobs.AsNoTracking().AnyAsync(x =>
            x.Id == lease.Id && x.State == MigrationJobStates.Running &&
            x.Attempts == lease.Attempt && x.LeaseToken == lease.LeaseToken &&
            x.LeaseUntilUtc > commitTime, ct))
            throw new DiscoveryRefManifestException("DiscoveryLeaseLost");
        await transaction.CommitAsync(ct);
        return Manifest(record);
    }

    public async Task<DiscoveryRefManifest?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var row = await db.DiscoveryRefManifests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == jobId &&
                db.MigrationJobs.Any(job => job.Id == x.JobId &&
                    job.State == MigrationJobStates.Succeeded &&
                    job.Kind == MigrationJobKinds.DiscoverSource), ct);
        return row is null ? null : Manifest(row);
    }

    public async Task<DiscoveryRefChunkList> ListChunksAsync(
        Guid jobId, int after, int take, CancellationToken ct)
    {
        if (after < -1 || take is < 1 or > 50)
            throw new DiscoveryRefManifestException("InvalidRefChunkPage");
        if (await GetAsync(jobId, ct) is null)
            throw new DiscoveryRefManifestException("RefManifestUnavailable");
        var rows = await db.DiscoveryRefChunks.AsNoTracking()
            .Where(x => x.JobId == jobId && x.Index > after)
            .OrderBy(x => x.Index).Take(take + 1).ToListAsync(ct);
        var hasNext = rows.Count > take;
        var selected = rows.Take(take).Select(Descriptor).ToArray();
        return new(jobId, selected, hasNext ? selected[^1].Index : null);
    }

    public async Task<DiscoveryRefChunkContents?> ReadChunkAsync(
        Guid jobId, int index, CancellationToken ct)
    {
        if (index < 0)
            throw new DiscoveryRefManifestException("InvalidRefChunkIndex");
        if (await GetAsync(jobId, ct) is null)
            throw new DiscoveryRefManifestException("RefManifestUnavailable");
        var chunk = await db.DiscoveryRefChunks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == jobId && x.Index == index, ct);
        if (chunk is null) return null;

        // Keyset read bounded to one manifest chunk; the PK index on
        // (job_id, source_id) is used instead of OFFSET scans.
        var rows = await db.DiscoveryRefObservations.FromSqlInterpolated($"""
            SELECT * FROM discovery_ref_observations
            WHERE job_id = {jobId}
              AND source_id >= {chunk.FirstSourceId}
              AND source_id <= {chunk.LastSourceId}
            ORDER BY source_id
            LIMIT {ChunkSize + 1}
            """).AsNoTracking().ToListAsync(ct);

        var summary = rows.Select(x => (x.SourceId, x.Fingerprint)).ToArray();
        if (rows.Count != chunk.AssetCount || rows.Count > ChunkSize ||
            rows.Count == 0 || rows[0].SourceId != chunk.FirstSourceId ||
            rows[^1].SourceId != chunk.LastSourceId ||
            HashChunk(summary) != chunk.Sha256)
            throw new DiscoveryRefManifestException("RefManifestIntegrityFailed");

        return new(jobId, Descriptor(chunk), rows.Select(x => new DiscoveryRefChunkAsset(
            x.SourceId, x.RepositoryId, x.Fingerprint,
            JsonSerializer.Deserialize<JsonElement>(x.PayloadJson))).ToArray());
    }

    private static DiscoveryRefChunkRecord Chunk(
        Guid jobId, int index, List<(string SourceId, string Fingerprint)> rows) =>
        new()
        {
            JobId = jobId, Index = index, AssetCount = rows.Count,
            FirstSourceId = rows[0].SourceId, LastSourceId = rows[^1].SourceId,
            Sha256 = HashChunk(rows)
        };

    private static string HashChunk(IEnumerable<(string SourceId, string Fingerprint)> rows)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes("ref-chunk-v1\n"));
        foreach (var (sourceId, fingerprint) in rows)
            hasher.AppendData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { sourceId, fingerprint }) + "\n"));
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static string RootDigest(IEnumerable<string> chunks) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "ref-manifest-v1\n" + string.Join("\n", chunks) + "\n")));

    private static DiscoveryRefManifest Manifest(DiscoveryRefManifestRecord row) =>
        new(row.JobId, row.SourceConnectionId, row.AssetCount, row.ChunkSize,
            row.ChunkCount, row.RootSha256, row.PreparedAtUtc);

    private static DiscoveryRefChunk Descriptor(DiscoveryRefChunkRecord row) =>
        new(row.Index, row.AssetCount, row.FirstSourceId, row.LastSourceId, row.Sha256);
}
