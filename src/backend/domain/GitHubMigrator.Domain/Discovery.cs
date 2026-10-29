namespace GitHubMigrator.Domain;

// Canonical source metadata. No PAT, cookie, authorization header, credential URL, or secret reference.
public sealed record RepositoryRef(string RepositoryId, string Name, string ObjectId);
public sealed record DiscoveryIssue(string Code, string? ProjectId = null, string? RepositoryId = null);
public enum DiscoveryCompleteness { Complete, Partial }

// Raw evidence, NOT an effective-permission decision. Graph descriptors and security ACL
// descriptors live in different namespaces; the legacy descriptor is their optional bridge.
public sealed record DiscoveryGroup(string Descriptor, string DisplayName, string? LegacyDescriptor);
public sealed record DiscoveryMembership(string GroupDescriptor, string MemberDescriptor);
public sealed record RepositoryAclEvidence(
    string RepositoryId, string SecurityToken, string SecurityDescriptor,
    long AllowMask, long DenyMask, bool InheritPermissions, string Scope);
public sealed record DiscoveryCoverage(bool GroupsComplete, bool MembershipsComplete,
    bool AclsCollected, bool EffectivePermissionsVerified);


public sealed class DiscoveryInventory
{
    public required Guid Id { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string SourceProvider { get; init; }
    public required string Organization { get; init; }
    public required DateTimeOffset CollectedAtUtc { get; init; }
    public required DiscoverySnapshot Snapshot { get; init; }
    public required IReadOnlyList<RepositoryRef> Refs { get; init; }
    public required IReadOnlyList<DiscoveryIssue> Issues { get; init; }
    public required DiscoveryCompleteness Completeness { get; init; }
    // Granular coverage flag prevents calling missing Graph scope an empty identity inventory.
    public required bool IdentitiesComplete { get; init; }
    public required bool PermissionsComplete { get; init; }

    // Optional to preserve deserialization of snapshots produced before security discovery.
    public IReadOnlyList<DiscoveryGroup> Groups { get; init; } = [];
    public IReadOnlyList<DiscoveryMembership> Memberships { get; init; } = [];
    public IReadOnlyList<RepositoryAclEvidence> AclEvidence { get; init; } = [];
    public DiscoveryCoverage SecurityCoverage { get; init; } = new(false, false, false, false);
}
