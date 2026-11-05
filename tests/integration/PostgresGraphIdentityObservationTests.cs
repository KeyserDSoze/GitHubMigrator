using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresGraphIdentityObservationTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Multi_page_1001_users_are_ingested_in_batches_and_match_canonical_staging_hashes()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var first = Enumerable.Range(0, 1000)
            .Select(i => User(i)).ToArray();
        var last = new[] { User(1000) };
        var page0 = Page(job.Id, 0, first, "opaque-user-cursor");
        var page1 = Page(job.Id, 1, last, null);

        await store.SaveAsync(lease, page0, default);
        Assert.Equal(new(job.Id, 1, 1000), await store.GetProgressAsync(job.Id, default));
        await store.SaveAsync(lease, page1, default);
        Assert.Equal(new(job.Id, 2, 1001), await store.GetProgressAsync(job.Id, default));
        Assert.Equal(1001, await db.DiscoveryIdentityObservations.CountAsync(x => x.JobId == job.Id));
        Assert.DoesNotContain("DO_NOT_STAGE",
            string.Join(" ", (await db.DiscoveryPageCheckpoints
                .Where(x => x.JobId == job.Id)
                .Select(x => x.SafePayloadJson).ToArrayAsync())));
        Assert.Equal(0, await db.StagingAssets.CountAsync(x => x.SourceConnectionId == job.SourceConnectionId));

        var asset = await db.DiscoveryIdentityObservations.AsNoTracking()
            .SingleAsync(x => x.JobId == job.Id && x.SourceId == "aad.user-0042");
        var canonical = StagingAssetNormalizer.NormalizeIdentity("aad.user-0042",
            "Person 42", "user42@example.invalid", "legacy.42");
        Assert.Equal(canonical.Fingerprint, asset.Fingerprint);
        using var persisted = JsonDocument.Parse(asset.PayloadJson);
        Assert.Equal("Person 42", persisted.RootElement.GetProperty("DisplayName").GetString());

        var p1 = await store.ListAsync(job.Id, null, 100, default);
        Assert.Equal(100, p1.Items.Count);
        Assert.NotNull(p1.NextAfter);
        var p2 = await store.ListAsync(job.Id, p1.NextAfter, 100, default);
        Assert.Equal("aad.user-0100", p2.Items[0].SourceId);
        Assert.All(p2.Items, x => Assert.Equal(64, x.Fingerprint.Length));

        // A crash after a committed checkpoint causes no duplicate rows/events.
        await store.SaveAsync(lease, page0, default);
        Assert.Equal(new(job.Id, 2, 1001), await store.GetProgressAsync(job.Id, default));
        var events = await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(job.Id, default);
        Assert.Equal(2, events.Count(x => x.EventType == "PageSaved"));
    }

    [Fact]
    public async Task Externalized_1001_Graph_users_are_staged_in_bounded_batches_and_paged_by_F004()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, pageLease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(pageLease,
            Page(job.Id, 0, Enumerable.Range(0, 1000).Select(User), "cursor"), default);
        await pages.SaveAsync(pageLease, Page(job.Id, 1, [User(1000)], null), default);
        Assert.Equal(new(job.Id, 2, 1001),
            await pages.GetProgressAsync(job.Id, default));

        var now = clock.GetUtcNow();
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = job.Id, ConnectionId = job.SourceConnectionId,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = job.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [
                    new SourceProject("p1", "Project", [
                        new SourceRepository("r1", "p1", "Repo", VersionControlKind.Git)])
                ],
                Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = 0,
            IdentitiesExternalized = true, IdentityCount = 1001,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = true, PermissionsComplete = false
        }, default);
        var stored = await new PostgresDiscoveryRepository(db).GetAsync(job.Id, default);
        Assert.NotNull(stored);
        Assert.Empty(stored.Snapshot.Identities);
        Assert.True(stored.IdentitiesExternalized);
        Assert.Equal(1001, stored.IdentityCount);
        var legacy = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock));
        var refused = await Assert.ThrowsAsync<StagingSyncInputException>(() =>
            legacy.SyncAsync(job.Id, default));
        Assert.Equal("ExternalizedSnapshotRequiresManifest", refused.Code);

        var jobs = new PostgresMigrationJobRepository(db, clock);
        var lease = new MigrationJobLease(job.Id, job.Id, job.SourceConnectionId,
            MigrationJobKinds.DiscoverSource, pageLease.LeaseToken, pageLease.Attempt);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, job.Id, default));
        await pages.VerifySnapshotAsync(lease, default);
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(lease, 0, default);
        var staged = await new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, pages))
            .SyncUsingRefManifestAsync(lease, default);
        Assert.Equal(1003, staged.Observed); // 1001 identities + project + repo
        Assert.Equal(1003, staged.Added);
        Assert.Equal(1001, await db.StagingAssets.AsNoTracking()
            .CountAsync(x => x.SourceConnectionId == job.SourceConnectionId &&
                x.AssetType == "identity"));
        var observed = await db.DiscoveryIdentityObservations.AsNoTracking()
            .SingleAsync(x => x.JobId == job.Id && x.SourceId == "aad.user-0042");
        var canonical = await db.StagingAssets.AsNoTracking()
            .SingleAsync(x => x.SourceConnectionId == job.SourceConnectionId &&
                x.AssetType == "identity" && x.SourceId == "aad.user-0042");
        Assert.Equal(observed.Fingerprint, canonical.Fingerprint);

        Assert.True(await jobs.CompleteAsync(lease, staged, default));
        var identitySource = new PostgresIdentitySnapshotReader(db);
        Assert.Equal(1001, await identitySource.CountAsync(job.Id, default));
        var first = await identitySource.ListAsync(job.Id, null, 100, default);
        Assert.Equal(100, first.Items.Count);
        var second = await identitySource.ListAsync(job.Id, first.NextAfter, 100, default);
        Assert.Equal("aad.user-0100", second.Items[0].SourceIdentityId);
        Assert.Equal("Person 100", second.Items[0].DisplayName);
        Assert.Equal(staged, await new StagingSyncService(
            new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, pages))
            .GetSyncAsync(job.Id, default));
    }

    [Fact]
    public async Task Identity_fingerprint_tampering_rolls_back_project_and_identity_staging_together()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, pageLease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var pages = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await pages.SaveAsync(pageLease, Page(job.Id, 0, [User(1)], null), default);
        var now = clock.GetUtcNow();
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = job.Id, ConnectionId = job.SourceConnectionId,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = job.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now,
                Projects = [new SourceProject("p1", "Project", [])],
                Identities = [], AccessGrants = []
            },
            Refs = [], RefsExternalized = true, RefCount = 0,
            IdentitiesExternalized = true, IdentityCount = 1,
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = true, PermissionsComplete = false
        }, default);
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var lease = new MigrationJobLease(job.Id, job.Id, job.SourceConnectionId,
            MigrationJobKinds.DiscoverSource, pageLease.LeaseToken, pageLease.Attempt);
        Assert.True(await jobs.CheckpointSnapshotAsync(lease, job.Id, default));
        var manifest = new PostgresDiscoveryRefManifestStore(db, clock);
        await manifest.PrepareAsync(lease, 0, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE discovery_identity_observations SET fingerprint = {"corrupted"}
            WHERE job_id = {job.Id}
            """);
        var stage = new StagingSyncService(new PostgresDiscoveryRepository(db),
            new PostgresStagingRepository(db, clock, manifest, pages));
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            stage.SyncUsingRefManifestAsync(lease, default));
        // The cached Graph page replay detects the altered fingerprint even
        // before staging starts; it must fail closed with no catalog writes.
        Assert.Equal("IdentityObservationConflict", error.Code);
        Assert.Equal(0, await db.StagingAssets.AsNoTracking()
            .CountAsync(x => x.SourceConnectionId == job.SourceConnectionId));
        Assert.Null(await stage.GetSyncAsync(job.Id, default));
    }

    [Fact]
    public async Task Conflicting_user_on_later_page_rolls_back_its_checkpoint_and_all_batches()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(lease, Page(job.Id, 0, [User(1)], "next"), default);
        var second = Enumerable.Range(500, 299).Select(User)
            .Append(User(1) with { DisplayName = "Conflicting Person" }).ToArray();
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(lease, Page(job.Id, 1, second, null), default));
        Assert.Equal("IdentityObservationConflict", error.Code);
        Assert.Null(await store.GetAsync(job.Id, "graph-users", 1, default));
        Assert.Single(await db.DiscoveryIdentityObservations.ToArrayAsync());
        Assert.Equal(new(job.Id, 1, 1), await store.GetProgressAsync(job.Id, default));
        var events = await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(job.Id, default);
        Assert.Single(events, e => e.EventType == "PageSaved");
    }

    [Fact]
    public async Task Expired_worker_cannot_write_an_identity_page_and_new_attempt_can_replay()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        await store.SaveAsync(lease, Page(job.Id, 0, [User(1)], "next"), default);
        clock.Advance(TimeSpan.FromMinutes(4));
        var reclaimed = (await new PostgresMigrationJobRepository(db, clock)
            .ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(3), default))!;
        Assert.Equal(job.Id, reclaimed.Id);
        var page = Page(job.Id, 1, [User(2)], null);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(lease, page, default));
        Assert.Equal("DiscoveryLeaseLost", error.Code);
        Assert.Null(await store.GetAsync(job.Id, "graph-users", 1, default));
        await store.SaveAsync(new(reclaimed.Id, reclaimed.LeaseToken, reclaimed.Attempt),
            page, default);
        Assert.Equal(new(job.Id, 2, 2), await store.GetProgressAsync(job.Id, default));
    }

    [Fact]
    public async Task Snapshot_verification_catches_missing_or_modified_users_and_backfills_old_pages()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var page = Page(job.Id, 0, [User(1), User(2)], null);
        await store.SaveAsync(lease, page, default);
        // Emulate a checkpoint saved before Graph observation support landed.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM discovery_identity_observations WHERE job_id = {job.Id}
            """);
        Assert.Equal(0, (await store.GetProgressAsync(job.Id, default)).ObservedUsers);
        await SaveSnapshotAsync(db, job,
            [new("aad.user-0001", "Person 1", "user1@example.invalid", "legacy.1"),
             new("aad.user-0002", "Person 2", "user2@example.invalid", "legacy.2")]);
        Assert.True(await new PostgresMigrationJobRepository(db, clock)
            .CheckpointSnapshotAsync(new MigrationJobLease(
                lease.JobId, job.Id, job.SourceConnectionId,
                MigrationJobKinds.DiscoverSource, lease.LeaseToken, lease.Attempt), job.Id, default));
        var worker = new MigrationJobLease(job.Id, job.Id, job.SourceConnectionId,
            MigrationJobKinds.DiscoverSource, lease.LeaseToken, lease.Attempt);
        await store.VerifySnapshotAsync(worker, default);
        Assert.Equal(2, (await store.GetProgressAsync(job.Id, default)).ObservedUsers);
        Assert.Equal(1, (await new PostgresMigrationJobRepository(db, clock)
            .EventsAsync(job.Id, default)).Count(x => x.EventType == "PageSaved"));
        // Tampering with the saved source identity (not the observations)
        // must be caught before the F-013 staging catalog is changed.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE migration_discoveries SET
                inventory = jsonb_set(inventory,
                  ARRAY['snapshot','identities', '0', 'displayName'],
                  to_jsonb({"Unexpected Name"}::text))
            WHERE id = {job.Id}
            """);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.VerifySnapshotAsync(worker, default));
        Assert.Equal("IdentitySnapshotMismatch", error.Code);
    }

    [Fact]
    public async Task Duplicate_descriptor_on_one_page_rejects_all_rows_atomically()
    {
        await ResetAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var (job, lease) = await EnqueueAndClaimAsync(clock);
        await using var db = fixture.NewContext();
        var store = new PostgresDiscoveryPageCheckpointStore(db, clock);
        var page = Page(job.Id, 0, [User(1), User(1)], null);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            store.SaveAsync(lease, page, default));
        Assert.Equal("DuplicateIdentityObservation", error.Code);
        Assert.Null(await store.GetAsync(job.Id, "graph-users", 0, default));
        Assert.Empty(await db.DiscoveryIdentityObservations.ToArrayAsync());
    }

    private sealed record GraphUser(string Descriptor, string DisplayName,
        string Email, string LegacyDescriptor);

    private static GraphUser User(int number) =>
        new($"aad.user-{number:0000}", $"Person {number}",
            $"user{number}@example.invalid", $"legacy.{number}");

    private static DiscoveryPageCheckpoint Page(
        Guid jobId, int index, IEnumerable<GraphUser> users, string? continuation) =>
        new(jobId, "graph-users", index, new string('a', 64),
            JsonSerializer.Serialize(new {
                value = users.Select(u => new {
                    descriptor = u.Descriptor,
                    displayName = u.DisplayName,
                    mailAddress = u.Email,
                    legacyDescriptor = u.LegacyDescriptor
                })
            }), continuation, DateTimeOffset.UtcNow);

    private static async Task SaveSnapshotAsync(AppDbContext db, MigrationJobView job,
        IReadOnlyList<SourceIdentity> identities)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = job.Id, ConnectionId = job.SourceConnectionId,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = job.Id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [], Identities = identities,
                AccessGrants = [], PermissionsComplete = false
            },
            Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = true, PermissionsComplete = false
        }, default);
    }

    private async Task<(MigrationJobView Job, DiscoveryLeaseContext Lease)>
        EnqueueAndClaimAsync(TimeProvider clock)
    {
        await using var db = fixture.NewContext();
        // Isolate test workers from unrelated queued migration jobs sharing
        // this PostgreSQL fixture. We need the active lease, not queue ranking.
        var jobs = new PostgresMigrationJobRepository(db, clock);
        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var source = Guid.NewGuid();
        var now = clock.GetUtcNow();
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = id, SourceConnectionId = source, SnapshotId = Guid.Empty,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Running,
            Attempts = 1, EnqueuedBy = Guid.NewGuid(),
            IdempotencyKey = "f018-graph-test:" + id,
            LeaseToken = token, LeaseUntilUtc = now.AddMinutes(3),
            AvailableAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        var job = (await jobs.GetAsync(id, default))!;
        return (job, new(id, token, 1));
    }

    private async Task ResetAsync()
    {
        await using var db = fixture.NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE discovery_group_observations, discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY",
            CancellationToken.None);
    }

    private sealed class TestClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan delta) => current = current.Add(delta);
    }
}
