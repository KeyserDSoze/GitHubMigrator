using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresDiscoveryRefManifestTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Completed_job_exposes_five_immutable_chunks_through_bounded_keyset_reads()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var refs = Enumerable.Range(0, 1_250)
            .Select(i => new RepositoryRef("repo-1",
                $"refs/heads/feature-{i:0000}", $"oid-{i}")).ToArray();
        var (job, lease) = await PrepareJobAsync(db, clock, refs);

        var sourcePages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await sourcePages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
            Page(job.Id, 0, refs.Take(1_000), "next"), default);
        await sourcePages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
            Page(job.Id, 1, refs.Skip(1_000), null), default);
        var store = new PostgresDiscoveryRefManifestStore(db, clock);
        var first = await store.PrepareAsync(lease, refs.Length, default);
        Assert.Equal(job.Id, first.JobId);
        Assert.Equal(1_250, first.AssetCount);
        Assert.Equal(250, first.ChunkSize);
        Assert.Equal(5, first.ChunkCount);
        Assert.Equal(64, first.RootSha256.Length);
        Assert.Null(await store.GetAsync(job.Id, default)); // no pre-success exposure

        var same = await store.PrepareAsync(lease, refs.Length, default);
        Assert.Equal(first.RootSha256, same.RootSha256);
        Assert.Equal(5, await db.DiscoveryRefChunks.CountAsync(x => x.JobId == job.Id));

        var jobs = new PostgresMigrationJobRepository(db, clock);
        Assert.True(await jobs.CompleteAsync(lease, Result(job, refs.Length), default));
        var published = await store.GetAsync(job.Id, default);
        Assert.NotNull(published);
        Assert.Equal(first.RootSha256, published.RootSha256);

        var invalid = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            store.ListChunksAsync(job.Id, -1, 51, default));
        Assert.Equal("InvalidRefChunkPage", invalid.Code);
        var badIndex = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            store.ReadChunkAsync(job.Id, -1, default));
        Assert.Equal("InvalidRefChunkIndex", badIndex.Code);

        var part1 = await store.ListChunksAsync(job.Id, -1, 2, default);
        Assert.Equal(2, part1.Items.Count);
        Assert.Equal(1, part1.NextAfter);
        var part2 = await store.ListChunksAsync(job.Id, part1.NextAfter!.Value, 2, default);
        Assert.Equal(2, part2.Items.Count);
        Assert.Equal(3, part2.NextAfter);
        var last = await store.ListChunksAsync(job.Id, part2.NextAfter!.Value, 2, default);
        Assert.Single(last.Items);
        Assert.Null(last.NextAfter);
        Assert.Equal([0, 1, 2, 3, 4],
            part1.Items.Concat(part2.Items).Concat(last.Items).Select(x => x.Index));

        var allRefs = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 5; index++)
        {
            var chunk = await store.ReadChunkAsync(job.Id, index, default);
            Assert.NotNull(chunk);
            Assert.Equal(250, chunk.Chunk.AssetCount);
            Assert.Equal(250, chunk.Items.Count);
            Assert.Equal(64, chunk.Chunk.Sha256.Length);
            foreach (var item in chunk.Items)
            {
                Assert.True(allRefs.Add(item.SourceId));
                Assert.Equal("repo-1", item.RepositoryId);
                Assert.Equal(64, item.Fingerprint.Length);
                Assert.StartsWith("oid-", item.Properties.GetProperty("ObjectId").GetString());
            }
        }
        Assert.Equal(1_250, allRefs.Count);
        Assert.Null(await store.ReadChunkAsync(job.Id, 5, default));
    }

    [Fact]
    public async Task Externalized_snapshot_with_1250_refs_seals_five_chunks_without_inline_ref_array()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var refs = Enumerable.Range(0, 1250).Select(i =>
            new RepositoryRef("repo-1", $"refs/heads/feature-{i:0000}", $"oid-{i}")).ToArray();
        var (job, lease) = await PrepareJobAsync(db, clock, refs, externalized: true);
        var saved = await new PostgresDiscoveryRepository(db).GetAsync(job.Id, default);
        Assert.NotNull(saved);
        Assert.True(saved.RefsExternalized);
        Assert.Equal(1250, saved.RefCount);
        Assert.Empty(saved.Refs);
        var legacy = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock));
        var refusal = await Assert.ThrowsAsync<StagingSyncInputException>(() =>
            legacy.SyncAsync(job.Id, default));
        Assert.Equal("ExternalizedSnapshotRequiresManifest", refusal.Code);
        Assert.Empty(await db.StagingSnapshotSyncs.AsNoTracking()
            .Where(x => x.SnapshotId == job.Id).ToArrayAsync());
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
            Page(job.Id, 0, refs.Take(1000), "opaque-next"), default);
        await pages.SaveAsync(new(lease.Id, lease.LeaseToken, lease.Attempt),
            Page(job.Id, 1, refs.Skip(1000), null), default);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var prepared = await manifest.PrepareAsync(lease, saved.RefCount, default);
        Assert.Equal(1250, prepared.AssetCount);
        Assert.Equal(5, prepared.ChunkCount);
        Assert.Equal(64, prepared.RootSha256.Length);
        Assert.Null(await manifest.GetAsync(job.Id, default));
        var staged = await new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest))
            .SyncUsingRefManifestAsync(lease with { SnapshotId = lease.Id }, default);
        Assert.Equal(1252, staged.Observed); // project + repository + 1250 refs
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(lease, Result(job, staged.Observed), default));
        Assert.NotNull(await manifest.GetAsync(job.Id, default));
        var lastChunk = await manifest.ReadChunkAsync(job.Id, 4, default);
        Assert.NotNull(lastChunk);
        Assert.Equal(250, lastChunk.Items.Count);
    }

    [Fact]
    public async Task Externalized_snapshot_cannot_seal_if_checkpointed_ref_is_missing()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var refs = new[] { new RepositoryRef("repo-1", "refs/heads/main", "sha") };
        var (job, lease) = await PrepareJobAsync(db, clock, refs, externalized: true);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var error = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            manifest.PrepareAsync(lease, 1, default));
        Assert.Equal("RefManifestSnapshotMismatch", error.Code);
        Assert.Empty(await db.DiscoveryRefManifests.ToArrayAsync());
    }

    [Fact]
    public async Task Missing_observation_rejects_manifest_without_leaking_partial_data()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var refs = new[] { new RepositoryRef("repo", "refs/heads/main", "sha") };
        var (job, lease) = await PrepareJobAsync(db, clock, refs);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var error = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            manifest.PrepareAsync(lease, refs.Length, default));
        Assert.Equal("RefManifestSnapshotMismatch", error.Code);
        Assert.Null(await manifest.GetAsync(job.Id, default));
        Assert.Empty(await db.DiscoveryRefChunks.ToArrayAsync());
        Assert.Empty(await db.DiscoveryRefManifests.ToArrayAsync());
    }

    [Fact]
    public async Task Equal_counts_but_different_object_ids_cannot_be_sealed()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var refs = new[] { new RepositoryRef("repo-1", "refs/heads/main", "original") };
        var (job, lease) = await PrepareJobAsync(db, clock, refs);
        await new PostgresDiscoveryPageCheckpointStore(db, clock).SaveAsync(
            new(lease.Id, lease.LeaseToken, lease.Attempt),
            Page(job.Id, 0,
                [new RepositoryRef("repo-1", "refs/heads/main", "incorrect")], null),
            default);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var error = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            manifest.PrepareAsync(lease, 1, default));
        Assert.Equal("RefManifestSnapshotMismatch", error.Code);
        Assert.Null(await manifest.GetAsync(job.Id, default));
        Assert.Empty(await db.DiscoveryRefManifests.ToArrayAsync());
    }

    [Fact]
    public async Task Expired_lease_cannot_prepare_manifest_even_after_snapshot_commit()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var (job, lease) = await PrepareJobAsync(db, clock, []);
        clock.Advance(TimeSpan.FromMinutes(4)); // original lease was only 3 min
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        var error = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            manifest.PrepareAsync(lease, 0, default));
        Assert.Equal("DiscoveryLeaseLost", error.Code);
        Assert.Null(await manifest.GetAsync(job.Id, default));
    }

    [Fact]
    public async Task Mutated_observation_is_detected_when_reading_a_published_chunk()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var refs = new[] { new RepositoryRef("repo-1", "refs/heads/main", "before") };
        var (job, lease) = await PrepareJobAsync(db, clock, refs);
        await new PostgresDiscoveryPageCheckpointStore(db, clock).SaveAsync(
            new(lease.Id, lease.LeaseToken, lease.Attempt),
            Page(job.Id, 0, refs, null), default);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(lease, 1, default);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(lease, Result(job, 1), default));

        var chunk = await manifest.ReadChunkAsync(job.Id, 0, default);
        Assert.NotNull(chunk);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_ref_observations SET fingerprint = {"tampered"}
            WHERE job_id = {job.Id} AND source_id = {chunk.Items[0].SourceId}
            """);
        var error = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            manifest.ReadChunkAsync(job.Id, 0, default));
        Assert.Equal("RefManifestIntegrityFailed", error.Code);
    }

    [Fact]
    public async Task Changed_payload_without_changing_fingerprint_fails_verified_chunk_read()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var refs = new[] { new RepositoryRef("repo-1", "refs/heads/main", "real") };
        var (job, lease) = await PrepareJobAsync(db, clock, refs);
        await new PostgresDiscoveryPageCheckpointStore(db, clock).SaveAsync(
            new(lease.Id, lease.LeaseToken, lease.Attempt), Page(job.Id, 0, refs, null), default);
        var manifests = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifests.PrepareAsync(lease, 1, default);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(lease, Result(job, 1), default));
        var before = await manifests.ReadChunkAsync(job.Id, 0, default);
        Assert.NotNull(before);
        // Same ID and stored digest, but an unexpected payload now contradicts
        // its fingerprint; hashes of descriptors alone cannot detect this.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_ref_observations SET payload_json = {"{\"ObjectId\":\"fake\"}"}::jsonb
            WHERE job_id = {job.Id}
            """);
        var failure = await Assert.ThrowsAsync<DiscoveryRefManifestException>(() =>
            manifests.ReadChunkAsync(job.Id, 0, default));
        Assert.Equal("RefManifestIntegrityFailed", failure.Code);
    }

    [Fact]
    public async Task Empty_snapshot_has_a_stable_root_and_no_chunks()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var (job, lease) = await PrepareJobAsync(db, clock, []);
        var store = new PostgresDiscoveryRefManifestStore(db, clock);
        var empty = await store.PrepareAsync(lease, 0, default);
        Assert.Equal(0, empty.AssetCount);
        Assert.Equal(0, empty.ChunkCount);
        Assert.Equal(64, empty.RootSha256.Length);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CompleteAsync(lease, Result(job, 0), default));
        var pages = await store.ListChunksAsync(job.Id, -1, 20, default);
        Assert.Empty(pages.Items);
        Assert.Null(pages.NextAfter);
    }

    private static DiscoveryPageCheckpoint Page(Guid jobId, int pageIndex,
        IEnumerable<RepositoryRef> refs, string? continuation) =>
        new(jobId, "refs:repo-1", pageIndex, new string('a', 64),
            JsonSerializer.Serialize(new
            {
                value = refs.Select(r => new { name = r.Name, objectId = r.ObjectId })
            }), continuation, DateTimeOffset.UtcNow);

    private static StagingSyncResult Result(MigrationJobView job, int count) =>
        new(job.Id, job.SourceConnectionId, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, count, count, 0, 0, 0, false);

    private static async Task<(MigrationJobView, MigrationJobLease)> PrepareJobAsync(
        AppDbContext db, TimeProvider clock, RepositoryRef[] refs,
        bool externalized = false)
    {
        var repo = new PostgresMigrationJobRepository(db, clock);
        var source = Guid.NewGuid();
        var job = await repo.EnqueueDiscoveryAsync(source, Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await repo.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, lease.Id);
        var now = clock.GetUtcNow();
        var snapshot = new DiscoveryInventory
        {
            Id = job.Id, ConnectionId = source,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = job.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now,
                Projects = [new SourceProject("p1", "Project", [
                    new SourceRepository("repo-1", "p1", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = externalized ? [] : refs,
            RefsExternalized = externalized,
            RefCount = externalized ? refs.Length : 0,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(snapshot, default);
        Assert.True(await repo.CheckpointSnapshotAsync(lease, job.Id, default));
        return (job, lease);
    }

    private async Task ResetAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY",
            CancellationToken.None);
    }

    private sealed class ManualClock(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
        public void Advance(TimeSpan duration) => time = time.Add(duration);
    }
}
