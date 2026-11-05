using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresRefManifestStagingTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Staging_streams_1250_refs_then_reconciles_one_changed_and_one_missing()
    {
        await ResetQueueAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid();
        var snapshot1 = Enumerable.Range(0, 1250)
            .Select(i => Ref(i, "sha-" + i)).ToArray();
        var first = await PrepareAsync(db, clock, source, snapshot1,
            DiscoveryCompleteness.Complete);
        var discoveries = new PostgresDiscoveryRepository(db);
        var slim = await discoveries.GetWithoutRefsAsync(first.Id, default);
        Assert.NotNull(slim);
        Assert.Empty(slim.Refs);
        Assert.Equal(1250, (await discoveries.GetAsync(first.Id, default))!.Refs.Count);

        var manifests = new PostgresDiscoveryRefManifestStore(db, clock);
        var staging = new StagingSyncService(discoveries,
            new PostgresStagingRepository(db, clock, manifests));
        var applied1 = await staging.SyncUsingRefManifestAsync(first, default);
        Assert.Equal(1252, applied1.Observed); // one project, one repository + refs
        Assert.Equal(1252, applied1.Added);
        Assert.Equal(0, applied1.MarkedMissing);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(first, applied1, default));

        var previous = await db.StagingAssets.AsNoTracking().SingleAsync(x =>
            x.SourceConnectionId == source && x.AssetType == "ref" &&
            x.SourceId == StagingAssetNormalizer.NormalizeRef("repo-1",
                "refs/heads/branch-0001", "sha-1").SourceId);
        Assert.Equal(1, previous.Revision);

        clock.Advance(TimeSpan.FromMinutes(1));
        // Remove branch 0001; change branch 0002 OID; other 1,248 remain.
        var snapshot2 = snapshot1.Where(r => r.Name != "refs/heads/branch-0001")
            .Select(r => r.Name == "refs/heads/branch-0002"
                ? new RepositoryRef(r.RepositoryId, r.Name, "changed") : r)
            .ToArray();
        var second = await PrepareAsync(db, clock, source, snapshot2,
            DiscoveryCompleteness.Complete);
        var applied2 = await staging.SyncUsingRefManifestAsync(second, default);
        Assert.Equal(1251, applied2.Observed);
        Assert.Equal(0, applied2.Added);
        Assert.Equal(1, applied2.Changed);
        Assert.Equal(1, applied2.MarkedMissing);
        Assert.Equal(1250, applied2.Unchanged);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(second, applied2, default));

        var missing = await db.StagingAssets.AsNoTracking().SingleAsync(x =>
            x.SourceConnectionId == source && x.AssetType == "ref" &&
            x.SourceId == previous.SourceId);
        Assert.True(missing.Missing);
        Assert.Equal(2, missing.Revision);
        Assert.Equal(second.Id, missing.LastChangedSnapshotId);
        var changed = await db.StagingAssets.AsNoTracking().SingleAsync(x =>
            x.SourceConnectionId == source && x.AssetType == "ref" &&
            x.SourceId == StagingAssetNormalizer.NormalizeRef("repo-1",
                "refs/heads/branch-0002", "changed").SourceId);
        Assert.False(changed.Missing);
        Assert.Equal(2, changed.Revision);
        using var newOid = JsonDocument.Parse(changed.PayloadJson);
        Assert.Equal("changed", newOid.RootElement.GetProperty("ObjectId").GetString());
        Assert.Equal(1250, await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == source && x.AssetType == "ref"));
        Assert.Equal(applied2, await staging.GetSyncAsync(second.Id, default));
        Assert.Equal(applied2, await staging.SyncUsingRefManifestAsync(second, default)); // replay

        // Read all refs from the final committed catalog with bounded keyset
        // pagination. No OFFSET scan and no full ref list required.
        var cursor = (string?)null;
        var total = 0;
        do
        {
            var page = await staging.ListAssetsAsync(source, "ref", cursor, 100, default);
            Assert.True(page.Items.Count <= 100);
            total += page.Items.Count;
            cursor = page.NextAfter;
        } while (cursor is not null);
        Assert.Equal(1250, total);
    }

    [Fact]
    public async Task Incomplete_scan_does_not_mark_older_ref_as_deleted()
    {
        await ResetQueueAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid();
        var initial = await PrepareAsync(db, clock, source,
            [Ref(1, "first"), Ref(2, "second")], DiscoveryCompleteness.Complete);
        var discoveries = new PostgresDiscoveryRepository(db);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var staging = new StagingSyncService(discoveries,
            new PostgresStagingRepository(db, clock, manifest));
        var baseline = await staging.SyncUsingRefManifestAsync(initial, default);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(initial, baseline, default));

        clock.Advance(TimeSpan.FromMinutes(1));
        var partial = await PrepareAsync(db, clock, source,
            [Ref(2, "second")], DiscoveryCompleteness.Partial);
        var result = await staging.SyncUsingRefManifestAsync(partial, default);
        Assert.Equal(0, result.MarkedMissing);
        var untouched = await db.StagingAssets.AsNoTracking().SingleAsync(x =>
            x.SourceConnectionId == source && x.AssetType == "ref" &&
            x.SourceId == StagingAssetNormalizer.NormalizeRef("repo-1",
                "refs/heads/branch-0001", "first").SourceId);
        Assert.False(untouched.Missing);
        Assert.Equal(1, untouched.Revision);
    }

    [Fact]
    public async Task Corrupt_manifest_chunk_rolls_back_non_ref_staging_work()
    {
        await ResetQueueAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid();
        var first = await PrepareAsync(db, clock, source,
            [Ref(1, "first")], DiscoveryCompleteness.Complete);
        var discoveries = new PostgresDiscoveryRepository(db);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var staging = new StagingSyncService(discoveries,
            new PostgresStagingRepository(db, clock, manifest));
        var baseline = await staging.SyncUsingRefManifestAsync(first, default);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(first, baseline, default));

        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await PrepareAsync(db, clock, source,
            [Ref(1, "changed")], DiscoveryCompleteness.Complete);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_ref_observations SET payload_json = {"{\"ObjectId\":\"unexpected\"}"}::jsonb
            WHERE job_id = {second.Id}
            """);

        var error = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            staging.SyncUsingRefManifestAsync(second, default));
        Assert.Equal("RefManifestIntegrityFailed", error.Code);
        Assert.Null(await staging.GetSyncAsync(second.Id, default));
        var unchanged = await db.StagingAssets.AsNoTracking().SingleAsync(x =>
            x.SourceConnectionId == source && x.AssetType == "ref");
        Assert.Equal(1, unchanged.Revision);
        Assert.Equal(first.Id, unchanged.LastSeenSnapshotId);
        using var objectId = JsonDocument.Parse(unchanged.PayloadJson);
        Assert.Equal("first", objectId.RootElement.GetProperty("ObjectId").GetString());
    }

    [Fact]
    public async Task Stale_worker_cannot_publish_snapshot_catalog()
    {
        await ResetQueueAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid();
        var lease = await PrepareAsync(db, clock, source, [Ref(1, "sha")],
            DiscoveryCompleteness.Complete);
        clock.Advance(TimeSpan.FromMinutes(6)); // exceeds the 3 min test lease
        var discoveries = new PostgresDiscoveryRepository(db);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var staging = new StagingSyncService(discoveries,
            new PostgresStagingRepository(db, clock, manifest));
        var error = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            staging.SyncUsingRefManifestAsync(lease, default));
        Assert.Equal("DiscoveryLeaseLost", error.Code);
        Assert.Null(await staging.GetSyncAsync(lease.Id, default));
        Assert.Equal(0, await db.StagingAssets.AsNoTracking()
            .CountAsync(x => x.SourceConnectionId == source));
    }

    private static RepositoryRef Ref(int index, string oid) =>
        new("repo-1", $"refs/heads/branch-{index:0000}", oid);

    private static async Task<MigrationJobLease> PrepareAsync(
        AppDbContext db, ManualClock clock, Guid source, RepositoryRef[] refs,
        DiscoveryCompleteness completeness)
    {
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var queued = await jobs.EnqueueDiscoveryAsync(source, Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(queued.Id, lease.Id);
        var timestamp = clock.GetUtcNow();
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory
        {
            Id = queued.Id, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "test-org", CollectedAtUtc = timestamp,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = queued.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = timestamp, Projects = [new SourceProject("p1", "Project", [
                    new SourceRepository("repo-1", "p1", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = refs, Issues = [], Completeness = completeness,
            IdentitiesComplete = false, PermissionsComplete = false
        }, default);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, queued.Id, default));
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        for (var i = 0; i < refs.Length; i += 1000)
        {
            var json = JsonSerializer.Serialize(new
            {
                value = refs.Skip(i).Take(1000).Select(r => new {
                    name = r.Name, objectId = r.ObjectId
                })
            });
            await pages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
                new DiscoveryPageCheckpoint(queued.Id, "refs:repo-1", i / 1000,
                    new string('a', 64), json,
                    i + 1000 < refs.Length ? "continue" : null, timestamp), default);
        }
        await new PostgresDiscoveryRefManifestStore(db, clock)
            .PrepareAsync(lease, refs.Length, default);
        return lease with { SnapshotId = lease.Id };
    }

    private async Task ResetQueueAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY",
            CancellationToken.None);
    }

    private sealed class ManualClock(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
        public void Advance(TimeSpan duration) => time = time.Add(duration);
    }
}
