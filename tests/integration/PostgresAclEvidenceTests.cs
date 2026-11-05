using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL 17 integration; each test checks immutable, lease-fenced
// sanitized source evidence, never effective Azure/GitHub authorization.
public sealed class PostgresAclEvidenceTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    private const string ProjectToken = "repoV2/project";
    private const string RepoOneToken = "repoV2/project/repo-1";
    private const string RepoTwoToken = "repoV2/project/repo-2";

    [Fact]
    public async Task Raw_522_ACEs_are_atomic_paged_reconciled_and_stage_without_effective_rights()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var aceRows = Enumerable.Range(0, 521)
            .ToDictionary(i => $"vssgp.member-{i:0000}",
                i => (Allow: (long)(i + 2), Deny: (long)0), StringComparer.Ordinal);

        await store.SaveAsync(Context(lease), Page(lease.Id, ProjectToken,
            new Dictionary<string, (long Allow, long Deny)> {
                ["vssgp.parent"] = (4, 2) }, true), default);
        await store.SaveAsync(Context(lease), Page(lease.Id, RepoOneToken,
            aceRows, false), default);
        await store.SaveAsync(Context(lease), Page(lease.Id, RepoTwoToken,
            new Dictionary<string, (long, long)>(), false), default);

        var evidence = (IDiscoveryAclEvidenceStore)store;
        Assert.Equal(new DiscoveryAclProgress(lease.Id, 3, 522),
            await evidence.GetProgressAsync(lease.Id, default));
        var first = await evidence.ListAsync(lease.Id, null, 100, default);
        Assert.Equal(100, first.Items.Count);
        Assert.NotNull(first.NextAfter);
        var second = await evidence.ListAsync(lease.Id, first.NextAfter, 100, default);
        Assert.Equal(100, second.Items.Count);
        Assert.True(string.CompareOrdinal(first.Items[^1].SourceId,
            second.Items[0].SourceId) < 0);

        var acls = new List<RepositoryAclEvidence>();
        foreach (var repo in new[] { "repo-1", "repo-2" })
            acls.Add(new(repo, ProjectToken, "vssgp.parent", 4, 2, true, "project"));
        acls.AddRange(aceRows.Select(row => new RepositoryAclEvidence(
            "repo-1", RepoOneToken, row.Key, row.Value.Allow,
            row.Value.Deny, false, "repository")));
        await SnapshotAsync(db, lease, acls, true);
        var jobs = new PostgresMigrationJobRepository(db, clock);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, lease.Id, default));
        var active = lease with { SnapshotId = lease.Id };
        await evidence.VerifySnapshotAsync(active, default);

        var canonicalPayload = JsonSerializer.Serialize(new {
            AllowMask = 4L, DenyMask = 2L, InheritPermissions = true
        });
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "acl-observation-v1\n" + canonicalPayload)));
        var stored = await db.DiscoveryAclObservations.AsNoTracking()
            .SingleAsync(x => x.JobId == lease.Id &&
                x.SecurityToken == ProjectToken && x.SecurityDescriptor == "vssgp.parent");
        Assert.Equal(expectedSha, stored.Fingerprint);
        Assert.DoesNotContain("Authorization", stored.PayloadJson);

        await store.SaveAsync(Context(lease), Page(lease.Id, RepoOneToken, aceRows, false),
            default);
        Assert.Equal(3, (await jobs.EventsAsync(lease.Id, default))
            .Count(x => x.EventType == "PageSaved"));

        // Existing F-013 staging still imports the source-scoped ACL asset
        // copies. This increment does not change their scope/effective rights.
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(active, 0, default);
        var stage = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest));
        var result = await stage.SyncUsingRefManifestAsync(active, default);
        Assert.Equal(526, result.Observed); // 522 raw ACEs + parent per repo + project + 2 repos
        Assert.Equal(523, await db.StagingAssets.AsNoTracking().CountAsync(x =>
            x.SourceConnectionId == lease.SourceConnectionId && x.AssetType == "acl"));
        Assert.False(result.SourceInventoryComplete);
    }

    [Fact]
    public async Task Same_count_different_allow_mask_is_rejected_before_staging()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(lease.Id, ProjectToken,
            new Dictionary<string, (long, long)> { ["vssgp.parent"] = (4, 2) }, true), default);
        await store.SaveAsync(Context(lease), Page(lease.Id, RepoOneToken,
            new Dictionary<string, (long, long)>(), false), default);
        await store.SaveAsync(Context(lease), Page(lease.Id, RepoTwoToken,
            new Dictionary<string, (long, long)>(), false), default);
        await SnapshotAsync(db, lease, [
            new("repo-1", ProjectToken, "vssgp.parent", 8, 2, true, "project"),
            new("repo-2", ProjectToken, "vssgp.parent", 8, 2, true, "project") ], true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));

        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryAclEvidenceStore)store).VerifySnapshotAsync(
                lease with { SnapshotId = lease.Id }, default));
        Assert.Equal("AclSnapshotMismatch", error.Code);
        Assert.Empty(await db.StagingAssets.AsNoTracking()
            .Where(x => x.SourceConnectionId == lease.SourceConnectionId).ToArrayAsync());
    }

    [Fact]
    public async Task Complete_acl_collection_requires_even_empty_repository_pages()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(Context(lease), Page(lease.Id, ProjectToken,
            new Dictionary<string, (long, long)>(), false), default);
        await SnapshotAsync(db, lease, [], true);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));

        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            ((IDiscoveryAclEvidenceStore)store).VerifySnapshotAsync(
                lease with { SnapshotId = lease.Id }, default));
        Assert.Equal("AclSnapshotMismatch", error.Code);
    }

    [Fact]
    public async Task Invalid_ACE_rolls_back_its_checkpoint_and_all_prior_rows()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var dictionary = Enumerable.Range(0, 270).ToDictionary(
            i => $"user.{i:0000}", i => (object)new {
                descriptor = $"user.{i:0000}", allow = 2L, deny = 0L
            });
        dictionary["user.0269"] = new { descriptor = "user.0269", allow = "bad", deny = "bad" };
        var invalid = RawPage(lease.Id, ProjectToken,
            JsonSerializer.Serialize(new { value = new[] {
                new { token = ProjectToken, inheritPermissions = true,
                    acesDictionary = dictionary } } }));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(Context(lease), invalid, default));
        Assert.Equal("InvalidAclObservation", error.Code);
        Assert.Null(await store.GetAsync(lease.Id, Key(ProjectToken), 0, default));
        Assert.Empty(await db.DiscoveryAclObservations.AsNoTracking()
            .Where(x => x.JobId == lease.Id).ToArrayAsync());
        Assert.DoesNotContain(await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(lease.Id, default), x => x.EventType == "PageSaved");
    }

    [Fact]
    public async Task A_page_cannot_masquerade_as_another_security_token()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var forged = new DiscoveryPageCheckpoint(lease.Id, Key(ProjectToken), 0,
            new string('a', 64),
            JsonSerializer.Serialize(new { value = new[] {
                new { token = RepoOneToken, inheritPermissions = false,
                    acesDictionary = new Dictionary<string, object> {
                        ["user"] = new { descriptor = "user", allow = 2L, deny = 0L }
                    } } } }), null, DateTimeOffset.UtcNow);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(Context(lease), forged, default));
        Assert.Equal("InvalidAclObservation", error.Code);
        Assert.Null(await store.GetAsync(lease.Id, Key(ProjectToken), 0, default));
    }

    [Fact]
    public async Task Old_page_backfills_without_a_second_provider_call_or_audit_event()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        var page = Page(lease.Id, ProjectToken,
            new Dictionary<string, (long, long)> { ["vssgp.parent"] = (4, 0) }, false);
        db.DiscoveryPageCheckpoints.Add(new DiscoveryPageCheckpointRecord {
            JobId = lease.Id, ResourceKey = page.ResourceKey, PageIndex = 0,
            RequestHash = page.RequestHash, SafePayloadJson = page.SafePayloadJson,
            SavedAtUtc = clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        await SnapshotAsync(db, lease, [
            new("repo-1", ProjectToken, "vssgp.parent", 4, 0, false, "project"),
            new("repo-2", ProjectToken, "vssgp.parent", 4, 0, false, "project") ], false);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(lease, lease.Id, default));
        var evidence = (IDiscoveryAclEvidenceStore)new PostgresDiscoveryPageCheckpointStore(db, clock);
        await evidence.VerifySnapshotAsync(lease with { SnapshotId = lease.Id }, default);
        Assert.Equal(new DiscoveryAclProgress(lease.Id, 1, 1),
            await evidence.GetProgressAsync(lease.Id, default));
        Assert.DoesNotContain(await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(lease.Id, default), x => x.EventType == "PageSaved");
    }

    [Fact]
    public async Task Expired_lease_cannot_write_any_ACEs()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var lease = await RunningAsync(db, clock);
        clock.Advance(TimeSpan.FromMinutes(6));
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(Context(lease), Page(lease.Id, ProjectToken,
                new Dictionary<string, (long, long)> { ["user"] = (2, 0) }, false), default));
        Assert.Equal("DiscoveryLeaseLost", error.Code);
        Assert.Empty(await db.DiscoveryAclObservations.AsNoTracking().ToArrayAsync());
        Assert.Null(await store.GetAsync(lease.Id, Key(ProjectToken), 0, default));
    }

    [Fact]
    public async Task Invalid_paging_arguments_cannot_expand_ACE_exposure()
    {
        await ResetAsync();
        await using var db = fixture.NewContext();
        var evidence = (IDiscoveryAclEvidenceStore)new PostgresDiscoveryPageCheckpointStore(
            db, new ManualClock(DateTimeOffset.UtcNow));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            evidence.ListAsync(Guid.NewGuid(), null, 101, default));
        Assert.Equal("InvalidAclObservationPage", error.Code);
    }

    private static string Key(string token) => "security-acl:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..40];

    private static DiscoveryPageCheckpoint RawPage(Guid jobId, string token, string json) =>
        new(jobId, Key(token), 0, new string('a', 64), json, null, DateTimeOffset.UtcNow);

    private static DiscoveryPageCheckpoint Page(Guid jobId, string token,
        IReadOnlyDictionary<string, (long Allow, long Deny)> aces, bool inherit)
    {
        if (aces.Count == 0)
            return RawPage(jobId, token, """{"value":[]}""");
        var dictionary = aces.ToDictionary(x => x.Key,
            x => new { descriptor = x.Key, allow = x.Value.Allow, deny = x.Value.Deny });
        return RawPage(jobId, token,
            JsonSerializer.Serialize(new { value = new[] {
                new { token, inheritPermissions = inherit, acesDictionary = dictionary }
            } }));
    }

    private static async Task SnapshotAsync(AppDbContext db, MigrationJobLease lease,
        IReadOnlyList<RepositoryAclEvidence> acls, bool complete)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = lease.Id, ConnectionId = lease.SourceConnectionId,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = lease.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [
                    new SourceProject("project", "Project", [
                        new SourceRepository("repo-1", "project", "Repo 1", VersionControlKind.Git),
                        new SourceRepository("repo-2", "project", "Repo 2", VersionControlKind.Git)
                    ])
                ], Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = 0,
            AclEvidence = acls, Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false,
            SecurityCoverage = new(false, false, complete, false)
        }, default);
    }

    private static DiscoveryLeaseContext Context(MigrationJobLease lease) =>
        new(lease.Id, lease.LeaseToken, lease.Attempt);

    private static async Task<MigrationJobLease> RunningAsync(AppDbContext db, TimeProvider clock)
    {
        var id = Guid.NewGuid();
        var source = Guid.NewGuid();
        var token = Guid.NewGuid();
        var now = clock.GetUtcNow();
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = id, SourceConnectionId = source, SnapshotId = Guid.Empty,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            Attempts = 1, EnqueuedBy = Guid.NewGuid(),
            IdempotencyKey = "f022-test:" + id, LeaseToken = token,
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

    private sealed class ManualClock(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
        public void Advance(TimeSpan span) => time = time.Add(span);
    }
}
