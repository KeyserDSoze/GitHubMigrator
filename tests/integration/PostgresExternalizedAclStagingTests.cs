using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-023 uses real PostgreSQL and the production source ACL checkpoint store
// and staging services. Raw ACEs do not authorize any destination writes.
public sealed class PostgresExternalizedAclStagingTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    private const string Parent = "repoV2/project";
    private const string RepoOne = "repoV2/project/repo-1";
    private const string RepoTwo = "repoV2/project/repo-2";

    [Fact]
    public async Task Leased_521_repository_ACEs_and_parent_ACE_stream_to_bounded_canonical_staging()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(Context(lease), Page(lease.Id, Parent,
            new Dictionary<string, (long Allow, long Deny)> {
                ["vssgp.parent"] = (4, 2) }, true), default);
        var children = Enumerable.Range(0, 521).ToDictionary(
            i => $"vssgp.user-{i:0000}", i => ((long)(i + 2), 0L));
        await pages.SaveAsync(Context(lease), Page(lease.Id, RepoOne, children, false), default);
        await pages.SaveAsync(Context(lease), Page(lease.Id, RepoTwo,
            new Dictionary<string, (long Allow, long Deny)>(), false), default);

        var evidence = (IDiscoveryAclEvidenceStore)pages;
        Assert.Equal(new DiscoveryAclProgress(lease.Id, 3, 522),
            await evidence.GetProgressAsync(lease.Id, default));
        await SaveSnapshotAsync(db, lease, 522, complete: true);
        var stored = (await new PostgresDiscoveryRepository(db)
            .GetAsync(lease.Id, default))!;
        Assert.True(stored.AclsExternalized);
        Assert.Empty(stored.AclEvidence);
        Assert.Equal(522, stored.AclCount);
        var jobs = new PostgresMigrationJobRepository(db, clock);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        await evidence.VerifySnapshotAsync(active, default);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(active, 0, default);

        var service = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, null, null, evidence));
        var refused = await Assert.ThrowsAsync<StagingSyncInputException>(() =>
            service.SyncAsync(lease.Id, default));
        Assert.Equal("ExternalizedSnapshotRequiresManifest", refused.Code);

        var result = await service.SyncUsingRefManifestAsync(active, default);
        Assert.Equal(526, result.Observed); // 521 child ACEs, 2 inherited copies, 3 metadata assets
        Assert.Equal(526, result.Added);
        Assert.Equal(523, await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == lease.SourceConnectionId && x.AssetType == "acl"));

        var parentOne = StagingAssetNormalizer.NormalizeAcl(new RepositoryAclEvidence(
            "repo-1", Parent, "vssgp.parent", 4, 2, true, "project"));
        var parentTwo = StagingAssetNormalizer.NormalizeAcl(new RepositoryAclEvidence(
            "repo-2", Parent, "vssgp.parent", 4, 2, true, "project"));
        Assert.NotEqual(parentOne.SourceId, parentTwo.SourceId);
        Assert.Equal(parentOne.Fingerprint, parentTwo.Fingerprint);
        Assert.Equal(parentOne.Fingerprint, (await db.StagingAssets.AsNoTracking()
            .SingleAsync(x => x.SourceConnectionId == lease.SourceConnectionId &&
                x.AssetType == "acl" && x.SourceId == parentOne.SourceId)).Fingerprint);
        Assert.Equal(parentTwo.Fingerprint, (await db.StagingAssets.AsNoTracking()
            .SingleAsync(x => x.SourceConnectionId == lease.SourceConnectionId &&
                x.AssetType == "acl" && x.SourceId == parentTwo.SourceId)).Fingerprint);
        Assert.False(result.SourceInventoryComplete); // No effective-permissions inference.
        Assert.True(await jobs.CompleteAsync(active, result, default));
        Assert.Equal(result, await service.GetSyncAsync(lease.Id, default));
    }

    [Fact]
    public async Task Externalized_zero_ACEs_preserve_empty_Git_scopes_and_stage_no_fake_acl_assets()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        foreach (var token in new[] { Parent, RepoOne, RepoTwo })
            await pages.SaveAsync(Context(lease), Page(lease.Id, token,
                new Dictionary<string, (long Allow, long Deny)>(), false), default);
        await SaveSnapshotAsync(db, lease, 0, complete: true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        var evidence = (IDiscoveryAclEvidenceStore)pages;
        await evidence.VerifySnapshotAsync(active, default);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(active, 0, default);
        var service = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, null, null, evidence));
        var result = await service.SyncUsingRefManifestAsync(active, default);
        Assert.Equal(3, result.Observed); // 1 project + 2 repositories
        Assert.Equal(0, await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == lease.SourceConnectionId && x.AssetType == "acl"));
    }

    [Fact]
    public async Task Same_count_tampered_normalized_ACE_aborts_staging_without_catalog_side_effects()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(Context(lease), Page(lease.Id, Parent,
            new Dictionary<string, (long Allow, long Deny)> {
                ["vssgp.parent"] = (4, 0) }, false), default);
        await pages.SaveAsync(Context(lease), Page(lease.Id, RepoOne,
            new Dictionary<string, (long Allow, long Deny)>(), false), default);
        await pages.SaveAsync(Context(lease), Page(lease.Id, RepoTwo,
            new Dictionary<string, (long Allow, long Deny)>(), false), default);
        await SaveSnapshotAsync(db, lease, 1, complete: true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        var changed = JsonSerializer.Serialize(new {
            AllowMask = 8L, DenyMask = 0L, InheritPermissions = false
        });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_acl_observations
            SET payload_json = {changed}::jsonb
            WHERE job_id = {lease.Id} AND security_token = {Parent}
            """);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(active, 0, default);
        var service = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, null, null,
                (IDiscoveryAclEvidenceStore)pages));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            service.SyncUsingRefManifestAsync(active, default));
        Assert.True(error.Code is "AclObservationConflict" or "AclSnapshotMismatch");
        Assert.Null(await service.GetSyncAsync(lease.Id, default));
        Assert.Empty(await db.StagingAssets.AsNoTracking().Where(x =>
            x.SourceConnectionId == lease.SourceConnectionId).ToArrayAsync());
    }

    [Fact]
    public async Task Externalized_count_mismatch_rejects_a_previously_immutable_snapshot()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(Context(lease), Page(lease.Id, Parent,
            new Dictionary<string, (long Allow, long Deny)> {
                ["vssgp.parent"] = (2, 0) }, false), default);
        await SaveSnapshotAsync(db, lease, 2, complete: false);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryAclEvidenceStore)pages).VerifySnapshotAsync(
                lease with { SnapshotId = lease.Id }, default));
        Assert.Equal("AclSnapshotMismatch", error.Code);
    }

    private static string Key(string token) => "security-acl:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..40];

    private static DiscoveryPageCheckpoint Page(Guid job, string token,
        IReadOnlyDictionary<string, (long Allow, long Deny)> aces, bool inherit)
    {
        if (aces.Count == 0)
            return new(job, Key(token), 0, new string('a', 64),
                """{"value":[]}""", null, DateTimeOffset.UtcNow);
        var dictionary = aces.ToDictionary(x => x.Key,
            x => new { descriptor = x.Key, allow = x.Value.Allow, deny = x.Value.Deny });
        return new(job, Key(token), 0, new string('a', 64),
            JsonSerializer.Serialize(new { value = new[] {
                new { token, inheritPermissions = inherit, acesDictionary = dictionary }
            } }), null, DateTimeOffset.UtcNow);
    }

    private static async Task SaveSnapshotAsync(AppDbContext db, MigrationJobLease lease,
        int rawAceCount, bool complete)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = lease.Id, ConnectionId = lease.SourceConnectionId,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now, Snapshot = new DiscoverySnapshot {
                SnapshotId = lease.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [
                    new SourceProject("project", "Project", [
                        new SourceRepository("repo-1", "project", "Repo 1", VersionControlKind.Git),
                        new SourceRepository("repo-2", "project", "Repo 2", VersionControlKind.Git)])
                ], Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = 0,
            AclEvidence = [], AclsExternalized = true, AclCount = rawAceCount,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false,
            SecurityCoverage = new(false, false, complete, false)
        }, default);
    }

    private static DiscoveryLeaseContext Context(MigrationJobLease lease) =>
        new(lease.Id, lease.LeaseToken, lease.Attempt);

    private static async Task<MigrationJobLease> RunningAsync(AppDbContext db, TimeProvider clock)
    {
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var token = Guid.NewGuid();
        var source = Guid.NewGuid();
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = id, SourceConnectionId = source, SnapshotId = Guid.Empty,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            Attempts = 1, EnqueuedBy = Guid.NewGuid(),
            IdempotencyKey = "f023-test:" + id, LeaseToken = token,
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

    private sealed class Clock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
    }
}
