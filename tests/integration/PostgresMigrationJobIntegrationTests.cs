using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresMigrationJobIntegrationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Concurrent_enqueues_have_one_job_and_one_audited_queued_event()
    {
        await ResetJobsAsync();
        var source = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var actor = Guid.NewGuid();
        async Task<MigrationJobView> Enqueue()
        {
            await using var db = fixture.NewContext();
            return await new PostgresMigrationJobRepository(db, TimeProvider.System)
                .EnqueueStagingAsync(snapshot, source, actor, default);
        }

        var jobs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Enqueue()));
        Assert.Single(jobs.Select(x => x.Id).Distinct());
        Assert.All(jobs, x => Assert.Equal(MigrationJobStates.Queued, x.State));
        await using var verify = fixture.NewContext();
        Assert.Single(await verify.MigrationJobs.Where(x => x.SnapshotId == snapshot).ToArrayAsync());
        var events = await new PostgresMigrationJobRepository(verify, TimeProvider.System)
            .EventsAsync(jobs[0].Id, default);
        Assert.Equal("Queued", Assert.Single(events).EventType);
    }

    [Fact]
    public async Task Two_workers_claim_distinct_jobs_and_only_current_lease_can_commit()
    {
        await ResetJobsAsync();
        var source = Guid.NewGuid();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        Guid firstId;
        Guid secondId;
        await using (var db = fixture.NewContext())
        {
            var jobs = new PostgresMigrationJobRepository(db, clock);
            firstId = (await jobs.EnqueueStagingAsync(Guid.NewGuid(), source, Guid.NewGuid(), default)).Id;
            secondId = (await jobs.EnqueueStagingAsync(Guid.NewGuid(), source, Guid.NewGuid(), default)).Id;
        }

        async Task<MigrationJobLease?> Claim()
        {
            await using var db = fixture.NewContext();
            return await new PostgresMigrationJobRepository(db, clock)
                .ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default);
        }
        var claims = await Task.WhenAll(Claim(), Claim());
        Assert.All(claims, x => Assert.NotNull(x));
        Assert.Equal(2, claims.Select(x => x!.Id).Distinct().Count());
        Assert.Contains(firstId, claims.Select(x => x!.Id));
        Assert.Contains(secondId, claims.Select(x => x!.Id));

        await using var verify = fixture.NewContext();
        var repository = new PostgresMigrationJobRepository(verify, clock);
        Assert.Null(await repository.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default));
        var lease = claims[0]!;
        var result = Result(lease);
        Assert.False(await repository.CompleteAsync(lease with { LeaseToken = Guid.NewGuid() },
            result, default));
        Assert.True(await repository.CompleteAsync(lease, result, default));
        Assert.False(await repository.CompleteAsync(lease, result, default));
        var saved = await repository.GetAsync(lease.Id, default);
        Assert.Equal(MigrationJobStates.Succeeded, saved!.State);
        Assert.Equal(1, saved.Attempts);
        Assert.Equal(result, saved.Result);
        Assert.Equal(["Queued", "Claimed", "Succeeded"],
            (await repository.EventsAsync(lease.Id, default)).Select(x => x.EventType));
    }

    [Fact]
    public async Task Expired_lease_can_be_reclaimed_and_stale_worker_is_fenced()
    {
        await ResetJobsAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueStagingAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);
        var original = (await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        clock.Advance(TimeSpan.FromMinutes(4));
        var recovered = (await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(original.Id, recovered.Id);
        Assert.Equal(2, recovered.Attempt);
        Assert.False(await jobs.CompleteAsync(original, Result(original), default));
        Assert.False(await jobs.FailAsync(original, "StaleFailure", false, default));
        Assert.True(await jobs.CompleteAsync(recovered, Result(recovered), default));
        Assert.Equal(2, (await jobs.GetAsync(job.Id, default))!.Attempts);
    }

    [Fact]
    public async Task Retriable_failure_waits_with_exponential_backoff_and_exhausts_at_five_attempts()
    {
        await ResetJobsAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueStagingAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var lease = await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default);
            Assert.NotNull(lease);
            Assert.Equal(attempt, lease.Attempt);
            Assert.True(await jobs.FailAsync(lease, "StagingExecutionFailed", false, default));
            var after = (await jobs.GetAsync(job.Id, default))!;
            if (attempt < 5)
            {
                Assert.Equal(MigrationJobStates.Queued, after.State);
                Assert.Null(await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default));
                clock.Advance(TimeSpan.FromMinutes(6));
            }
            else
            {
                Assert.Equal(MigrationJobStates.Failed, after.State);
                Assert.NotNull(after.CompletedAtUtc);
            }
        }
        Assert.Null(await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default));
        var events = await jobs.EventsAsync(job.Id, default);
        Assert.Equal(11, events.Count); // 1 queued, 5 claimed, 4 retry, 1 failed
        Assert.Equal("Failed", events[^1].EventType);
    }

    [Fact]
    public async Task Expired_final_attempt_becomes_failed_and_is_never_reclaimed_again()
    {
        await ResetJobsAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var job = await jobs.EnqueueStagingAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);
        MigrationJobLease? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            last = await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default);
            Assert.NotNull(last);
            clock.Advance(TimeSpan.FromMinutes(4)); // simulate crash, no finalizer
        }
        Assert.Null(await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default));
        var saved = await jobs.GetAsync(job.Id, default);
        Assert.Equal(MigrationJobStates.Failed, saved!.State);
        Assert.Equal("LeaseExpiredAfterMaxAttempts", saved.LastErrorCode);
        Assert.False(await jobs.CompleteAsync(last!, Result(last!), default));
    }

    [Fact]
    public async Task Worker_normalizes_saved_snapshot_and_persists_result_without_provider_access()
    {
        await ResetJobsAsync();
        var source = Guid.NewGuid();
        var snapshot = Snapshot(source);
        await using var db = fixture.NewContext();
        var discovery = new PostgresDiscoveryRepository(db);
        await discovery.SaveAsync(snapshot, default);
        var jobs = new PostgresMigrationJobRepository(db, TimeProvider.System);
        var queued = await jobs.EnqueueStagingAsync(snapshot.Id, source, Guid.NewGuid(), default);
        var processor = new MigrationJobProcessor(jobs,
            new StagingSyncService(discovery, new PostgresStagingRepository(db, TimeProvider.System)));
        Assert.True(await processor.RunOnceAsync(default));
        var saved = await jobs.GetAsync(queued.Id, default);
        Assert.Equal(MigrationJobStates.Succeeded, saved!.State);
        Assert.Equal(3, saved.Result!.Added);
        Assert.Equal(3, await db.StagingAssets.CountAsync(a =>
            a.SourceConnectionId == source));
        Assert.False(await processor.RunOnceAsync(default));
    }

    [Fact]
    public async Task Malformed_error_codes_are_rejected_to_prevent_raw_secret_leakage()
    {
        await ResetJobsAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var db = fixture.NewContext();
        var jobs = new PostgresMigrationJobRepository(db, clock);
        await jobs.EnqueueStagingAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);
        var lease = (await jobs.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        var ex = await Assert.ThrowsAsync<MigrationJobInputException>(() =>
            jobs.FailAsync(lease, "token=TOPSECRET", false, default));
        Assert.Equal("UnsafeJobErrorCode", ex.Code);
        Assert.Equal(MigrationJobStates.Running, (await jobs.GetAsync(lease.Id, default))!.State);
    }

    private async Task ResetJobsAsync()
    {
        // All integration tests share the same ephemeral PostgreSQL database.
        // The queue is intentionally global, so remove leftover test jobs before
        // assuming which job a worker should claim.
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE migration_job_events, migration_jobs RESTART IDENTITY", default);
    }

    private static StagingSyncResult Result(MigrationJobLease lease) =>
        new(lease.SnapshotId, lease.SourceConnectionId, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, 3, 3, 0, 0, 0, false);

    private static DiscoveryInventory Snapshot(Guid source)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new DiscoveryInventory
        {
            Id = id, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [
                    new SourceProject("project", "Project", [
                        new SourceRepository("repo", "project", "Repo", VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            Refs = [new RepositoryRef("repo", "refs/heads/main", "abc")],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
