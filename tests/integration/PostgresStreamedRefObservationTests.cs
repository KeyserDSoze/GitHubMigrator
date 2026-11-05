using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresStreamedRefObservationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Large_source_pages_are_persisted_in_bounded_batches_and_replays_are_idempotent()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var first = RefPage(job.Id, 0, Enumerable.Range(0, 1_000)
            .Select(i => ($"refs/heads/branch-{i:0000}", $"oid-{i}")).ToArray(), "next");
        var second = RefPage(job.Id, 1, Enumerable.Range(1_000, 250)
            .Select(i => ($"refs/heads/branch-{i:0000}", $"oid-{i}")).ToArray(), null);
        await store.SaveAsync(lease, first, default);
        Assert.Equal(new(job.Id, 1, 1_000), await store.GetAsync(job.Id, default));
        await store.SaveAsync(lease, second, default);
        Assert.Equal(new(job.Id, 2, 1_250), await store.GetAsync(job.Id, default));
        Assert.Equal(1_250, await db.DiscoveryRefObservations.CountAsync());
        Assert.Equal(0, await db.StagingAssets.CountAsync(x =>
            x.SourceConnectionId == job.SourceConnectionId)); // provisional scans never mutate this source catalog

        var sample = await db.DiscoveryRefObservations.AsNoTracking()
            .SingleAsync(x => x.JobId == job.Id && x.RepositoryId == "repo-1" &&
                x.SourceId.Contains("branch-0420"));
        var expected = StagingAssetNormalizer.NormalizeRef(
            "repo-1", "refs/heads/branch-0420", "oid-420");
        Assert.Equal(expected.SourceId, sample.SourceId);
        Assert.Equal(expected.Fingerprint, sample.Fingerprint);
        using var persistedJson = JsonDocument.Parse(sample.PayloadJson);
        using var canonicalJson = JsonDocument.Parse(expected.PayloadJson);
        Assert.Equal(canonicalJson.RootElement.GetProperty("ObjectId").GetString(),
            persistedJson.RootElement.GetProperty("ObjectId").GetString());

        // Simulates an old cached page re-encountered by a restarted worker.
        await store.SaveAsync(lease, first, default);
        Assert.Equal(new(job.Id, 2, 1_250), await store.GetAsync(job.Id, default));
        var events = await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(job.Id, default);
        Assert.Equal(2, events.Count(x => x.EventType == "PageSaved"));
    }

    [Fact]
    public async Task Changed_ref_across_pages_rolls_back_new_page_and_earlier_SQL_batches()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var page0 = RefPage(job.Id, 0, [("refs/heads/main", "original")], "cursor");
        await store.SaveAsync(lease, page0, default);
        // First 250 rows would be inserted, but the last row conflicts with
        // a ref observed on page 0. Everything from page 1 must roll back.
        var newRefs = Enumerable.Range(0, 299)
            .Select(i => ($"refs/heads/new-{i:0000}", $"sha-{i}"))
            .Append(("refs/heads/main", "changed")).ToArray();
        var inconsistent = RefPage(job.Id, 1, newRefs, null);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(lease, inconsistent, default));
        Assert.Equal("RefObservationConflict", error.Code);
        Assert.Null(await store.GetAsync(job.Id, "refs:repo-1", 1, default));
        Assert.Equal(1, await db.DiscoveryRefObservations.CountAsync());
        Assert.Equal(new(job.Id, 1, 1), await store.GetAsync(job.Id, default));
        var events = await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(job.Id, default);
        Assert.Single(events, x => x.EventType == "PageSaved");
        Assert.Equal("original", JsonDocument.Parse((await db.DiscoveryRefObservations
            .SingleAsync()).PayloadJson).RootElement.GetProperty("ObjectId").GetString());
    }

    [Fact]
    public async Task Lost_worker_cannot_write_observations_and_new_attempt_can_retry()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, old) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(old, RefPage(job.Id, 0,
            [("refs/heads/main", "oid")], "cursor"), default);
        clock.Advance(TimeSpan.FromMinutes(4));
        var retry = (await new PostgresMigrationJobRepository(db, clock)
            .ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, retry.Id);
        var page2 = RefPage(job.Id, 1, [("refs/heads/feature", "sha")], null);
        var thrown = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(old, page2, default));
        Assert.Equal("DiscoveryLeaseLost", thrown.Code);
        Assert.Null(await store.GetAsync(job.Id, "refs:repo-1", 1, default));
        Assert.Equal(new(job.Id, 1, 1), await store.GetAsync(job.Id, default));

        await store.SaveAsync(new(retry.Id, retry.LeaseToken, retry.Attempt), page2, default);
        Assert.Equal(new(job.Id, 2, 2), await store.GetAsync(job.Id, default));
    }

    [Fact]
    public async Task Duplicated_refs_on_the_same_page_fail_without_saving_any_observations()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var page = RefPage(job.Id, 0,
            [("refs/heads/main", "a"), ("refs/heads/main", "b")], null);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(lease, page, default));
        Assert.Equal("DuplicateRefObservation", error.Code);
        Assert.Empty(await db.DiscoveryRefObservations.ToArrayAsync());
        Assert.Null(await store.GetAsync(job.Id, page.ResourceKey, page.PageIndex, default));
    }

    private static DiscoveryPageCheckpoint RefPage(
        Guid job, int index, IReadOnlyList<(string Name, string ObjectId)> refs,
        string? continuation)
    {
        var json = JsonSerializer.Serialize(new {
            value = refs.Select(r => new { name = r.Name, objectId = r.ObjectId })
        });
        return new(job, "refs:repo-1", index, new string('a', 64),
            json, continuation, DateTimeOffset.UtcNow);
    }

    private async Task<(MigrationJobView Job, DiscoveryLeaseContext Lease)> EnqueueAndClaimAsync(TimeProvider clock)
    {
        await using var db = fixture.NewContext();
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueDiscoveryAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, lease.Id);
        return (job, new(lease.Id, lease.LeaseToken, lease.Attempt));
    }

    private async Task ResetAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_membership_observations, discovery_group_observations, discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY",
            CancellationToken.None);
    }

    private sealed class TestClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
        public void Advance(TimeSpan step) => instant = instant.Add(step);
    }
}
