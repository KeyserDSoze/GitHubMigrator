using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresDiscoveryJobIntegrationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Concurrent_discovery_requests_for_same_source_are_deduplicated_and_audited()
    {
        await ResetQueueAsync();
        var source = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        async Task<MigrationJobView> Queue(Guid key)
        {
            await using var db = fixture.NewContext();
            return await new PostgresMigrationJobRepository(db, TimeProvider.System)
                .EnqueueDiscoveryAsync(source, actor, key, default);
        }
        var jobs = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Queue(i % 2 == 0 ? requestId : Guid.NewGuid())));
        Assert.Single(jobs.Select(x => x.Id).Distinct());
        Assert.All(jobs, x =>
        {
            Assert.Equal(MigrationJobKinds.DiscoverSource, x.Kind);
            Assert.Equal(MigrationJobStates.Queued, x.State);
            Assert.Equal(Guid.Empty, x.SnapshotId);
        });
        await using var db = fixture.NewContext();
        var queued = await db.MigrationJobs.AsNoTracking()
            .Where(x => x.SourceConnectionId == source).ToArrayAsync();
        Assert.Single(queued);
        Assert.Equal("Queued", Assert.Single(await db.MigrationJobEvents
            .Where(x => x.JobId == jobs[0].Id).ToArrayAsync()).EventType);
    }

    [Fact]
    public async Task Crash_after_snapshot_commit_resumes_without_a_second_source_read()
    {
        await ResetQueueAsync();
        var source = Guid.NewGuid();
        var connection = Source(source);
        await using var db = fixture.NewContext();
        var reader = new CountingReader();
        var discoveries = new PostgresDiscoveryRepository(db);
        var service = new DiscoveryService(new FakeConnections(connection),
            new FakeSecrets(), [reader], discoveries);
        var jobs = new PostgresMigrationJobRepository(db, TimeProvider.System);
        var queued = await jobs.EnqueueDiscoveryAsync(source, Guid.NewGuid(), Guid.NewGuid(), default);
        var committed = await service.ScanAsync(source, default, queued.Id);
        Assert.Equal(queued.Id, committed.Id);
        Assert.Equal(1, reader.Calls);
        Assert.Null((await jobs.GetAsync(queued.Id, default))!.Result);

        var staging = new StagingSyncService(discoveries,
            new PostgresStagingRepository(db, TimeProvider.System));
        var processor = new MigrationJobProcessor(jobs, staging, service);
        Assert.True(await processor.RunOnceAsync(default));
        Assert.Equal(1, reader.Calls);
        var finished = (await jobs.GetAsync(queued.Id, default))!;
        Assert.Equal(MigrationJobStates.Succeeded, finished.State);
        Assert.Equal(queued.Id, finished.SnapshotId);
        Assert.Equal(3, finished.Result!.Observed);
        Assert.Equal(3, finished.Result.Added);
        Assert.Equal(["Queued", "Claimed", "SnapshotSaved", "Succeeded"],
            (await jobs.EventsAsync(queued.Id, default)).Select(x => x.EventType));
        Assert.False(await processor.RunOnceAsync(default));
    }

    [Fact]
    public async Task Checkpoint_survives_retry_and_lost_lease_cannot_mutate_newer_attempt()
    {
        await ResetQueueAsync();
        var source = Guid.NewGuid();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var reader = new CountingReader();
        var discoveries = new PostgresDiscoveryRepository(db);
        var service = new DiscoveryService(new FakeConnections(Source(source)),
            new FakeSecrets(), [reader], discoveries);
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var queued = await jobs.EnqueueDiscoveryAsync(source, Guid.NewGuid(), Guid.NewGuid(), default);
        var oldLease = (await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        var saved = await service.ScanAsync(source, default, queued.Id);
        Assert.Equal(queued.Id, saved.Id);
        Assert.True(await jobs.CheckpointSnapshotAsync(oldLease, saved.Id, default));
        Assert.False(await jobs.CheckpointSnapshotAsync(oldLease, saved.Id, default));
        clock.Advance(TimeSpan.FromMinutes(4));
        var newLease = (await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(queued.Id, newLease.Id);
        Assert.Equal(queued.Id, newLease.SnapshotId);
        Assert.Equal(2, newLease.Attempt);
        Assert.False(await jobs.CheckpointSnapshotAsync(oldLease, saved.Id, default));

        var staging = new StagingSyncService(discoveries,
            new PostgresStagingRepository(db, clock));
        // This simulate a worker holding the second lease; the source is already
        // saved and must never be called on replay.
        var replay = await service.ScanAsync(source, default, queued.Id);
        Assert.Equal(saved.Id, replay.Id);
        Assert.Equal(1, reader.Calls);
        var result = await staging.SyncAsync(queued.Id, default);
        Assert.False(await jobs.CompleteAsync(oldLease, result, default));
        Assert.True(await jobs.CompleteAsync(newLease, result, default));
        Assert.Equal(MigrationJobStates.Succeeded,
            (await jobs.GetAsync(queued.Id, default))!.State);
        Assert.Equal(["Queued", "Claimed", "SnapshotSaved", "Claimed", "Succeeded"],
            (await jobs.EventsAsync(queued.Id, default)).Select(x => x.EventType));
    }

    [Fact]
    public async Task Completed_source_scan_can_be_repeated_with_new_request_id()
    {
        await ResetQueueAsync();
        var source = Guid.NewGuid();
        await using var db = fixture.NewContext();
        var repo = new PostgresMigrationJobRepository(db, TimeProvider.System);
        var first = await repo.EnqueueDiscoveryAsync(source, Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await repo.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.True(await repo.CheckpointSnapshotAsync(lease, first.Id, default));
        var result = new StagingSyncResult(first.Id, source, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, false);
        Assert.True(await repo.CompleteAsync(lease, result, default));
        var next = await repo.EnqueueDiscoveryAsync(source, Guid.NewGuid(), Guid.NewGuid(), default);
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(MigrationJobStates.Queued, next.State);
    }

    private async Task ResetQueueAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_acl_observations, discovery_membership_observations, discovery_group_observations, discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY",
            CancellationToken.None);
    }

    private static MigrationConnection Source(Guid id) =>
        new(id, "Source", "azure-devops-services", ConnectionDirection.Source, "az-org",
            "fake-secret", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "Verified", "operator", "read_only");

    private sealed class CountingReader : IDiscoveryReader
    {
        public string ProviderKey => "azure-devops-services";
        public int Calls { get; private set; }
        public Task<DiscoveryInventory> DiscoverAsync(
            Guid sourceId, string org, string token, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("FAKE-TEST-PAT", token);
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DiscoveryInventory
            {
                Id = id, ConnectionId = sourceId, SourceProvider = ProviderKey,
                Organization = org, CollectedAtUtc = now,
                Snapshot = new DiscoverySnapshot
                {
                    SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                    CollectedAt = now, Projects = [new SourceProject("p1", "Project", [
                        new SourceRepository("repo-1", "p1", "Repo", VersionControlKind.Git)])],
                    Identities = [], AccessGrants = []
                },
                Refs = [new("repo-1", "refs/heads/main", "abc")],
                Issues = [], Completeness = DiscoveryCompleteness.Partial,
                IdentitiesComplete = false, PermissionsComplete = false
            });
        }
    }
    private sealed class FakeConnections(MigrationConnection connection) : IConnectionRepository
    {
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<MigrationConnection?>(id == connection.Id ? connection : null);
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MigrationConnection>>([connection]);
        public Task SaveAsync(MigrationConnection value, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
    }
    private sealed class FakeSecrets : IConnectionSecretStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken ct) =>
            Task.FromResult<string?>("FAKE-TEST-PAT");
        public Task SaveAsync(string reference, string token, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string reference, CancellationToken ct) =>
            throw new NotSupportedException();
    }
    private sealed class ManualClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current = current.Add(by);
    }
}
