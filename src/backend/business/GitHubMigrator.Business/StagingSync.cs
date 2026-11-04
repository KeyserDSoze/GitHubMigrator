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
                    repo.Name, repo.VersionControl, repo.SourcePath
                });
            }
        }
        foreach (var reference in inventory.Refs)
        {
            // JSON tuple produces an unambiguous composite identity for each repository/ref.
            var id = JsonSerializer.Serialize(new[] { reference.RepositoryId, reference.Name });
            Add("ref", id, null, new { reference.ObjectId });
        }
        foreach (var identity in inventory.Snapshot.Identities)
            Add("identity", identity.ProviderId, null, new {
                identity.DisplayName, identity.Email, identity.LegacyDescriptor
            });
        foreach (var group in inventory.Groups)
            Add("group", group.Descriptor, null, new { group.DisplayName, group.LegacyDescriptor });
        foreach (var edge in inventory.Memberships)
            Add("membership", JsonSerializer.Serialize(new[] { edge.GroupDescriptor, edge.MemberDescriptor }),
                null, new { edge.GroupDescriptor, edge.MemberDescriptor });
        foreach (var acl in inventory.AclEvidence)
            Add("acl", JsonSerializer.Serialize(new[] { acl.RepositoryId, acl.SecurityToken,
                acl.SecurityDescriptor, acl.Scope }), null,
                new { acl.AllowMask, acl.DenyMask, acl.InheritPermissions });

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

    public async Task<StagingSyncResult> SyncAsync(Guid snapshotId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty)
            throw new StagingSyncInputException("InvalidStagingSnapshot");
        var inventory = await discoveries.GetAsync(snapshotId, ct)
            ?? throw new KeyNotFoundException("SnapshotNotFound");
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        return await staging.ApplyAsync(inventory, normalized.Assets, normalized.CompleteTypes, ct);
    }
}
