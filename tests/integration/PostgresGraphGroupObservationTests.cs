using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresGraphGroupObservationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Group_pages_ingest_in_250_row_batches_and_verify_source_snapshot()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        var groups = Enumerable.Range(0, 521)
            .Select(i => Group(i, "Name " + i)).ToArray();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(job, 0, groups.Take(500), "next"), default);
        await store.SaveAsync(Context(lease), Page(job, 1, groups.Skip(500), null), default);
        var service = (IDiscoveryGroupObservationStore)store;
        Assert.Equal(new DiscoveryGroupProgress(job, 2, 521),
            await service.GetProgressAsync(job, default));
        var canonical = StagingAssetNormalizer.NormalizeGroup(
            "vssgp.group-0042", "Name 42", "legacy.42");
        var row = await db.DiscoveryGroupObservations.AsNoTracking()
            .SingleAsync(x => x.JobId == job && x.SourceId == canonical.SourceId);
        Assert.Equal(canonical.Fingerprint, row.Fingerprint);
        using (var payload = JsonDocument.Parse(row.PayloadJson))
        {
            Assert.Equal("Name 42", payload.RootElement.GetProperty("DisplayName").GetString());
            Assert.Equal("legacy.42", payload.RootElement.GetProperty("LegacyDescriptor").GetString());
        }
        var first = await service.ListAsync(job, null, 100, default);
        Assert.Equal(100, first.Items.Count);
        Assert.NotNull(first.NextAfter);
        var second = await service.ListAsync(job, first.NextAfter, 100, default);
        Assert.Equal("vssgp.group-0100", second.Items[0].SourceId);

        await SaveSnapshotAsync(db, job, source, groups);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, job, default));
        await service.VerifySnapshotAsync(lease with { SnapshotId = job }, default);
        await store.SaveAsync(Context(lease), Page(job, 0, groups.Take(500), "next"), default);
        Assert.Equal(new DiscoveryGroupProgress(job, 2, 521),
            await service.GetProgressAsync(job, default));
        var events = await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(job, default);
        Assert.Equal(2, events.Count(x => x.EventType == "PageSaved"));
    }

    [Fact]
    public async Task Conflicting_group_across_pages_rolls_back_new_checkpoint_and_partial_batches()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, _) = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(job, 0, [Group(1, "Original")], "next"), default);
        var conflicting = Enumerable.Range(20, 290).Select(i => Group(i, "Name " + i))
            .Append(Group(1, "Changed")).ToArray();
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(Context(lease), Page(job, 1, conflicting, null), default));
        Assert.Equal("GroupObservationConflict", error.Code);
        Assert.Null(await store.GetAsync(job, "security-groups", 1, default));
        Assert.Equal(1, await db.DiscoveryGroupObservations
            .CountAsync(x => x.JobId == job));
        Assert.Equal(new DiscoveryGroupProgress(job, 1, 1),
            await ((IDiscoveryGroupObservationStore)store).GetProgressAsync(job, default));
    }

    [Fact]
    public async Task Equal_counts_with_changed_group_name_fail_snapshot_consistency()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(job, 0,
            [Group(1, "Original")], null), default);
        await SaveSnapshotAsync(db, job, source, [Group(1, "Altered")]);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, job, default));
        var failure = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryGroupObservationStore)store)
                .VerifySnapshotAsync(lease with { SnapshotId = job }, default));
        Assert.Equal("GroupSnapshotMismatch", failure.Code);
    }

    [Fact]
    public async Task Expired_group_page_lease_does_not_create_source_evidence()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, _) = await RunningAsync(db, clock);
        clock.Advance(TimeSpan.FromMinutes(6));
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(Context(lease), Page(job, 0,
                [Group(1, "One")], null), default));
        Assert.Equal("DiscoveryLeaseLost", error.Code);
        Assert.Null(await store.GetAsync(job, "security-groups", 0, default));
        Assert.Empty(await db.DiscoveryGroupObservations.Where(x => x.JobId == job).ToArrayAsync());
    }

    private static DiscoveryGroup Group(int i, string name) =>
        new($"vssgp.group-{i:0000}", name, $"legacy.{i}");

    private static DiscoveryPageCheckpoint Page(Guid id, int number,
        IEnumerable<DiscoveryGroup> groups, string? next) =>
        new(id, "security-groups", number, new string('a', 64),
            JsonSerializer.Serialize(new { value = groups.Select(g => new {
                descriptor = g.Descriptor, displayName = g.DisplayName,
                legacyDescriptor = g.LegacyDescriptor
            }) }), next, DateTimeOffset.UtcNow);

    private static DiscoveryLeaseContext Context(MigrationJobLease lease) =>
        new(lease.Id, lease.LeaseToken, lease.Attempt);

    private static async Task SaveSnapshotAsync(AppDbContext db, Guid job, Guid source,
        IReadOnlyList<DiscoveryGroup> groups)
    {
        var at = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = job, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = at,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = job, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = at, Projects = [], Identities = [], AccessGrants = []
            },
            Refs = [], Groups = groups, Issues = [],
            Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false,
            SecurityCoverage = new(true, false, false, false)
        }, default);
    }

    private static async Task<(Guid Job, MigrationJobLease Lease, Guid Source)> RunningAsync(
        AppDbContext db, TimeProvider clock)
    {
        var id = Guid.NewGuid();
        var source = Guid.NewGuid();
        var token = Guid.NewGuid();
        var now = clock.GetUtcNow();
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = id, SourceConnectionId = source, SnapshotId = Guid.Empty,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            Attempts = 1, EnqueuedBy = Guid.NewGuid(),
            IdempotencyKey = "f019-test:" + id,
            LeaseToken = token, LeaseUntilUtc = now.AddMinutes(5),
            AvailableAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        return (id, new MigrationJobLease(id, Guid.Empty, source,
            MigrationJobKinds.DiscoverSource, token, 1), source);
    }

    private async Task ResetAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_acl_observations, discovery_membership_observations, discovery_group_observations, discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY",
            CancellationToken.None);
    }

    private sealed class TestClock(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
        public void Advance(TimeSpan span) => time = time.Add(span);
    }
}
