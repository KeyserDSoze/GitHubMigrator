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
        Assert.Equal(2, first.EvidenceVersion);
        Assert.NotNull(first.RefChangeManifest);
        Assert.Equal(first.Proof.TotalRefChanges, first.RefChangeManifest!.Total);
        Assert.Equal(first.Proof.SnapshotId, first.RefChangeManifest.SnapshotId);
        Assert.Equal(64, first.RefChangeAttestationSha256!.Length);
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
        Assert.Equal(first.RefChangeAttestationSha256,
            replay.RefChangeAttestationSha256);
        Assert.Equal(first.RefChangeManifest, replay.RefChangeManifest);
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
        Assert.Equal(2, second.EvidenceVersion);
        Assert.NotNull(second.RefChangeManifest);
        Assert.Equal(second.Proof.TotalRefChanges, second.RefChangeManifest!.Total);
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
    public async Task F092_verified_ref_delta_pages_are_bounded_stable_and_source_scoped()
    {
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        PostgresIncrementalOperationPlanTests.Seed seed;
        IncrementalOperationPlan plan;
        await using (var setup = fixture.NewContext())
            (seed, plan) = await SeedPlanAsync(setup, clock, changed: 7);

        await using var db = fixture.NewContext();
        var journal = Journal(db, clock);
        var saved = await journal.CaptureAsync(seed.Run.Id, plan.Id,
            plan.SnapshotId, null, plan.ManifestSha256,
            Guid.NewGuid(), default);
        Assert.Equal(2, saved.EvidenceVersion);
        var first = await journal.ListRefChangesAsync(
            seed.Run.Id, plan.Id, saved.Id, null, 2, default);
        Assert.Equal(saved.RefChangeManifest!.EvidenceSha256,
            first.RefManifestSha256);
        Assert.Equal(saved.RefChangeAttestationSha256,
            first.RefAttestationSha256);
        Assert.Equal(saved.Proof.TotalRefChanges, first.TotalRefChanges);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextAfter);
        Assert.False(first.CanPush || first.CanRetry ||
            first.CanDispatch || first.MigrationComplete);
        GitHubSourceCheckpointRefChangeIntegrity.VerifyPage(first, 2);
        Assert.All(first.Items, x => {
            Assert.StartsWith("refs/", x.RefName);
            Assert.Equal("Added", x.Kind);
            Assert.Null(x.PreviousFingerprint);
            Assert.NotNull(x.CurrentFingerprint);
        });

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<GitHubSourceCheckpointRefChange>();
        string? cursor = null;
        do
        {
            var page = await journal.ListRefChangesAsync(
                seed.Run.Id, plan.Id, saved.Id, cursor, 2, default);
            GitHubSourceCheckpointRefChangeIntegrity.VerifyPage(page, 2);
            foreach (var item in page.Items)
            {
                Assert.True(ids.Add(item.SourceId));
                all.Add(item);
            }
            cursor = page.NextAfter;
        } while (cursor is not null);
        Assert.Equal(saved.Proof.TotalRefChanges, all.Count);
        Assert.Equal(all.OrderBy(x => x.SourceId, StringComparer.Ordinal)
            .Select(x => x.SourceId),
            all.Select(x => x.SourceId));

        // The cursor must actually belong to the same checkpoint's F-025
        // snapshot; arbitrary well-formed source IDs cannot skip data.
        var foreignCursor = System.Text.Json.JsonSerializer.Serialize(
            new[] { "foreign-repo", "refs/heads/missing" });
        Assert.Equal("SourceSyncCheckpointCursorInvalid",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.ListRefChangesAsync(seed.Run.Id, plan.Id,
                    saved.Id, foreignCursor, 2, default))).Code);
        Assert.Equal("SourceSyncCheckpointRefChangesInvalid",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.ListRefChangesAsync(seed.Run.Id, plan.Id,
                    saved.Id, "not-json", 2, default))).Code);
        Assert.Equal("SourceSyncCheckpointInvalidPage",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.ListRefChangesAsync(seed.Run.Id, plan.Id,
                    saved.Id, null, 101, default))).Code);
        Assert.Equal("SourceSyncCheckpointNotFound",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.ListRefChangesAsync(seed.Run.Id, plan.Id,
                    Guid.NewGuid(), null, 2, default))).Code);

        await using var reopened = fixture.NewContext();
        var persisted = await Journal(reopened, clock).ListRefChangesAsync(
            seed.Run.Id, plan.Id, saved.Id, null, 2, default);
        Assert.Equal(first.CheckpointId, persisted.CheckpointId);
        Assert.Equal(first.RefManifestSha256, persisted.RefManifestSha256);
        Assert.Equal(first.RefAttestationSha256, persisted.RefAttestationSha256);
        Assert.Equal(first.Items.ToArray(), persisted.Items.ToArray());
        Assert.Equal(first.NextAfter, persisted.NextAfter);
    }

    [Fact]
    public async Task F092_disabled_never_reads_even_a_sealed_checkpoint()
    {
        await using var db = fixture.NewContext();
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        var (seed, plan) = await SeedPlanAsync(db, clock);
        var checkpoint = await Journal(db, clock).CaptureAsync(
            seed.Run.Id, plan.Id, plan.SnapshotId, null,
            plan.ManifestSha256, Guid.NewGuid(), default);
        Assert.Equal("SourceSyncCheckpointDisabled",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => Journal(db, clock, false).ListRefChangesAsync(
                    seed.Run.Id, plan.Id, checkpoint.Id, null, 2,
                    default))).Code);
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
    public async Task F091_historic_count_only_F090_receipt_remains_readable_as_version_one()
    {
        await using var db = fixture.NewContext();
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        var (seed, plan) = await SeedPlanAsync(db, clock);
        var stage = await db.StagingSnapshotSyncs.AsNoTracking()
            .SingleAsync(x => x.SnapshotId == plan.SnapshotId);
        var staging = new PostgresStagingRepository(db, clock);
        var counts = (await staging.GetChangeSummaryAsync(plan.SnapshotId,
            default))!;
        var refRows = await db.StagingAssetChanges.AsNoTracking()
            .Where(x => x.SourceConnectionId == plan.SourceConnectionId &&
                x.SnapshotId == plan.SnapshotId && x.AssetType == "ref")
            .ToArrayAsync();
        var mapped = plan.Steps.Where(x => x.Kind == "ReviewTargetName")
            .OrderBy(x => x.SourceRepositoryId, StringComparer.Ordinal)
            .Select(step => {
                var changes = refRows.Where(row =>
                    System.Text.Json.JsonSerializer.Deserialize<string[]>(
                        row.SourceId)![0] == step.SourceRepositoryId).ToArray();
                return new GitHubSourceRefDelta(step.SourceRepositoryId,
                    step.TargetRepositoryName,
                    changes.Count(row => row.Kind == "Added"),
                    changes.Count(row => row.Kind == "Changed"),
                    changes.Count(row => row.Kind == "Restored"),
                    changes.Count(row => row.Kind == "Missing"));
            }).ToArray();
        var baseline = new GitHubSourceSyncCheckpointProof(
            plan.PreparationId, plan.Id, plan.SourceConnectionId,
            plan.SnapshotId, plan.SnapshotId, null, null,
            plan.ManifestSha256, stage.SourceCollectedAtUtc,
            stage.AppliedAtUtc, stage.SourceInventoryComplete,
            true, counts.Added, counts.Changed, counts.Restored,
            counts.Missing, refRows.Length,
            refRows.Length - mapped.Sum(m => m.Total), mapped);
        var id = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var sha = GitHubSourceSyncCheckpointIntegrity.Digest(baseline);
        var timestamp = DateTimeOffset.UtcNow;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.GitHubSourceSyncCheckpoints.Add(new() {
                Id = id, PreparationId = seed.Run.Id,
                PlanId = plan.Id, SourceConnectionId = plan.SourceConnectionId,
                SnapshotId = plan.SnapshotId, PreviousCheckpointId = null,
                ManifestSha256 = plan.ManifestSha256,
                ProofJson = System.Text.Json.JsonSerializer.Serialize(baseline),
                EvidenceSha256 = sha, EvidenceVersion = 1,
                RecordedBy = actor, RecordedAtUtc = timestamp,
                CanPush = false, CanRetry = false,
                CanDispatch = false, MigrationComplete = false
            });
            await db.SaveChangesAsync();
            db.GitHubSourceSyncCheckpointEvents.Add(new() {
                CheckpointId = id, Type = "Recorded", ActorId = actor,
                EvidenceSha256 = sha, OccurredAtUtc = timestamp
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var reopened = fixture.NewContext();
        var journal = Journal(reopened, clock);
        var history = await journal.ListAsync(
            seed.Run.Id, plan.Id, null, 10, default);
        var old = Assert.Single(history.Items);
        Assert.Equal(1, old.EvidenceVersion);
        Assert.Null(old.RefChangeManifest);
        Assert.Null(old.RefChangeAttestationSha256);
        Assert.Equal("SourceSyncCheckpointLegacyUnattested",
            (await Assert.ThrowsAsync<GitHubSourceSyncCheckpointException>(
                () => journal.ListRefChangesAsync(
                    seed.Run.Id, plan.Id, old.Id, null, 2,
                    default))).Code);
        Assert.Equal(sha, old.EvidenceSha256);
        GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(old);

        // No retroactive upgrade or duplicate append event on exact replay.
        var replay = await journal.CaptureAsync(seed.Run.Id, plan.Id,
            plan.SnapshotId, null, plan.ManifestSha256,
            Guid.NewGuid(), default);
        Assert.Equal(old.Id, replay.Id);
        Assert.Equal(1, replay.EvidenceVersion);
        Assert.Single(await reopened.GitHubSourceSyncCheckpointEvents
            .AsNoTracking().Where(x => x.CheckpointId == id).ToArrayAsync());
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
