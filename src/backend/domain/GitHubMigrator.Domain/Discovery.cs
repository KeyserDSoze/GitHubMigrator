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
    // A durable source discovery may persist refs exclusively in its F-008
    // source pages and F-014 PostgreSQL observations. A zero-length Refs list
    // then DOES NOT mean the source contained zero refs. Legacy snapshots omit
    // these optional properties and retain the previous inline semantics.
    public bool RefsExternalized { get; init; }
    public int RefCount { get; init; }
    // Only durable, lease-fenced scans can externalize Graph users. A zero
    // inline Snapshot.Identities collection is *not* proof of zero source
    // identities when this marker is set. Older snapshots default to false.
    public bool IdentitiesExternalized { get; init; }
    public int IdentityCount { get; init; }
    public required IReadOnlyList<DiscoveryIssue> Issues { get; init; }
    public required DiscoveryCompleteness Completeness { get; init; }
    // Granular coverage flag prevents calling missing Graph scope an empty identity inventory.
    public required bool IdentitiesComplete { get; init; }
    public required bool PermissionsComplete { get; init; }

    // Optional to preserve deserialization of snapshots produced before security discovery.
    public IReadOnlyList<DiscoveryGroup> Groups { get; init; } = [];
    public IReadOnlyList<DiscoveryMembership> Memberships { get; init; } = [];
    // An externalized list is NOT evidence of zero direct group memberships.
    public bool MembershipsExternalized { get; init; }
    public int MembershipCount { get; init; }
    public IReadOnlyList<RepositoryAclEvidence> AclEvidence { get; init; } = [];
    // Leased Graph/Git ACL discovery saves each distinct raw ACE in an
    // indexed, checkpoint-provenanced PostgreSQL observation. When true,
    // empty AclEvidence is not evidence of zero source ACL entries.
    public bool AclsExternalized { get; init; }
    public int AclCount { get; init; }
    public DiscoveryCoverage SecurityCoverage { get; init; } = new(false, false, false, false);
}
