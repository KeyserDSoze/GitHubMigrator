using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-013: canonical, credential-free staging assets. No Git pack or attachment bytes in PostgreSQL.
public sealed record StagingAssetInput(
    string AssetType, string SourceId, string? ProjectId, string Fingerprint, string PayloadJson);

public sealed record StagingSyncResult(
    Guid SnapshotId, Guid SourceConnectionId, DateTimeOffset SourceCollectedAtUtc,
    DateTimeOffset AppliedAtUtc, int Observed, int Added, int Changed, int Unchanged,
    int MarkedMissing, bool SourceInventoryComplete);

public sealed record StagingCatalogSummary(
    Guid SourceConnectionId, Guid? LatestSnapshotId, DateTimeOffset? LastSyncAtUtc,
    int ActiveAssets, int MissingAssets, int TotalRevisions);

public sealed record StagingAssetView(
    string AssetType, string SourceId, string? ProjectId, string Fingerprint,
    int Revision, bool Missing, Guid FirstSnapshotId, Guid LastSeenSnapshotId,
    Guid LastChangedSnapshotId, JsonElement Properties);

public sealed record StagingAssetPage(
    Guid SourceConnectionId, string AssetType, IReadOnlyList<StagingAssetView> Items,
    string? NextAfter);

public interface IStagingRepository
{
    Task<StagingSyncResult> ApplyAsync(DiscoveryInventory inventory,
        IReadOnlyList<StagingAssetInput> assets, IReadOnlySet<string> completeTypes,
        CancellationToken ct);
    Task<StagingSyncResult> ApplyWithRefManifestAsync(DiscoveryInventory slimInventory,
        IReadOnlyList<StagingAssetInput> nonRefAssets, IReadOnlySet<string> completeTypes,
        MigrationJobLease lease, CancellationToken ct);
    Task<StagingSyncResult?> GetSyncAsync(Guid snapshotId, CancellationToken ct);
    Task<StagingCatalogSummary> GetSummaryAsync(Guid sourceConnectionId, CancellationToken ct);
    Task<StagingAssetPage> ListAssetsAsync(Guid sourceConnectionId, string assetType,
        string? after, int take, CancellationToken ct);
}

public sealed class StagingSyncInputException(string code) : Exception("Invalid staging sync")
{
    public string Code { get; } = code;
}

public sealed class StagingStaleSnapshotException() : Exception("Cannot apply an older source snapshot");

public static class StagingAssetNormalizer
{
    public const int MaxAssetsPerSnapshot = 200_000;

    // The same canonical representation is used by the final snapshot import
    // and by provisional per-page observations, preventing divergent hashes.
    public static StagingAssetInput NormalizeRef(string repositoryId, string refName, string objectId)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || string.IsNullOrWhiteSpace(refName) ||
            string.IsNullOrWhiteSpace(objectId))
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var sourceId = JsonSerializer.Serialize(new[] { repositoryId, refName });
        if (sourceId.Length > 1024)
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var payload = JsonSerializer.Serialize(new { ObjectId = objectId });
        if (payload.Length > 65_536)
            throw new StagingSyncInputException("StagingAssetTooLarge");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "staging-v1\nref\n" + payload)));
        return new("ref", sourceId, null, fingerprint, payload);
    }

    // Graph identity observations must use exactly the same source identity,
    // payload schema and SHA as finalized canonical F-013 identity assets.
    public static StagingAssetInput NormalizeIdentity(
        string descriptor, string displayName, string? email, string? legacyDescriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor) || descriptor.Length > 1024)
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var payload = JsonSerializer.Serialize(new {
            DisplayName = displayName, Email = email, LegacyDescriptor = legacyDescriptor
        });
        if (payload.Length > 65_536)
            throw new StagingSyncInputException("StagingAssetTooLarge");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "staging-v1\nidentity\n" + payload)));
        return new("identity", descriptor, null, fingerprint, payload);
    }

    // F-019: provisional Graph security groups share the *exact* F-013
    // canonical source identity/payload/fingerprint representation. This is
    // raw membership/ACL evidence, never proof of effective permission.
    public static StagingAssetInput NormalizeGroup(
        string descriptor, string displayName, string? legacyDescriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor) || descriptor.Length > 1024)
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var payload = JsonSerializer.Serialize(new {
            DisplayName = displayName, LegacyDescriptor = legacyDescriptor
        });
        if (payload.Length > 65_536)
            throw new StagingSyncInputException("StagingAssetTooLarge");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "staging-v1\ngroup\n" + payload)));
        return new("group", descriptor, null, fingerprint, payload);
    }

    // Keep the exact F-013 membership natural key and fingerprint for durable
    // checkpoint observations and canonical staging.
    public static StagingAssetInput NormalizeMembership(
        string groupDescriptor, string memberDescriptor)
    {
        if (string.IsNullOrWhiteSpace(groupDescriptor) ||
            string.IsNullOrWhiteSpace(memberDescriptor))
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var sourceId = JsonSerializer.Serialize(new[] { groupDescriptor, memberDescriptor });
        if (sourceId.Length > 1024)
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var payload = JsonSerializer.Serialize(new {
            GroupDescriptor = groupDescriptor, MemberDescriptor = memberDescriptor
        });
        if (payload.Length > 65_536)
            throw new StagingSyncInputException("StagingAssetTooLarge");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "staging-v1\nmembership\n" + payload)));
        return new("membership", sourceId, null, fingerprint, payload);
    }

    // Preserve the exact F-013 natural key and fingerprint for a repository-
    // scoped raw ACE, whether it came from inline or externalized evidence.
    public static StagingAssetInput NormalizeAcl(RepositoryAclEvidence acl)
    {
        if (string.IsNullOrWhiteSpace(acl.RepositoryId) ||
            string.IsNullOrWhiteSpace(acl.SecurityToken) ||
            string.IsNullOrWhiteSpace(acl.SecurityDescriptor) ||
            acl.Scope is not ("project" or "repository"))
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var sourceId = JsonSerializer.Serialize(new[] { acl.RepositoryId,
            acl.SecurityToken, acl.SecurityDescriptor, acl.Scope });
        if (sourceId.Length > 1024)
            throw new StagingSyncInputException("InvalidStagingAssetId");
        var payload = JsonSerializer.Serialize(new {
            acl.AllowMask, acl.DenyMask, acl.InheritPermissions
        });
        if (payload.Length > 65_536)
            throw new StagingSyncInputException("StagingAssetTooLarge");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "staging-v1\nacl\n" + payload)));
        return new("acl", sourceId, null, fingerprint, payload);
    }

    public static (IReadOnlyList<StagingAssetInput> Assets, IReadOnlySet<string> CompleteTypes)
        Normalize(DiscoveryInventory inventory)
    {
        if (inventory.Id == Guid.Empty || inventory.ConnectionId == Guid.Empty ||
            inventory.Snapshot.SnapshotId != inventory.Id || string.IsNullOrWhiteSpace(inventory.Organization) ||
            string.IsNullOrWhiteSpace(inventory.SourceProvider))
            throw new StagingSyncInputException("InvalidStagingSnapshot");

        var assets = new Dictionary<(string Type, string Id), StagingAssetInput>();
        void Add<T>(string type, string id, string? project, T data)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 1024 ||
                (project?.Length ?? 0) > 512)
                throw new StagingSyncInputException("InvalidStagingAssetId");
            var payload = JsonSerializer.Serialize(data);
            if (payload.Length > 65_536)
                throw new StagingSyncInputException("StagingAssetTooLarge");
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                "staging-v1\n" + type + "\n" + payload)));
            var key = (type, id);
            if (!assets.TryAdd(key, new(type, id, project, fingerprint, payload)))
                throw new StagingSyncInputException("DuplicateStagingAsset");
            if (assets.Count > MaxAssetsPerSnapshot)
                throw new StagingSyncInputException("StagingAssetLimit");
        }

        foreach (var project in inventory.Snapshot.Projects)
        {
            Add("project", project.ProviderId, project.ProviderId, new { project.Name });
            foreach (var repo in project.Repositories)
            {
                if (!string.Equals(repo.ProjectId, project.ProviderId, StringComparison.Ordinal))
                    throw new StagingSyncInputException("RepositoryProjectMismatch");
                Add("repository", repo.ProviderId, repo.ProjectId, new {
                    repo.ProjectId, repo.Name, repo.VersionControl, repo.SourcePath
                });
            }
        }
        foreach (var reference in inventory.Refs)
        {
            var normalized = NormalizeRef(reference.RepositoryId, reference.Name, reference.ObjectId);
            var key = (normalized.AssetType, normalized.SourceId);
            if (!assets.TryAdd(key, normalized))
                throw new StagingSyncInputException("DuplicateStagingAsset");
            if (assets.Count > MaxAssetsPerSnapshot)
                throw new StagingSyncInputException("StagingAssetLimit");
        }
        foreach (var identity in inventory.Snapshot.Identities)
        {
            var asset = NormalizeIdentity(identity.ProviderId, identity.DisplayName,
                identity.Email, identity.LegacyDescriptor);
            if (!assets.TryAdd(("identity", asset.SourceId), asset))
                throw new StagingSyncInputException("DuplicateStagingAsset");
            if (assets.Count > MaxAssetsPerSnapshot)
                throw new StagingSyncInputException("StagingAssetLimit");
        }
        foreach (var group in inventory.Groups)
        {
            var asset = NormalizeGroup(group.Descriptor, group.DisplayName, group.LegacyDescriptor);
            if (!assets.TryAdd(("group", asset.SourceId), asset))
                throw new StagingSyncInputException("DuplicateStagingAsset");
            if (assets.Count > MaxAssetsPerSnapshot)
                throw new StagingSyncInputException("StagingAssetLimit");
        }
        foreach (var edge in inventory.Memberships)
        {
            var asset = NormalizeMembership(edge.GroupDescriptor, edge.MemberDescriptor);
            if (!assets.TryAdd(("membership", asset.SourceId), asset))
                throw new StagingSyncInputException("DuplicateStagingAsset");
            if (assets.Count > MaxAssetsPerSnapshot)
                throw new StagingSyncInputException("StagingAssetLimit");
        }
        foreach (var acl in inventory.AclEvidence)
        {
            var asset = NormalizeAcl(acl);
            if (!assets.TryAdd(("acl", asset.SourceId), asset))
                throw new StagingSyncInputException("DuplicateStagingAsset");
            if (assets.Count > MaxAssetsPerSnapshot)
                throw new StagingSyncInputException("StagingAssetLimit");
        }

        // Never report absent data as deleted when its source coverage was partial.
        var complete = new HashSet<string>(StringComparer.Ordinal);
        if (inventory.Completeness == DiscoveryCompleteness.Complete)
        {
            complete.UnionWith(["project", "repository", "ref"]);
        }
        if (inventory.IdentitiesComplete) complete.Add("identity");
        if (inventory.SecurityCoverage.GroupsComplete) complete.Add("group");
        if (inventory.SecurityCoverage.MembershipsComplete) complete.Add("membership");
        if (inventory.PermissionsComplete && inventory.SecurityCoverage.AclsCollected)
            complete.Add("acl");

        return (assets.Values.OrderBy(x => x.AssetType, StringComparer.Ordinal)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal).ToArray(), complete);
    }
}

public sealed class StagingSyncService(IDiscoveryRepository discoveries, IStagingRepository staging)
{
    public Task<StagingSyncResult?> GetSyncAsync(Guid snapshotId, CancellationToken ct) =>
        staging.GetSyncAsync(snapshotId, ct);

    public Task<StagingCatalogSummary> GetSummaryAsync(Guid connectionId, CancellationToken ct) =>
        staging.GetSummaryAsync(connectionId, ct);

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "project", "repository", "ref", "identity", "group", "membership", "acl"
    };

    public Task<StagingAssetPage> ListAssetsAsync(Guid connectionId, string assetType,
        string? after, int take, CancellationToken ct)
    {
        if (connectionId == Guid.Empty || !SupportedTypes.Contains(assetType))
            throw new StagingSyncInputException("UnsupportedStagingAssetType");
        if (take is < 1 or > 100 || (after?.Length ?? 0) > 1024)
            throw new StagingSyncInputException("InvalidStagingPage");
        return staging.ListAssetsAsync(connectionId, assetType, after, take, ct);
    }

    public async Task<StagingSyncResult> SyncUsingRefManifestAsync(
        MigrationJobLease lease, CancellationToken ct)
    {
        if (lease.Kind != MigrationJobKinds.DiscoverSource ||
            lease.Id == Guid.Empty || lease.Id != lease.SnapshotId ||
            lease.SourceConnectionId == Guid.Empty ||
            discoveries is not IDiscoveryWithoutRefsRepository slim)
            throw new StagingSyncInputException("InvalidManifestSyncContext");
        var inventory = await slim.GetWithoutRefsAsync(lease.Id, ct)
            ?? throw new KeyNotFoundException("SnapshotNotFound");
        if (inventory.Id != lease.Id || inventory.ConnectionId != lease.SourceConnectionId ||
            inventory.Snapshot.SnapshotId != lease.Id || inventory.Refs.Count != 0)
            throw new StagingSyncInputException("InvalidManifestSyncContext");

        // All non-ref types retain existing F-013 canonical normalization;
        // ref rows are read separately, one integrity-checked chunk at a time.
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        return await staging.ApplyWithRefManifestAsync(
            inventory, normalized.Assets, normalized.CompleteTypes, lease, ct);
    }

    public async Task<StagingSyncResult> SyncAsync(Guid snapshotId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty)
            throw new StagingSyncInputException("InvalidStagingSnapshot");
        var inventory = await discoveries.GetAsync(snapshotId, ct)
            ?? throw new KeyNotFoundException("SnapshotNotFound");
        // A metadata-only source snapshot is not an empty repository ref
        // inventory. Legacy manual staging must never silently normalize it
        // as zero refs or persist a false authoritative sync result.
        if (inventory.RefsExternalized || inventory.IdentitiesExternalized ||
            inventory.MembershipsExternalized || inventory.AclsExternalized)
            throw new StagingSyncInputException("ExternalizedSnapshotRequiresManifest");
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        return await staging.ApplyAsync(inventory, normalized.Assets, normalized.CompleteTypes, ct);
    }
}
