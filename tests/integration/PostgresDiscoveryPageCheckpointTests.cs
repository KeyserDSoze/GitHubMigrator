using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresDiscoveryPageCheckpointTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Immutable_checkpoint_and_event_survive_reopening_context_and_jsonb_reordering()
    {
        await ResetQueueAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid();
        var job = await QueueAsync(source, clock);
        MigrationJobLease lease;
        await using (var db = fixture.NewContext())
        {
            lease = (await new PostgresMigrationJobRepository(db, clock)
                .ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        }
        var page = new DiscoveryPageCheckpoint(job.Id, "refs:repo-1", 0,
            new string('a', 64),
            """{"value":[{"name":"refs/heads/main","objectId":"sha-1"}]}""",
            "opaque+/page2=", clock.GetUtcNow());
        await using (var first = fixture.NewContext())
        {
            var store = new PostgresDiscoveryPageCheckpointStore(first, clock);
            var saved = await store.SaveAsync(
                new DiscoveryLeaseContext(lease.Id, lease.LeaseToken, lease.Attempt), page, default);
            Assert.Equal(page.RequestHash, saved.RequestHash);
            Assert.Equal(page.NextContinuation, saved.NextContinuation);
        }
        await using (var second = fixture.NewContext())
        {
            var store = new PostgresDiscoveryPageCheckpointStore(second, clock);
            var restored = await store.GetAsync(job.Id, "refs:repo-1", 0, default);
            Assert.NotNull(restored);
            Assert.Contains("sha-1", restored.SafePayloadJson);
            // PostgreSQL jsonb can reorder object fields; semantic replay is stable.
            var reordered = page with
            {
                SafePayloadJson = """{"value":[{"objectId":"sha-1","name":"refs/heads/main"}]}"""
            };
            await store.SaveAsync(
                new DiscoveryLeaseContext(lease.Id, lease.LeaseToken, lease.Attempt),
                reordered, default);
            Assert.Single(await second.DiscoveryPageCheckpoints.ToArrayAsync());
            var events = await new PostgresMigrationJobRepository(second, clock)
                .EventsAsync(job.Id, default);
            Assert.Equal(["Queued", "Claimed", "PageSaved"],
                events.Select(x => x.EventType));
        }
    }

    [Fact]
    public async Task Expired_worker_cannot_persist_another_page_after_reclaim()
    {
        await ResetQueueAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var source = Guid.NewGuid();
        var job = await QueueAsync(source, clock);
        await using var db = fixture.NewContext();
        var repo = new PostgresMigrationJobRepository(db, clock);
        var old = (await repo.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var first = new DiscoveryPageCheckpoint(job.Id, "projects", 0,
            new string('b', 64), """{"value":[{"id":"a","name":"A"}]}""",
            null, clock.GetUtcNow());
        await store.SaveAsync(new(old.Id, old.LeaseToken, old.Attempt), first, default);
        clock.Advance(TimeSpan.FromMinutes(4));
        var current = (await repo.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(old.Id, current.Id);
        Assert.Equal(2, current.Attempt);
        var second = first with { ResourceKey = "graph-users", PageIndex = 1 };
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(new(old.Id, old.LeaseToken, old.Attempt), second, default));
        Assert.Equal("DiscoveryLeaseLost", error.Code);
        Assert.Null(await store.GetAsync(job.Id, "graph-users", 1, default));
        await store.SaveAsync(new(current.Id, current.LeaseToken, current.Attempt), second, default);
        Assert.NotNull(await store.GetAsync(job.Id, "graph-users", 1, default));
    }

    [Fact]
    public async Task Conflicting_page_fingerprint_cannot_replace_persisted_inventory()
    {
        await ResetQueueAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var job = await QueueAsync(Guid.NewGuid(), clock);
        await using var db = fixture.NewContext();
        var lease = (await new PostgresMigrationJobRepository(db, clock)
            .ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        var ctx = new DiscoveryLeaseContext(lease.Id, lease.LeaseToken, lease.Attempt);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var first = new DiscoveryPageCheckpoint(job.Id, "projects", 0,
            new string('c', 64), """{"value":[{"id":"first"}]}""",
            null, clock.GetUtcNow());
        await store.SaveAsync(ctx, first, default);
        var wrong = first with { RequestHash = new string('d', 64) };
        var ex = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(ctx, wrong, default));
        Assert.Equal("CheckpointMismatch", ex.Code);
        Assert.Equal(first.RequestHash,
            (await store.GetAsync(job.Id, "projects", 0, default))!.RequestHash);
        Assert.Equal(1, await db.DiscoveryPageCheckpoints.CountAsync());
    }

    private async Task<MigrationJobView> QueueAsync(Guid source, TimeProvider clock)
    {
        await using var db = fixture.NewContext();
        return await new PostgresMigrationJobRepository(db, clock)
            .EnqueueDiscoveryAsync(source, Guid.NewGuid(), Guid.NewGuid(), default);
    }

    private async Task ResetQueueAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_acl_observations, discovery_membership_observations, discovery_group_observations, discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY",
            CancellationToken.None);
    }

    private sealed class ManualClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current = current.Add(by);
    }
}
