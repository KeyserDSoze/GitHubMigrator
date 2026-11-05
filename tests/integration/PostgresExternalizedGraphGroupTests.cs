using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresExternalizedGraphGroupTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    private const string Group = "vssgp.group-0042";
    private const string ProjectToken = "repoV2/project";
    private const string RepoToken = "repoV2/project/repo-1";

    [Fact]
    public async Task Group_521_pages_and_direct_membership_stage_using_only_indexed_observations()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var checkpoints = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var groups = Enumerable.Range(0, 521).Select(i =>
            new DiscoveryGroup($"vssgp.group-{i:0000}", $"Graph {i}", $"legacy.{i}")).ToArray();
        await checkpoints.SaveAsync(Context(lease),
            Page(lease.Id, 0, groups.Take(500), "next-page"), default);
        await checkpoints.SaveAsync(Context(lease),
            Page(lease.Id, 1, groups.Skip(500), null), default);
        await checkpoints.SaveAsync(Context(lease),
            MembershipPage(lease.Id, Group, "aad.user"), default);
        var groupStore = (IDiscoveryGroupObservationStore)checkpoints;
        Assert.Equal(new DiscoveryGroupProgress(lease.Id, 2, 521),
            await groupStore.GetProgressAsync(lease.Id, default));

        await SaveSnapshotAsync(db, lease, [], 521, true, true, 1);
        var stored = await new PostgresDiscoveryRepository(db).GetAsync(lease.Id, default);
        Assert.NotNull(stored);
        Assert.True(stored.GroupsExternalized);
        Assert.Empty(stored.Groups);
        Assert.Equal(521, stored.GroupCount);
        Assert.Empty(stored.Memberships);
        var manual = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock));
        Assert.Equal("ExternalizedSnapshotRequiresManifest",
            (await Assert.ThrowsAsync<StagingSyncInputException>(() =>
                manual.SyncAsync(lease.Id, default))).Code);

        var jobs = new PostgresMigrationJobRepository(db, clock);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        await groupStore.VerifySnapshotAsync(active, default);
        await ((IDiscoveryMembershipEvidenceVerifier)checkpoints)
            .VerifyMembershipsAsync(active, default);

        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(active, 0, default);
        var stage = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, null, checkpoints, null, checkpoints));
        var result = await stage.SyncUsingRefManifestAsync(active, default);
        Assert.Equal(522, result.Observed); // 521 group rows and one membership, no inline group
        Assert.Equal(522, result.Added);
        Assert.Equal(521, await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == lease.SourceConnectionId && x.AssetType == "group"));
        var observed = await db.DiscoveryGroupObservations.AsNoTracking()
            .SingleAsync(x => x.JobId == lease.Id && x.SourceId == Group);
        var canonical = StagingAssetNormalizer.NormalizeGroup(
            Group, "Graph 42", "legacy.42");
        Assert.Equal(canonical.Fingerprint, observed.Fingerprint);
        var staged = await db.StagingAssets.AsNoTracking().SingleAsync(x =>
            x.SourceConnectionId == lease.SourceConnectionId &&
            x.AssetType == "group" && x.SourceId == Group);
        Assert.Equal(canonical.Fingerprint, staged.Fingerprint);
        Assert.True(await jobs.CompleteAsync(active, result, default));
        Assert.Equal(result, await stage.GetSyncAsync(lease.Id, default));
        Assert.Equal(3, (await jobs.EventsAsync(lease.Id, default))
            .Count(x => x.EventType == "PageSaved"));
    }

    [Fact]
    public async Task Equal_count_corrupted_group_payload_is_rejected_before_any_catalog_write()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var checkpoints = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await checkpoints.SaveAsync(Context(lease),
            Page(lease.Id, 0, [new DiscoveryGroup(Group, "Original", "legacy.42")], null),
            default);
        await SaveSnapshotAsync(db, lease, [], 1, true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var corrupted = JsonSerializer.Serialize(new {
            DisplayName = "Forged", LegacyDescriptor = "legacy.42"
        });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_group_observations
            SET payload_json = {corrupted}::jsonb
            WHERE job_id = {lease.Id} AND source_id = {Group}
            """);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryGroupObservationStore)checkpoints).VerifySnapshotAsync(
                lease with { SnapshotId = lease.Id }, default));
        Assert.Equal("GroupSnapshotMismatch", error.Code);
        Assert.Empty(await db.StagingAssets.AsNoTracking()
            .Where(x => x.SourceConnectionId == lease.SourceConnectionId).ToArrayAsync());
    }

    [Fact]
    public async Task Externalized_group_count_mismatch_is_never_equated_to_zero_inline_groups()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var checkpoints = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await checkpoints.SaveAsync(Context(lease),
            Page(lease.Id, 0, [new DiscoveryGroup(Group, "Original", null)], null),
            default);
        await SaveSnapshotAsync(db, lease, [], 0, true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryGroupObservationStore)checkpoints).VerifySnapshotAsync(
                lease with { SnapshotId = lease.Id }, default));
        Assert.Equal("GroupSnapshotMismatch", error.Code);
    }

    [Fact]
    public async Task Complete_externalized_groups_require_a_terminated_page_chain()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var checkpoints = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await checkpoints.SaveAsync(Context(lease),
            Page(lease.Id, 0, [new DiscoveryGroup(Group, "Original", null)], "missing-page"),
            default);
        await SaveSnapshotAsync(db, lease, [], 1, true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryGroupObservationStore)checkpoints).VerifySnapshotAsync(
                lease with { SnapshotId = lease.Id }, default));
        Assert.Equal("GroupSnapshotMismatch", error.Code);
    }

    [Fact]
    public async Task Identity_mapping_resolves_principals_across_externalized_group_and_ACL_tables()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var checkpoints = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await checkpoints.SaveAsync(Context(lease),
            Page(lease.Id, 0, [new DiscoveryGroup(Group, "Known", "legacy.known")], null),
            default);
        await checkpoints.SaveAsync(Context(lease),
            AclPage(lease.Id, ProjectToken, true), default);
        await checkpoints.SaveAsync(Context(lease),
            AclPage(lease.Id, RepoToken, false), default);
        await SaveSnapshotAsync(db, lease, [
            new SourceProject("project", "Project", [
                new SourceRepository("repo-1", "project", "Repo", VersionControlKind.Git)
            ])
        ], 1, true, false, 0, true, 2);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        await ((IDiscoveryGroupObservationStore)checkpoints)
            .VerifySnapshotAsync(active, default);
        await ((IDiscoveryAclEvidenceStore)checkpoints)
            .VerifySnapshotAsync(active, default);
        var identityReader = new PostgresIdentitySnapshotReader(db);
        Assert.Equal(1, await identityReader.CountUnresolvedAclPrincipalsAsync(
            lease.Id, default)); // known legacy.known + unknown legacy.unmapped
    }

    private static DiscoveryPageCheckpoint Page(Guid jobId, int index,
        IEnumerable<DiscoveryGroup> groups, string? continuation) =>
        new(jobId, "security-groups", index, new string('a', 64),
            JsonSerializer.Serialize(new { value = groups.Select(g => new {
                descriptor = g.Descriptor, displayName = g.DisplayName,
                legacyDescriptor = g.LegacyDescriptor
            }) }), continuation, DateTimeOffset.UtcNow);

    private static DiscoveryPageCheckpoint MembershipPage(Guid job, string group,
        string member)
    {
        var key = "security-members:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(group)))[..40];
        return new(job, key, 0, new string('b', 64),
            JsonSerializer.Serialize(new { value = new[] {
                new { containerDescriptor = group, memberDescriptor = member }
            } }), null, DateTimeOffset.UtcNow);
    }

    private static DiscoveryPageCheckpoint AclPage(Guid job, string token, bool project)
    {
        var key = "security-acl:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..40];
        var payload = project
            ? JsonSerializer.Serialize(new { value = new[] {
                new { token, inheritPermissions = true,
                    acesDictionary = new Dictionary<string, object> {
                        ["legacy.known"] = new { descriptor = "legacy.known", allow = 2, deny = 0 },
                        ["legacy.unmapped"] = new { descriptor = "legacy.unmapped", allow = 4, deny = 0 }
                    }
                } } })
            : """{"value":[]}""";
        return new(job, key, 0, new string('c', 64),
            payload, null, DateTimeOffset.UtcNow);
    }

    private static async Task SaveSnapshotAsync(AppDbContext db, MigrationJobLease lease,
        IReadOnlyList<SourceProject> projects, int groupCount, bool complete,
        bool membershipsExternalized = false, int membershipCount = 0,
        bool aclsExternalized = false, int aclCount = 0)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = lease.Id, ConnectionId = lease.SourceConnectionId,
            SourceProvider = "azure-devops-services", Organization = "az-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = lease.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = projects, Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = 0,
            Groups = [], GroupsExternalized = true, GroupCount = groupCount,
            Memberships = [], MembershipsExternalized = membershipsExternalized,
            MembershipCount = membershipCount,
            AclEvidence = [], AclsExternalized = aclsExternalized, AclCount = aclCount,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false,
            SecurityCoverage = new(complete, false, aclsExternalized, false)
        }, default);
    }

    private static DiscoveryLeaseContext Context(MigrationJobLease lease) =>
        new(lease.Id, lease.LeaseToken, lease.Attempt);

    private static async Task<MigrationJobLease> RunningAsync(
        AppDbContext db, TimeProvider clock)
    {
        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var source = Guid.NewGuid();
        var now = clock.GetUtcNow();
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = id, SourceConnectionId = source, SnapshotId = Guid.Empty,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            Attempts = 1, EnqueuedBy = Guid.NewGuid(),
            IdempotencyKey = "f024-test:" + id, LeaseToken = token,
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

    private sealed class ManualClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan span) => value = value.Add(span);
    }
}
