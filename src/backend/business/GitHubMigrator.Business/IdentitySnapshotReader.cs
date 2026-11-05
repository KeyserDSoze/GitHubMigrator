using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-016: immutable-snapshot-bound identity source catalog. Avoid materializing
// all Graph identities for each mapping preview or update. This is read-only;
// no identity evidence implies effective or target GitHub authorization.
public sealed record IdentitySourceItem(
    string SourceIdentityId, string DisplayName, string? Email, string? LegacyDescriptor);

public sealed record IdentitySourcePage(
    Guid SnapshotId, IReadOnlyList<IdentitySourceItem> Items, string? NextAfter);

public interface IIdentitySnapshotReader
{
    // Returns the immutable discovery metadata with the refs AND identities
    // arrays removed in PostgreSQL before transfer to the application.
    Task<DiscoveryInventory?> GetMetadataAsync(Guid snapshotId, CancellationToken ct);
    Task<int> CountAsync(Guid snapshotId, CancellationToken ct);
    Task<IdentitySourcePage> ListAsync(
        Guid snapshotId, string? after, int take, CancellationToken ct);
    Task<IReadOnlySet<string>> ResolveKnownAsync(
        Guid snapshotId, IReadOnlyList<string> sourceIds, CancellationToken ct);
    Task<IReadOnlyList<string>> ListUnmappedAsync(
        Guid snapshotId, IReadOnlyList<string> mappedIds, int take, CancellationToken ct);
    Task<int> CountUnresolvedAclPrincipalsAsync(Guid snapshotId, CancellationToken ct);
}
