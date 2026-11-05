using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresGraphMembershipObservationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    private const string GroupA = "vssgp.team-alpha";
    private const string GroupB = "vssgp.empty-team";

    [Fact]
    public async Task Externalized_memberships_are_paged_reconciled_and_staged_in_250_row_batches()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var edges = Enumerable.Range(0, 521)
            .Select(i => new DiscoveryMembership(GroupA, $"aad.member-{i:0000}")).ToArray();
        await store.SaveAsync(Context(lease),
            Page(lease.Id, GroupA, 0, edges.Take(400).Append(edges[0]), "next"), default);
        await store.SaveAsync(Context(lease),
            Page(lease.Id, GroupA, 1, edges.Skip(400), null), default);
        await store.SaveAsync(Context(lease), Page(lease.Id, GroupB, 0, [], null), default);
        var membershipEvidence = (IDiscoveryMembershipEvidenceVerifier)store;
        Assert.Equal(new DiscoveryMembershipProgress(lease.Id, 3, 521),
            await membershipEvidence.GetProgressAsync(lease.Id, default));
        var canonical = StagingAssetNormalizer.NormalizeMembership(GroupA, "aad.member-0042");
        var observed = await db.DiscoveryMembershipObservations.AsNoTracking()
            .SingleAsync(x => x.JobId == lease.Id && x.SourceId == canonical.SourceId);
        Assert.Equal(canonical.Fingerprint, observed.Fingerprint);
        Assert.Equal(GroupA, observed.GroupDescriptor);

        var first = await membershipEvidence.ListAsync(lease.Id, null, 100, default);
        Assert.Equal(100, first.Items.Count);
        Assert.NotNull(first.NextAfter);
        Assert.Equal("aad.member-0000", first.Items[0].MemberDescriptor);
        var second = await membershipEvidence.ListAsync(lease.Id, first.NextAfter, 100, default);
        Assert.Equal("aad.member-0100", second.Items[0].MemberDescriptor);

        await SnapshotAsync(db, lease, [GroupA, GroupB], 521, true);
        var jobs = new PostgresMigrationJobRepository(db, clock);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        await store.VerifyMembershipsAsync(active, default);
        // Resume of a previously saved page must not duplicate observations
        // or its immutable PageSaved audit event.
        await store.SaveAsync(Context(lease),
            Page(lease.Id, GroupA, 0, edges.Take(400).Append(edges[0]), "next"), default);
        Assert.Equal(3, (await jobs.EventsAsync(lease.Id, default))
            .Count(x => x.EventType == "PageSaved"));

        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(active, 0, default);
        var staging = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, null, store));
        Assert.Equal("ExternalizedSnapshotRequiresManifest",
            (await Assert.ThrowsAsync<StagingSyncInputException>(() =>
                staging.SyncAsync(lease.Id, default))).Code);

        var result = await staging.SyncUsingRefManifestAsync(active, default);
        Assert.Equal(523, result.Observed); // 521 direct edges + 2 group records
        Assert.Equal(523, result.Added);
        Assert.Equal(521, await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == active.SourceConnectionId &&
            x.AssetType == "membership"));
        var staged = await db.StagingAssets.AsNoTracking().SingleAsync(x =>
            x.SourceConnectionId == active.SourceConnectionId &&
            x.AssetType == "membership" && x.SourceId == canonical.SourceId);
        Assert.Equal(observed.Fingerprint, staged.Fingerprint);
        Assert.True(await jobs.CompleteAsync(active, result, default));
        Assert.Equal(result, await staging.GetSyncAsync(lease.Id, default));
    }

    [Fact]
    public async Task Equal_count_member_swap_refuses_canonical_stage()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(lease.Id, GroupA, 0,
            [new(GroupA, "aad.original")], null), default);
        await SnapshotAsync(db, lease, [GroupA], 1, true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        var asset = StagingAssetNormalizer.NormalizeMembership(GroupA, "aad.original");
        var forged = JsonSerializer.Serialize(new {
            GroupDescriptor = GroupA, MemberDescriptor = "aad.forged"
        });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_membership_observations
            SET payload_json = {forged}::jsonb
            WHERE job_id = {lease.Id} AND source_id = {asset.SourceId}
            """);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(active, 0, default);
        var service = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, null, store));
        var ex = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            service.SyncUsingRefManifestAsync(active, default));
        Assert.Equal("MembershipSnapshotMismatch", ex.Code);
        Assert.Null(await service.GetSyncAsync(lease.Id, default));
        Assert.Empty(await db.StagingAssets.Where(x =>
            x.SourceConnectionId == lease.SourceConnectionId).ToArrayAsync());
    }

    [Fact]
    public async Task Expired_lease_rolls_back_checkpoint_and_observation_batch()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        clock.Advance(TimeSpan.FromMinutes(6));
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var ex = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(Context(lease), Page(lease.Id, GroupA, 0,
                [new(GroupA, "aad.member")], null), default));
        Assert.Equal("DiscoveryLeaseLost", ex.Code);
        Assert.Empty(await db.DiscoveryMembershipObservations.AsNoTracking()
            .Where(x => x.JobId == lease.Id).ToArrayAsync());
        Assert.Null(await store.GetAsync(lease.Id, Key(GroupA), 0, default));
    }

    [Fact]
    public async Task Invalid_membership_paging_is_rejected()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db,
            new TestClock(DateTimeOffset.UtcNow));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryMembershipEvidenceVerifier)store)
                .ListAsync(Guid.NewGuid(), null, 101, default));
        Assert.Equal("InvalidMembershipObservationPage", error.Code);
    }

    private static string Key(string group) => "security-members:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(group)))[..40];

    private static DiscoveryPageCheckpoint Page(Guid job, string group, int page,
        IEnumerable<DiscoveryMembership> edges, string? next) =>
        new(job, Key(group), page, new string('c', 64),
            JsonSerializer.Serialize(new { value = edges.Select(x => new {
                containerDescriptor = x.GroupDescriptor, memberDescriptor = x.MemberDescriptor
            }) }), next, DateTimeOffset.UtcNow);

    private static DiscoveryLeaseContext Context(MigrationJobLease lease) =>
        new(lease.Id, lease.LeaseToken, lease.Attempt);

    private static async Task SnapshotAsync(AppDbContext db, MigrationJobLease lease,
        string[] groups, int members, bool complete)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = lease.Id, ConnectionId = lease.SourceConnectionId,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now, Snapshot = new DiscoverySnapshot {
                SnapshotId = lease.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [], Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = 0,
            Groups = groups.Select(g => new DiscoveryGroup(g, g, null)).ToArray(),
            Memberships = [], MembershipsExternalized = true, MembershipCount = members,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false,
            SecurityCoverage = new(true, complete, false, false)
        }, default);
    }

    private static async Task<MigrationJobLease> RunningAsync(AppDbContext db, TimeProvider clock)
    {
        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var source = Guid.NewGuid();
        var now = clock.GetUtcNow();
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = id, SourceConnectionId = source, SnapshotId = Guid.Empty,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            Attempts = 1, EnqueuedBy = Guid.NewGuid(),
            IdempotencyKey = "f021-test:" + id, LeaseToken = token,
            LeaseUntilUtc = now.AddMinutes(5), AvailableAtUtc = now,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        return new(id, Guid.Empty, source, MigrationJobKinds.DiscoverSource, token, 1);
    }

    private async Task ResetAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_acl_observations, discovery_membership_observations, discovery_group_observations, discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY");
    }

    private sealed class TestClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
