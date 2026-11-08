using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL17: immutable F-025→F-029 source epoch chain. No
// GitHub transport, token mint, retry or migration-completion claim.
public sealed class PostgresGitHubSourceSyncCheckpointTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o =>
                o.UseNpgsql(fixture.ConnectionString))
            .BuildServiceProvider();
        await GitHubSourceSyncCheckpointSchema.InitializeAsync(services);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private static PostgresGitHubSourceSyncCheckpointJournal Journal(
        AppDbContext db,
        PostgresIncrementalOperationPlanTests.ManualClock clock,
        bool enabled = true) =>
        new(db, clock, new(enabled),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresStagingRepository(db, clock));

    private static async Task<(
        PostgresIncrementalOperationPlanTests.Seed Seed,
        IncrementalOperationPlan Plan)> SeedPlanAsync(
        AppDbContext db,
        PostgresIncrementalOperationPlanTests.ManualClock clock,
        int changed = 7)
    {
        var seed = await PostgresIncrementalOperationPlanTests.SeedAsync(
            db, clock, refs: changed);
        var plan = await PostgresIncrementalOperationPlanTests.NewService(
            db, clock).CreateAsync(seed.Run.Id, seed.OperatorId,
            seed.Preflight.EvidenceSha256, default);
        return (seed, plan);
    }

    private static async Task<Guid> AddSnapshotAsync(
        AppDbContext db,
        PostgresIncrementalOperationPlanTests.ManualClock clock,
        IncrementalOperationPlan plan, int ordinal)
    {
        var id = Guid.NewGuid();
        var observed = clock.GetUtcNow().AddMinutes(ordinal);
        var inventory = new DiscoveryInventory
        {
            Id = id, ConnectionId = plan.SourceConnectionId,
            SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = observed,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = id,
                SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = observed,
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("repo-1", "project", "One",
                        VersionControlKind.Git),
                    new SourceRepository("repo-2", "project", "Two",
                        VersionControlKind.Git)])],
                Identities = [], AccessGrants = []
            },
            // An unchanged ref in repo-1 and one new ref in repo-2
            // must be attributed to the correct mapped source.
            Refs = [
                new RepositoryRef("repo-1", "refs/heads/b-0000",
                    "sha-0000"),
                new RepositoryRef("repo-2", "refs/heads/new-" + ordinal,
                    "sha-new-" + ordinal)
            ],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        await new PostgresDiscoveryRepository(db).SaveAsync(
            inventory, default);
        var normalized = StagingAssetNormalizer.Normalize(inventory);
        await new PostgresStagingRepository(db, clock).ApplyAsync(
            inventory, normalized.Assets, normalized.CompleteTypes, default);
        return id;
    }

    [Fact]
    public async Task F090_first_snapshot_is_the_sealed_F029_baseline_after_restart()
    {
        PostgresIncrementalOperationPlanTests.Seed seed;
        IncrementalOperationPlan plan;
        await using (var setup = fixture.NewContext())
        {
            var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
                DateTimeOffset.UtcNow);
            (seed, plan) = await SeedPlanAsync(setup, clock);
        }

        await using var restarted = fixture.NewContext();
        var reader = Journal(restarted,
            new PostgresIncrementalOperationPlanTests.ManualClock(
                DateTimeOffset.UtcNow));
        var before = await reader.ListAsync(seed.Run.Id,
            plan.Id, null, 10, default);
        Assert.Empty(before.Items);
        Assert.Equal(plan.SnapshotId, before.NextEligibleSnapshotId);
        Assert.Null(before.LatestCheckpointId);
        var first = await reader.CaptureAsync(
            seed.Run.Id, plan.Id, plan.SnapshotId, null,
            plan.ManifestSha256, Guid.NewGuid(), default);
        GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(first);
        Assert.True(first.Proof.IsBaselineSnapshot);
        Assert.Equal(plan.ChangedRefCount,
            first.Proof.Repositories.Sum(x => x.Total));
        Assert.Equal(plan.SnapshotId, first.Proof.SnapshotId);
        Assert.False(first.CanPush);
        Assert.False(first.CanRetry);
        Assert.False(first.MigrationComplete);
        Assert.Equal(0, first.Proof.UnmappedRefChanges);

        await using var nextSession = fixture.NewContext();
        var replay = await Journal(nextSession,
            new PostgresIncrementalOperationPlanTests.ManualClock(
                DateTimeOffset.UtcNow)).CaptureAsync(
                    seed.Run.Id, plan.Id, plan.SnapshotId,
                    null, plan.ManifestSha256, Guid.NewGuid(), default);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.RecordedBy, replay.RecordedBy);
        Assert.Equal(first.EvidenceSha256, replay.EvidenceSha256);
        var after = await Journal(nextSession,
            new PostgresIncrementalOperationPlanTests.ManualClock(
                DateTimeOffset.UtcNow)).ListAsync(
                    seed.Run.Id, plan.Id, null, 10, default);
        Assert.Equal(first.Id, Assert.Single(after.Items).Id);
        Assert.Null(after.NextEligibleSnapshotId);
        Assert.Single(await nextSession.GitHubSourceSyncCheckpointEvents
            .AsNoTracking().Where(x => x.CheckpointId == first.Id).ToArrayAsync());
    }

    [Fact]
    public async Task F090_next_increment_must_follow_exact_prior_snapshot_without_skips()
    {
        await using var db = fixture.NewContext();
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        var (seed, plan) = await SeedPlanAsync(db, clock, 3);
        var first = await Journal(db, clock).CaptureAsync(
            seed.Run.Id, plan.Id, plan.SnapshotId, null,
            plan.ManifestSha256, Guid.NewGuid(), default);
        var stage2 = await AddSnapshotAsync(db, clock, plan, 1);
        var stage3 = await AddSnapshotAsync(db, clock, plan, 2);
        var journal = Journal(db, clock);

        Assert.Equal("SourceSyncCheckpointOutOfOrder",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.CaptureAsync(seed.Run.Id, plan.Id,
                    stage3, first.Id, plan.ManifestSha256,
                    Guid.NewGuid(), default))).Code);
        var next = await journal.ListAsync(
            seed.Run.Id, plan.Id, null, 10, default);
        Assert.Equal(stage2, next.NextEligibleSnapshotId);

        var second = await journal.CaptureAsync(seed.Run.Id, plan.Id,
            stage2, first.Id, plan.ManifestSha256, Guid.NewGuid(), default);
        Assert.Equal(first.Id, second.Proof.PreviousCheckpointId);
        Assert.Equal(first.EvidenceSha256,
            second.Proof.PreviousEvidenceSha256);
        Assert.Equal(1, second.Proof.Repositories.Single(x =>
            x.SourceRepositoryId == "repo-2").Added);
        Assert.Equal(0, second.Proof.Repositories.Single(x =>
            x.SourceRepositoryId == "repo-1").Changed);
        Assert.False(second.Proof.IsBaselineSnapshot);
        var third = await journal.CaptureAsync(seed.Run.Id, plan.Id,
            stage3, second.Id, plan.ManifestSha256,
            Guid.NewGuid(), default);
        Assert.Equal(second.EvidenceSha256,
            third.Proof.PreviousEvidenceSha256);

        await using var rebooted = fixture.NewContext();
        var after = await Journal(rebooted, clock).ListAsync(
            seed.Run.Id, plan.Id, null, 2, default);
        Assert.Equal([third.Id, second.Id],
            after.Items.Select(x => x.Id).ToArray());
        Assert.Equal(second.Id, after.NextAfter);
        Assert.Equal(third.Id, after.LatestCheckpointId);
        Assert.Null(after.NextEligibleSnapshotId);
        var older = await Journal(rebooted, clock).ListAsync(
            seed.Run.Id, plan.Id, after.NextAfter, 2, default);
        Assert.Equal(first.Id, Assert.Single(older.Items).Id);
        Assert.False(after.CanPush);
        Assert.False(after.MigrationComplete);
        Assert.Equal("SourceSyncCheckpointCursorInvalid",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => Journal(rebooted, clock).ListAsync(
                    seed.Run.Id, plan.Id, Guid.NewGuid(), 2, default))).Code);
    }

    [Fact]
    public async Task F090_six_concurrent_replicas_record_exactly_one_receipt_and_event()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        PostgresIncrementalOperationPlanTests.Seed seed;
        IncrementalOperationPlan plan;
        await using (var setup = fixture.NewContext())
            (seed, plan) = await SeedPlanAsync(setup, clock);
        var actors = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var tasks = actors.Select(async actor =>
        {
            await using var db = fixture.NewContext();
            return await Journal(db, clock).CaptureAsync(seed.Run.Id,
                plan.Id, plan.SnapshotId, null,
                plan.ManifestSha256, actor, default);
        }).ToArray();
        var receipts = await Task.WhenAll(tasks);
        Assert.Single(receipts.Select(x => x.Id).Distinct());
        Assert.Single(receipts.Select(x => x.EvidenceSha256).Distinct());
        await using var verify = fixture.NewContext();
        Assert.Single(await verify.GitHubSourceSyncCheckpoints.AsNoTracking()
            .Where(x => x.PlanId == plan.Id).ToArrayAsync());
        Assert.Single(await verify.GitHubSourceSyncCheckpointEvents.AsNoTracking()
            .Where(x => x.CheckpointId == receipts[0].Id).ToArrayAsync());
    }

    [Fact]
    public async Task F090_mutation_conflicting_cursor_and_no_journal_are_fail_closed()
    {
        await using var db = fixture.NewContext();
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        var (seed, plan) = await SeedPlanAsync(db, clock);
        var journal = Journal(db, clock);
        Assert.Equal("SourceSyncCheckpointDisabled",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => Journal(db, clock, false).ListAsync(
                    seed.Run.Id, plan.Id, null, 10, default))).Code);
        Assert.Equal("SourceSyncCheckpointManifestChanged",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.CaptureAsync(seed.Run.Id,
                    plan.Id, plan.SnapshotId, null,
                    new string('c', 64), Guid.NewGuid(), default))).Code);
        var sealedReceipt = await journal.CaptureAsync(seed.Run.Id,
            plan.Id, plan.SnapshotId, null,
            plan.ManifestSha256, Guid.NewGuid(), default);
        Assert.Equal("SourceSyncCheckpointReplayConflict",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.CaptureAsync(seed.Run.Id,
                    plan.Id, plan.SnapshotId, Guid.NewGuid(),
                    plan.ManifestSha256, Guid.NewGuid(), default))).Code);

        await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE github_source_sync_checkpoints
                SET evidence_sha256 = {new string('0', 64)}
                WHERE id = {sealedReceipt.Id}
                """));
        await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM github_source_sync_checkpoint_events
                WHERE checkpoint_id = {sealedReceipt.Id}
                """));
        // A blocked SQL mutation must never invalidate the original
        // immutable receipt after a new connection opens.
        await using var read = fixture.NewContext();
        var view = await Journal(read, clock).ListAsync(
            seed.Run.Id, plan.Id, null, 10, default);
        Assert.Equal(sealedReceipt.EvidenceSha256,
            Assert.Single(view.Items).EvidenceSha256);
    }
}
