using System.Text.Json;

namespace GitHubMigrator.Business;

// Immutable, bounded manifest built from the provisional PostgreSQL ref pages.
// Only completed source-discovery jobs may expose a prepared manifest publicly.
public sealed record DiscoveryRefManifest(
    Guid JobId, Guid SourceConnectionId, int AssetCount, int ChunkSize,
    int ChunkCount, string RootSha256, DateTimeOffset PreparedAtUtc);

public sealed record DiscoveryRefChunk(
    int Index, int AssetCount, string FirstSourceId,
    string LastSourceId, string Sha256);

public sealed record DiscoveryRefChunkList(
    Guid JobId, IReadOnlyList<DiscoveryRefChunk> Items, int? NextAfter);

public sealed record DiscoveryRefChunkAsset(
    string SourceId, string RepositoryId, string Fingerprint, JsonElement Properties);

public sealed record DiscoveryRefChunkContents(
    Guid JobId, DiscoveryRefChunk Chunk, IReadOnlyList<DiscoveryRefChunkAsset> Items);

public interface IDiscoveryRefManifestStore
{
    Task<DiscoveryRefManifest> PrepareAsync(
        MigrationJobLease lease, int expectedRefCount, CancellationToken ct);
    // Internal reads require the live worker lease. Public manifest reads
    // remain forbidden until the parent discovery job succeeds.
    Task<DiscoveryRefManifest> GetPreparedAsync(MigrationJobLease lease, CancellationToken ct);
    Task<DiscoveryRefChunkContents?> ReadPreparedChunkAsync(
        MigrationJobLease lease, int index, CancellationToken ct);
    Task<DiscoveryRefManifest?> GetAsync(Guid jobId, CancellationToken ct);
    Task<DiscoveryRefChunkList> ListChunksAsync(
        Guid jobId, int after, int take, CancellationToken ct);
    Task<DiscoveryRefChunkContents?> ReadChunkAsync(
        Guid jobId, int index, CancellationToken ct);
}

public sealed class DiscoveryRefManifestException(string code)
    : Exception("Ref manifest could not be prepared or verified")
{
    public string Code { get; } = code;
}
