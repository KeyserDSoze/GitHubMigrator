using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class StagingAssetNormalizerTests
{
    [Fact]
    public void Same_source_objects_produce_stable_keys_and_hashes_across_snapshots()
    {
        var first = StagingAssetNormalizer.Normalize(Sample("aaaa"));
        var second = StagingAssetNormalizer.Normalize(Sample("aaaa", Guid.NewGuid()));
        Assert.Equal(first.Assets, second.Assets);
        Assert.Contains(first.Assets, x => x.AssetType == "repository" &&
            x.SourceId == "repo-1" && x.ProjectId == "p1");
        Assert.Contains(first.Assets, x => x.AssetType == "ref" && x.SourceId.Contains("repo-1"));
    }

    [Fact]
    public void Changed_ref_object_id_changes_its_hash_but_not_repository_hash()
    {
        var old = StagingAssetNormalizer.Normalize(Sample("before")).Assets;
        var newer = StagingAssetNormalizer.Normalize(Sample("after")).Assets;
        Assert.NotEqual(old.Single(x => x.AssetType == "ref").Fingerprint,
            newer.Single(x => x.AssetType == "ref").Fingerprint);
        Assert.Equal(old.Single(x => x.AssetType == "repository").Fingerprint,
            newer.Single(x => x.AssetType == "repository").Fingerprint);
    }

    [Fact]
    public void Partial_inventory_does_not_authorize_marking_missing_repositories_or_refs()
    {
        var (_, complete) = StagingAssetNormalizer.Normalize(Sample("sha"));
        Assert.DoesNotContain("repository", complete);
        Assert.DoesNotContain("ref", complete);
        Assert.DoesNotContain("identity", complete);
        Assert.DoesNotContain("acl", complete);
    }

    [Fact]
    public void Only_explicitly_complete_categories_are_eligible_for_missing_detection()
    {
        var source = Sample("sha", completeness: DiscoveryCompleteness.Complete, identitiesComplete: true);
        var (_, complete) = StagingAssetNormalizer.Normalize(source);
        Assert.Contains("repository", complete);
        Assert.Contains("ref", complete);
        Assert.Contains("identity", complete);
        Assert.DoesNotContain("acl", complete);
        Assert.DoesNotContain("membership", complete);
    }

    [Fact]
    public void Duplicate_source_keys_are_rejected_instead_of_silently_overwriting()
    {
        var sample = Sample("sha");
        sample = Sample("sha", identityIds: ["same-id", "same-id"]);
        var error = Assert.Throws<StagingSyncInputException>(() => StagingAssetNormalizer.Normalize(sample));
        Assert.Equal("DuplicateStagingAsset", error.Code);
    }

    [Fact]
    public void Graph_user_observation_uses_same_fingerprint_as_finalized_identity_asset()
    {
        var inventory = Sample("sha", identityIds: ["aad.user"]);
        var canonical = StagingAssetNormalizer.Normalize(inventory).Assets
            .Single(x => x.AssetType == "identity");
        var page = StagingAssetNormalizer.NormalizeIdentity(
            "aad.user", "Person", null, null);
        Assert.Equal(canonical, page);
        var changed = StagingAssetNormalizer.NormalizeIdentity(
            "aad.user", "Renamed", null, null);
        Assert.Equal(page.SourceId, changed.SourceId);
        Assert.NotEqual(page.Fingerprint, changed.Fingerprint);
        var invalid = Assert.Throws<StagingSyncInputException>(() =>
            StagingAssetNormalizer.NormalizeIdentity("", "Person", null, null));
        Assert.Equal("InvalidStagingAssetId", invalid.Code);
    }

    [Fact]
    public void Large_asset_batches_are_normalized_deterministically_without_target_calls()
    {
        var refs = Enumerable.Range(0, 3_000)
            .Select(i => new RepositoryRef("repo-1", $"refs/heads/b-{i:0000}", "sha")).ToArray();
        var inventory = Sample("sha", refs: refs);
        var result = StagingAssetNormalizer.Normalize(inventory);
        Assert.Equal(3_002, result.Assets.Count); // one project + one repository + 3,000 refs
        Assert.Equal(result.Assets.Count,
            result.Assets.Select(x => (x.AssetType, x.SourceId)).Distinct().Count());
        Assert.All(result.Assets, asset => Assert.Equal(64, asset.Fingerprint.Length));
    }

    private static DiscoveryInventory Sample(string sha, Guid? snapshotId = null,
        DiscoveryCompleteness completeness = DiscoveryCompleteness.Partial,
        bool identitiesComplete = false, IReadOnlyList<string>? identityIds = null,
        IReadOnlyList<RepositoryRef>? refs = null)
    {
        var id = snapshotId ?? Guid.NewGuid();
        return new DiscoveryInventory
        {
            Id = id, ConnectionId = Guid.NewGuid(), Organization = "az-org",
            SourceProvider = "azure-devops-services", CollectedAtUtc = DateTimeOffset.UtcNow,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = DateTimeOffset.UtcNow,
                Projects = [new SourceProject("p1", "Project", [
                    new SourceRepository("repo-1", "p1", "Code", VersionControlKind.Git)])],
                Identities = (identityIds ?? []).Select(x => new SourceIdentity(x, "Person", null)).ToArray(),
                AccessGrants = [], PermissionsComplete = false
            },
            Refs = refs ?? [new RepositoryRef("repo-1", "refs/heads/main", sha)],
            Issues = [], Completeness = completeness, IdentitiesComplete = identitiesComplete,
            PermissionsComplete = false
        };
    }
}
