using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// PostgreSQL 17: digest commits to real F-025 per-ref fingerprints while
// F-013/F-090 remain the source of truth for staging and checkpoints.
public sealed class PostgresGitHubSourceRefChangeManifestTests(
    PostgresDraftFixture fixture) : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Real_journal_streaming_proves_changed_missing_and_restored_refs()
    {
        var source = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-15);
        await using var db = fixture.NewContext();
        var staging = new PostgresStagingRepository(db, TimeProvider.System);
        var reader = new PostgresGitHubSourceRefChangeManifestReader(db, staging);

        async Task<GitHubSourceRefChangeManifest> Stage(
            DateTimeOffset time, IReadOnlyList<RepositoryRef> refs)
        {
            var inventory = Snapshot(source, time, refs);
            var normalized = StagingAssetNormalizer.Normalize(inventory);
            await staging.ApplyAsync(
                inventory, normalized.Assets, normalized.CompleteTypes, default);
            return await reader.ReadAsync(source, inventory.Id, default);
        }

        var initial = await Stage(at, [
            new("repo-1", "refs/heads/main", "oid-1"),
            new("repo-1", "refs/tags/v1", "tag-1")
        ]);
        Assert.Equal(2, initial.Added);
        Assert.Equal(0, initial.Missing);

        var changed = await Stage(at.AddMinutes(1), [
            new("repo-1", "refs/heads/main", "oid-2")
        ]);
        Assert.Equal(1, changed.Changed);
        Assert.Equal(1, changed.Missing);
        Assert.Equal(2, changed.Total);
        Assert.NotEqual(initial.EvidenceSha256, changed.EvidenceSha256);

        var restored = await Stage(at.AddMinutes(2), [
            new("repo-1", "refs/heads/main", "oid-2"),
            new("repo-1", "refs/tags/v1", "tag-1")
        ]);
        Assert.Equal(1, restored.Restored);
        Assert.Equal(0, restored.Missing);

        var none = await Stage(at.AddMinutes(3), [
            new("repo-1", "refs/heads/main", "oid-2"),
            new("repo-1", "refs/tags/v1", "tag-1")
        ]);
        Assert.Equal(0, none.Total);
        Assert.NotEqual(restored.EvidenceSha256, none.EvidenceSha256);
        Assert.Throws<GitHubSourceRefChangeManifestException>(() =>
            GitHubSourceRefChangeManifestIntegrity.Verify(
                initial with { EvidenceSha256 = "bad" }));
    }

    [Fact]
    public async Task Foreign_source_and_missing_journal_cannot_be_certified()
    {
        var source = Guid.NewGuid();
        var inventory = Snapshot(source, DateTimeOffset.UtcNow,
            [new("repo-1", "refs/heads/main", "oid")]);
        await using var db = fixture.NewContext();
        var staging = new PostgresStagingRepository(db, TimeProvider.System);
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        await staging.ApplyAsync(inventory,
            normalized.Assets, normalized.CompleteTypes, default);
        var reader = new PostgresGitHubSourceRefChangeManifestReader(db, staging);

        await Assert.ThrowsAsync<GitHubSourceRefChangeManifestException>(
            () => reader.ReadAsync(
                Guid.NewGuid(), inventory.Id, default));
        var verified = await reader.ReadAsync(source, inventory.Id, default);
        Assert.Equal(1, verified.Added);
    }

    private static DiscoveryInventory Snapshot(Guid connection,
        DateTimeOffset at, IReadOnlyList<RepositoryRef> refs)
    {
        var id = Guid.NewGuid();
        return new DiscoveryInventory {
            Id = id, ConnectionId = connection,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = at,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = id,
                SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = at,
                Projects = [new SourceProject("proj-1", "Project",
                    [new SourceRepository("repo-1", "proj-1", "Repo",
                        VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = refs, Issues = [],
            Completeness = DiscoveryCompleteness.Complete,
            IdentitiesComplete = false, PermissionsComplete = false
        };
    }
}
