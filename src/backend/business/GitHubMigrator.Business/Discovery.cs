using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

public sealed class DiscoveryProviderException(string code) : Exception("Source inventory discovery failed")
{
    public string Code { get; } = code;
}

public interface IDiscoveryReader
{
    string ProviderKey { get; }
    Task<DiscoveryInventory> DiscoverAsync(Guid connectionId, string organization, string accessToken, CancellationToken ct);
}

// A durable source reader may reuse page-level PostgreSQL checkpoints. The
// ordinary reader interface stays compatible with synchronous discovery and
// adapters that do not support granular resume.
public readonly record struct DiscoveryLeaseContext(Guid JobId, Guid LeaseToken, int Attempt);
public interface IResumableDiscoveryReader : IDiscoveryReader
{
    Task<DiscoveryInventory> DiscoverWithCheckpointsAsync(
        Guid connectionId, string organization, string accessToken,
        DiscoveryLeaseContext lease, CancellationToken ct);
}

// Page data is strictly allowlisted discovery metadata. Opaque continuation
// markers stay in the database, never in job status, events or API responses.
public sealed record DiscoveryPageCheckpoint(
    Guid JobId, string ResourceKey, int PageIndex, string RequestHash,
    string SafePayloadJson, string? NextContinuation, DateTimeOffset SavedAtUtc);

public interface IDiscoveryPageCheckpointStore
{
    Task<DiscoveryPageCheckpoint?> GetAsync(
        Guid jobId, string resourceKey, int pageIndex, CancellationToken ct);
    Task<DiscoveryPageCheckpoint> SaveAsync(
        DiscoveryLeaseContext lease, DiscoveryPageCheckpoint page, CancellationToken ct);
}

// Ref observations may become visible before a complete discovery snapshot.
// They are *provisional* and must never imply deletions, approved migrations
// or committed canonical staging state.
public sealed record DiscoveryRefIngestProgress(
    Guid JobId, int CheckpointedRefPages, int ObservedRefs);

public interface IDiscoveryRefIngestProgress
{
    Task<DiscoveryRefIngestProgress> GetAsync(Guid jobId, CancellationToken ct);
}

// Provisional Azure DevOps Graph user observations. These never authorize
// grants and are not interpreted as complete identity coverage until the
// immutable source snapshot has passed reconciliation and its job succeeded.
public sealed record DiscoveryIdentityProgress(
    Guid JobId, int CheckpointedUserPages, int ObservedUsers);
public sealed record DiscoveryIdentityObservation(
    string SourceId, string Fingerprint, System.Text.Json.JsonElement Properties);
public sealed record DiscoveryIdentityObservationPage(
    Guid JobId, IReadOnlyList<DiscoveryIdentityObservation> Items, string? NextAfter);

public interface IDiscoveryIdentityObservationStore
{
    Task<DiscoveryIdentityProgress> GetProgressAsync(Guid jobId, CancellationToken ct);
    Task<DiscoveryIdentityObservationPage> ListAsync(
        Guid jobId, string? after, int take, CancellationToken ct);
    Task VerifySnapshotAsync(MigrationJobLease lease, CancellationToken ct);
}

// F-019 observations are provisional until a leased source worker verifies
// their agreement with the immutable source snapshot. Graph security group
// descriptors do not grant effective Azure DevOps or GitHub permissions.
public sealed record DiscoveryGroupProgress(
    Guid JobId, int CheckpointedGroupPages, int ObservedGroups);
public sealed record DiscoveryGroupObservation(
    string SourceId, string Fingerprint, System.Text.Json.JsonElement Properties);
public sealed record DiscoveryGroupObservationPage(
    Guid JobId, IReadOnlyList<DiscoveryGroupObservation> Items, string? NextAfter);
public interface IDiscoveryGroupObservationStore
{
    Task<DiscoveryGroupProgress> GetProgressAsync(Guid jobId, CancellationToken ct);
    Task<DiscoveryGroupObservationPage> ListAsync(
        Guid jobId, string? after, int take, CancellationToken ct);
    Task VerifySnapshotAsync(MigrationJobLease lease, CancellationToken ct);
}

// F-020 verifies raw, direct Graph membership edges against immutable
// PostgreSQL page checkpoints. It does not calculate effective permissions.
public interface IDiscoveryMembershipEvidenceVerifier
{
    Task VerifyMembershipsAsync(MigrationJobLease lease, CancellationToken ct);
}

public sealed class DiscoveryCheckpointException(string code)
    : Exception("Invalid or expired discovery page checkpoint")
{
    public string Code { get; } = code;
}

// Lightweight snapshot projection for chunk-based staging: PostgreSQL strips
// the large Git refs JSON array before sending discovery metadata to .NET.
public interface IDiscoveryWithoutRefsRepository
{
    Task<DiscoveryInventory?> GetWithoutRefsAsync(Guid snapshotId, CancellationToken ct);
}

public interface IDiscoveryRepository
{
    Task SaveAsync(DiscoveryInventory inventory, CancellationToken ct);
    Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct);
}

public sealed class DiscoveryService(
    IConnectionRepository connections,
    IConnectionSecretStore credentials,
    IEnumerable<IDiscoveryReader> readers,
    IDiscoveryRepository repository)
{
    private readonly IReadOnlyDictionary<string, IDiscoveryReader> _readers =
        readers.ToDictionary(r => r.ProviderKey, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct) => repository.ListAsync(ct);
    public Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct) => repository.GetAsync(id, ct);

    public async Task<DiscoveryInventory> ScanAsync(
        Guid connectionId, CancellationToken ct, Guid? stableSnapshotId = null,
        DiscoveryLeaseContext? checkpointLease = null)
    {
        var connection = await connections.GetAsync(connectionId, ct)
            ?? throw new KeyNotFoundException("Connection not found");
        if (connection.Direction != ConnectionDirection.Source || !_readers.TryGetValue(connection.ProviderKey, out var reader))
            throw new ArgumentException("Connection is not a supported source.");

        // A durable job uses its own job ID as the immutable snapshot ID. If a worker
        // dies after PostgreSQL commit but before marking its checkpoint, the next
        // attempt can resume with zero additional calls to Azure DevOps.
        if (stableSnapshotId is { } id)
        {
            if (id == Guid.Empty) throw new ArgumentException("Stable snapshot ID is required.");
            var saved = await repository.GetAsync(id, ct);
            if (saved is not null)
            {
                if (saved.ConnectionId != connection.Id ||
                    !string.Equals(saved.SourceProvider, connection.ProviderKey, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(saved.Organization, connection.Organization, StringComparison.Ordinal))
                    throw new InvalidOperationException("Stored snapshot does not match source connection.");
                return saved;
            }
        }
        var token = await credentials.ReadAsync(connection.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token)) throw new DiscoveryProviderException("CredentialMissing");
        // Reader can only access this transient token during the operation, never through the persisted inventory.
        // Checkpoints are enabled only for a leased worker attempt, never for
        // a synchronous request that has no exclusive source-job ownership.
        var inventory = checkpointLease is { } lease && reader is IResumableDiscoveryReader resumable
            ? await resumable.DiscoverWithCheckpointsAsync(
                connection.Id, connection.Organization, token, lease, ct)
            : await reader.DiscoverAsync(connection.Id, connection.Organization, token, ct);
        if (inventory.ConnectionId != connection.Id || inventory.SourceProvider != connection.ProviderKey ||
            inventory.Organization != connection.Organization)
            throw new InvalidOperationException("Source adapter returned an inventory for another connection.");
        if (inventory.RefsExternalized &&
            (checkpointLease is null || stableSnapshotId is null ||
             inventory.Refs.Count != 0 || inventory.RefCount < 0))
            throw new InvalidOperationException("Externalized refs require a durable job-owned source snapshot.");
        if (inventory.IdentitiesExternalized &&
            (checkpointLease is null || stableSnapshotId is null ||
             inventory.Snapshot.Identities.Count != 0 || inventory.IdentityCount < 0 ||
             inventory.IdentityCount > 15_000))
            throw new InvalidOperationException(
                "Externalized identities require a durable job-owned source snapshot.");
        if (stableSnapshotId is { } fixedId)
        {
            var sourceSnapshot = inventory.Snapshot;
            inventory = new DiscoveryInventory
            {
                Id = fixedId, ConnectionId = inventory.ConnectionId,
                SourceProvider = inventory.SourceProvider, Organization = inventory.Organization,
                CollectedAtUtc = inventory.CollectedAtUtc,
                Snapshot = new DiscoverySnapshot
                {
                    SnapshotId = fixedId, SourceSystem = sourceSnapshot.SourceSystem,
                    CollectedAt = sourceSnapshot.CollectedAt,
                    Projects = sourceSnapshot.Projects, Identities = sourceSnapshot.Identities,
                    AccessGrants = sourceSnapshot.AccessGrants,
                    PermissionsComplete = sourceSnapshot.PermissionsComplete
                },
                Refs = inventory.Refs, RefCount = inventory.RefCount,
                RefsExternalized = inventory.RefsExternalized,
                IdentitiesExternalized = inventory.IdentitiesExternalized,
                IdentityCount = inventory.IdentityCount, Issues = inventory.Issues,
                Completeness = inventory.Completeness, IdentitiesComplete = inventory.IdentitiesComplete,
                PermissionsComplete = inventory.PermissionsComplete,
                Groups = inventory.Groups, Memberships = inventory.Memberships,
                AclEvidence = inventory.AclEvidence, SecurityCoverage = inventory.SecurityCoverage
            };
        }
        await repository.SaveAsync(inventory, ct);
        return inventory;
    }
}
