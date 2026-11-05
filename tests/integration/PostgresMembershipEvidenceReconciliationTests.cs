using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL checks of the immutable snapshot vs. direct Graph membership
// checkpoint pages. These are raw edges, never effective permissions or grants.
public sealed class PostgresMembershipEvidenceReconciliationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    private const string GroupA = "vssgp.group-a";
    private const string GroupB = "vssgp.group-b";

    [Fact]
    public async Task Complete_pages_include_empty_groups_and_deduplicate_replayed_edges()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var first = new DiscoveryMembership(GroupA, "aad.user-1");
        var second = new DiscoveryMembership(GroupA, "aad.user-2");
        var third = new DiscoveryMembership(GroupA, "aad.user-3");
        await store.SaveAsync(Context(lease), Page(job, GroupA, 0, [first, first, second], "next"), default);
        await store.SaveAsync(Context(lease), Page(job, GroupA, 1, [third], null), default);
        await store.SaveAsync(Context(lease), Page(job, GroupB, 0, [], null), default);

        await SaveSnapshotAsync(db, job, source, [GroupA, GroupB], [first, second, third], true);
        await CheckpointAsync(db, clock, lease);
        await ((IDiscoveryMembershipEvidenceVerifier)store).VerifyMembershipsAsync(
            lease with { SnapshotId = job }, default);

        Assert.Equal(3, (await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(job, default)).Count(x => x.EventType == "PageSaved"));
    }

    [Fact]
    public async Task Equal_edge_counts_with_swapped_member_fail_closed()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(job, GroupA, 0,
            [new(GroupA, "aad.original")], null), default);
        await SaveSnapshotAsync(db, job, source, [GroupA],
            [new(GroupA, "aad.replacement")], true);
        await CheckpointAsync(db, clock, lease);

        var ex = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryMembershipEvidenceVerifier)store).VerifyMembershipsAsync(
                lease with { SnapshotId = job }, default));
        Assert.Equal("MembershipSnapshotMismatch", ex.Code);
    }

    [Fact]
    public async Task Complete_graph_requires_a_terminal_page_for_every_group_even_if_empty()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(job, GroupA, 0, [], "next"), default);
        await SaveSnapshotAsync(db, job, source, [GroupA, GroupB], [], true);
        await CheckpointAsync(db, clock, lease);

        var ex = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryMembershipEvidenceVerifier)store).VerifyMembershipsAsync(
                lease with { SnapshotId = job }, default));
        Assert.Equal("MembershipSnapshotMismatch", ex.Code);
    }

    [Fact]
    public async Task A_page_for_an_unlisted_group_fails_even_when_no_edges_exist()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(job, GroupB, 0, [], null), default);
        await SaveSnapshotAsync(db, job, source, [GroupA], [], false);
        await CheckpointAsync(db, clock, lease);

        var ex = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryMembershipEvidenceVerifier)store).VerifyMembershipsAsync(
                lease with { SnapshotId = job }, default));
        Assert.Equal("MembershipSnapshotMismatch", ex.Code);
    }

    [Fact]
    public async Task Partial_membership_collection_can_still_match_its_saved_edges()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var edge = new DiscoveryMembership(GroupA, "aad.user");
        await store.SaveAsync(Context(lease), Page(job, GroupA, 0, [edge], null), default);
        await SaveSnapshotAsync(db, job, source, [GroupA, GroupB], [edge], false);
        await CheckpointAsync(db, clock, lease);

        await ((IDiscoveryMembershipEvidenceVerifier)store).VerifyMembershipsAsync(
            lease with { SnapshotId = job }, default);
    }

    [Fact]
    public async Task Expired_worker_cannot_validate_stale_membership_evidence()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease, source) = await RunningAsync(db, clock);
        await SaveSnapshotAsync(db, job, source, [], [], false);
        await CheckpointAsync(db, clock, lease);
        clock.Advance(TimeSpan.FromMinutes(6));
        var ex = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryMembershipEvidenceVerifier)
                new PostgresDiscoveryPageCheckpointStore(db, clock))
                .VerifyMembershipsAsync(lease with { SnapshotId = job }, default));
        Assert.Equal("DiscoveryLeaseLost", ex.Code);
    }

    private static DiscoveryPageCheckpoint Page(Guid job, string group, int index,
        IEnumerable<DiscoveryMembership> edges, string? next)
    {
        var resource = "security-members:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(group)))[..40];
        return new(job, resource, index, new string('b', 64),
            JsonSerializer.Serialize(new { value = edges.Select(x => new {
                containerDescriptor = x.GroupDescriptor, memberDescriptor = x.MemberDescriptor
            }) }), next, DateTimeOffset.UtcNow);
    }

    private static DiscoveryLeaseContext Context(MigrationJobLease lease) =>
        new(lease.Id, lease.LeaseToken, lease.Attempt);

    private static async Task CheckpointAsync(AppDbContext db, TimeProvider clock,
        MigrationJobLease lease) =>
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));

    private static async Task SaveSnapshotAsync(AppDbContext db, Guid job, Guid source,
        IReadOnlyList<string> groups, IReadOnlyList<DiscoveryMembership> memberships, bool complete)
    {
        var at = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = job, ConnectionId = source, SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = at,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = job, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = at, Projects = [], Identities = [], AccessGrants = []
            },
            Refs = [],
            Groups = groups.Select(x => new DiscoveryGroup(x, x, null)).ToArray(),
            Memberships = memberships,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false,
            SecurityCoverage = new(true, complete, false, false)
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
            IdempotencyKey = "f020-test:" + id,
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
        public void Advance(TimeSpan duration) => time = time.Add(duration);
    }
}
