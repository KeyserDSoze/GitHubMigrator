using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresStagingSyncIntegrationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Initial_batch_sync_is_idempotent_and_creates_queryable_assets()
    {
        var source = Guid.NewGuid();
        var inventory = Snapshot(source, DateTimeOffset.UtcNow, "sha-1",
            refs: Enumerable.Range(0, 1_200)
                .Select(i => new RepositoryRef("repo-1", $"refs/heads/{i:0000}", "sha-1")).ToArray());
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        await using (var db = fixture.NewContext())
        {
            var repo = new PostgresStagingRepository(db, TimeProvider.System);
            var first = await repo.ApplyAsync(inventory, normalized.Assets,
                normalized.CompleteTypes, default);
            Assert.Equal(1_202, first.Observed);
            Assert.Equal(1_202, first.Added);
            Assert.Equal(0, first.Changed);
            Assert.Equal(0, first.MarkedMissing);
            var replayed = await repo.ApplyAsync(inventory, normalized.Assets, normalized.CompleteTypes, default);
            Assert.Equal(first, replayed);
        }
        await using (var verify = fixture.NewContext())
        {
            var repo = new PostgresStagingRepository(verify, TimeProvider.System);
            var saved = await repo.GetSyncAsync(inventory.Id, default);
            Assert.NotNull(saved);
            Assert.Equal(1_202, saved.Observed);
            var page1 = await repo.ListAssetsAsync(source, "ref", null, 100, default);
            Assert.Equal(100, page1.Items.Count);
            Assert.NotNull(page1.NextAfter);
            Assert.All(page1.Items, x => Assert.Equal("sha-1",
                x.Properties.GetProperty("ObjectId").GetString()));
            var page2 = await repo.ListAssetsAsync(source, "ref", page1.NextAfter, 100, default);
            Assert.Equal(100, page2.Items.Count);
            Assert.DoesNotContain(page2.Items, x => page1.Items.Any(y => y.SourceId == x.SourceId));
            Assert.True(string.CompareOrdinal(page1.Items[^1].SourceId, page2.Items[0].SourceId) < 0);

            Assert.Equal(1_202, await verify.StagingAssets.CountAsync(
                a => a.SourceConnectionId == source));
            Assert.All(await verify.StagingAssets.Where(a => a.SourceConnectionId == source)
                .Take(5).ToListAsync(), x => Assert.Equal(1, x.Revision));
        }
    }

    [Fact]
    public async Task Only_changed_fingerprint_advances_revision_and_partial_scan_preserves_missing_assets()
    {
        var connection = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-3);
        var first = Snapshot(connection, baseTime, "sha-original",
            refs: [new("repo-1", "refs/heads/main", "sha-original"),
                new("repo-1", "refs/heads/feature", "sha-feature")]);
        var second = Snapshot(connection, baseTime.AddMinutes(1), "sha-updated",
            refs: [new("repo-1", "refs/heads/main", "sha-updated")]);
        var third = Snapshot(connection, baseTime.AddMinutes(2), "sha-updated",
            completeness: DiscoveryCompleteness.Complete,
            refs: [new("repo-1", "refs/heads/main", "sha-updated")]);
        await using var db = fixture.NewContext();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        async Task<StagingSyncResult> Apply(DiscoveryInventory inventory)
        {
            var normalized = StagingAssetNormalizer.Normalize(inventory);
            return await repo.ApplyAsync(inventory, normalized.Assets, normalized.CompleteTypes, default);
        }

        Assert.Equal(4, (await Apply(first)).Added);
        var update = await Apply(second);
        Assert.Equal(0, update.Added);
        Assert.Equal(1, update.Changed);
        Assert.Equal(2, update.Unchanged);
        Assert.Equal(0, update.MarkedMissing);

        // The absent feature ref must survive because the second discovery was partial.
        Assert.Equal(4, await db.StagingAssets.CountAsync(a =>
            a.SourceConnectionId == connection && !a.Missing));
        var completed = await Apply(third);
        Assert.Equal(1, completed.MarkedMissing);
        Assert.Equal(0, completed.Changed);

        var reference = await db.StagingAssets.AsNoTracking().SingleAsync(a =>
            a.SourceConnectionId == connection && a.AssetType == "ref" &&
            a.SourceId.Contains("feature"));
        Assert.True(reference.Missing);
        Assert.Equal(2, reference.Revision);
        var mainRef = await db.StagingAssets.AsNoTracking().SingleAsync(a =>
            a.SourceConnectionId == connection && a.AssetType == "ref" &&
            a.SourceId.Contains("main"));
        Assert.Equal(2, mainRef.Revision);
    }

    [Fact]
    public async Task Older_snapshot_cannot_regress_current_catalog()
    {
        var source = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var newer = Snapshot(source, now, "newest");
        var older = Snapshot(source, now.AddMinutes(-5), "stale");
        await using var db = fixture.NewContext();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        var current = StagingAssetNormalizer.Normalize(newer);
        await repo.ApplyAsync(newer, current.Assets, current.CompleteTypes, default);
        var stale = StagingAssetNormalizer.Normalize(older);
        await Assert.ThrowsAsync<StagingStaleSnapshotException>(() => repo.ApplyAsync(
            older, stale.Assets, stale.CompleteTypes, default));
        var saved = await repo.GetSyncAsync(older.Id, default);
        Assert.Null(saved);
        Assert.Equal(3, await db.StagingAssets.CountAsync(a => a.SourceConnectionId == source));
    }

    [Fact]
    public async Task Independent_source_connections_keep_separate_catalogs()
    {
        var a = Snapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "sha-a");
        var b = Snapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "sha-b");
        await using var db = fixture.NewContext();
        var repo = new PostgresStagingRepository(db, TimeProvider.System);
        foreach (var scan in new[] { a, b })
        {
            var normalized = StagingAssetNormalizer.Normalize(scan);
            await repo.ApplyAsync(scan, normalized.Assets, normalized.CompleteTypes, default);
        }
        Assert.Equal(3, (await repo.GetSummaryAsync(a.ConnectionId, default)).ActiveAssets);
        Assert.Equal(3, (await repo.GetSummaryAsync(b.ConnectionId, default)).ActiveAssets);
        Assert.Equal(a.Id, (await repo.GetSummaryAsync(a.ConnectionId, default)).LatestSnapshotId);
        Assert.Equal(b.Id, (await repo.GetSummaryAsync(b.ConnectionId, default)).LatestSnapshotId);
    }

    [Fact]
    public async Task Failed_second_batch_rolls_back_first_batch_and_release_source_lock_for_retry()
    {
        var source = Guid.NewGuid();
        var inventory = Snapshot(source, DateTimeOffset.UtcNow, "sha");
        var invalid = Enumerable.Range(0, 300).Select(i =>
            new StagingAssetInput("ref", $"source-{i:0000}", null, new string('a', 64),
                i == 275 ? "{bad-json" : "{}")).ToArray();
        await using (var failingDb = fixture.NewContext())
        {
            var repository = new PostgresStagingRepository(failingDb, TimeProvider.System);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                repository.ApplyAsync(inventory, invalid, new HashSet<string>(), default));
        }

        await using (var verify = fixture.NewContext())
        {
            Assert.Equal(0, await verify.StagingAssets.CountAsync(x => x.SourceConnectionId == source));
            Assert.Null(await new PostgresStagingRepository(verify, TimeProvider.System)
                .GetSyncAsync(inventory.Id, default));
        }

        // Recovery with corrected data uses the same snapshot and source advisory lock.
        await using (var recovered = fixture.NewContext())
        {
            var repository = new PostgresStagingRepository(recovered, TimeProvider.System);
            var normalized = StagingAssetNormalizer.Normalize(inventory);
            var result = await repository.ApplyAsync(inventory, normalized.Assets,
                normalized.CompleteTypes, default);
            Assert.Equal(3, result.Added);
        }
    }

    private static DiscoveryInventory Snapshot(Guid connectionId, DateTimeOffset collected,
        string oid, DiscoveryCompleteness completeness = DiscoveryCompleteness.Partial,
        IReadOnlyList<RepositoryRef>? refs = null)
    {
        var id = Guid.NewGuid();
        return new DiscoveryInventory
        {
            Id = id, ConnectionId = connectionId, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = collected,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = collected, Projects = [
                    new SourceProject("proj-1", "Project", [
                        new SourceRepository("repo-1", "proj-1", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = refs ?? [new RepositoryRef("repo-1", "refs/heads/main", oid)],
            Issues = [], Completeness = completeness,
            IdentitiesComplete = false, PermissionsComplete = false
        };
    }
}
