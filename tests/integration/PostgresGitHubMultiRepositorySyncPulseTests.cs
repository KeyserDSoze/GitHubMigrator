using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real PostgreSQL17: F-013/F-025 staging, F-026 preparation, F-027/F-028,
// F-029 immutable 2-repository plan and F-089 source sync pulse.
// No GitHub network, credentials, target mutation or sender.
public sealed class PostgresGitHubMultiRepositorySyncPulseTests(
    PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    private static string Sha(char c) => new(c, 64);

    private sealed class RecoveryHistory : IGitHubRemoteRecoveryHistoryReader
    {
        public int Reads;
        public GitHubRemoteRecoveryWorkbenchItem? Latest;

        public Task<GitHubRemoteRecoveryWorkbenchPage> ListAsync(
            Guid preparationId, Guid planId, Guid? after,
            int take, CancellationToken ct)
        {
            Reads++;
            Assert.Null(after);
            Assert.Equal(1, take);
            var items = Latest is null
                ? Array.Empty<GitHubRemoteRecoveryWorkbenchItem>()
                : [Latest];
            return Task.FromResult(new GitHubRemoteRecoveryWorkbenchPage(
                preparationId, planId, 2468, items,
                null, false, false, false, false));
        }

        public void AddValid(Guid preparationId, Guid planId)
        {
            Latest = new(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), preparationId, planId, Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), 2468, "gh-org",
                "migration-one", DateTimeOffset.UtcNow,
                GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
                GitHubRemoteRecoveryWorkbenchActions.ObserveTips,
                true, true, 2, 1, Sha('a'), Sha('b'),
                null, null, null, null, null, null, null,
                false, false, false, false);
        }
    }

    private static PostgresGitHubMultiRepositorySyncPulseReader Reader(
        AppDbContext db,
        PostgresIncrementalOperationPlanTests.ManualClock clock,
        RecoveryHistory fake, bool enabled = true, bool recoveryEnabled = false)
        => new(db, new(enabled),
            new PostgresIncrementalOperationPlanRepository(db, clock),
            new PostgresStagingRepository(db, clock),
            new(recoveryEnabled, 2468, "gh-org/migration-one"),
            fake);

    [Fact]
    public async Task F089_reads_two_source_repos_without_claiming_GitHub_migration()
    {
        await using var db = fixture.NewContext();
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        var seed = await PostgresIncrementalOperationPlanTests.SeedAsync(
            db, clock, refs: 7);
        var plan = await PostgresIncrementalOperationPlanTests.NewService(
            db, clock).CreateAsync(seed.Run.Id, seed.OperatorId,
            seed.Preflight.EvidenceSha256, default);
        var recovery = new RecoveryHistory();
        await using var fresh = fixture.NewContext();
        var result = await Reader(fresh, clock, recovery).InspectAsync(
            seed.Run.Id, plan.Id, default);
        GitHubMultiRepositorySyncPulseIntegrity.Verify(result);
        Assert.Equal(2, result.Repositories.Count);
        Assert.Equal(7, result.Repositories.Sum(x => x.TotalRefChanges));
        Assert.Equal(7, result.Repositories[0].PlannedRefChanges);
        Assert.Equal("repo-1", result.Repositories[0].SourceRepositoryId);
        Assert.Equal("migration-one", result.Repositories[0].TargetRepositoryName);
        Assert.Equal(7, result.Repositories[0].AddedRefs);
        Assert.Equal("repo-2", result.Repositories[1].SourceRepositoryId);
        Assert.Equal(0, result.Repositories[1].PlannedRefChanges);
        Assert.Equal(GitHubMultiRepositoryRecoveryTracking.NotMonitored,
            result.Repositories[0].RecoveryTracking);
        Assert.All(result.Repositories, x =>
        {
            Assert.False(x.CanOpenRecovery);
            Assert.False(x.CanPush);
            Assert.False(x.CanRetry);
            Assert.False(x.MigrationComplete);
            Assert.Equal("Blocked", x.StepState);
        });
        Assert.Equal(0, recovery.Reads);
        Assert.Equal(plan.ManifestSha256, result.ManifestSha256);
        Assert.Equal(plan.SnapshotId, result.LatestKnownSnapshotId);
        Assert.Equal(GitHubMultiRepositoryStagingStates.LatestRecordedSnapshot,
            result.StagingState);
        Assert.Equal("DirectStagingWithoutJob", result.StageJobState);
        Assert.False(result.SourceInventoryComplete);
        Assert.False(result.ProviderSourceFreshnessVerified);
        Assert.False(result.CanPush);
        Assert.False(result.CanRetry);
        Assert.False(result.MigrationComplete);
    }

    [Fact]
    public async Task F089_only_exact_allowlisted_target_gets_sandbox_recovery_evidence()
    {
        await using var db = fixture.NewContext();
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        var seed = await PostgresIncrementalOperationPlanTests.SeedAsync(
            db, clock, refs: 3);
        var plan = await PostgresIncrementalOperationPlanTests.NewService(
            db, clock).CreateAsync(seed.Run.Id, seed.OperatorId,
            seed.Preflight.EvidenceSha256, default);
        var history = new RecoveryHistory();
        var reader = Reader(db, clock, history, recoveryEnabled: true);
        var noMarker = await reader.InspectAsync(seed.Run.Id, plan.Id, default);
        Assert.Equal(1, history.Reads);
        Assert.Equal(GitHubMultiRepositoryRecoveryTracking.NoMarkerRecorded,
            noMarker.Repositories.Single(x => x.SourceRepositoryId == "repo-1")
                .RecoveryTracking);
        Assert.Equal(GitHubMultiRepositoryRecoveryTracking.NotMonitored,
            noMarker.Repositories.Single(x => x.SourceRepositoryId == "repo-2")
                .RecoveryTracking);
        history.AddValid(seed.Run.Id, plan.Id);
        var result = await reader.InspectAsync(seed.Run.Id, plan.Id, default);
        Assert.Equal(2, history.Reads);
        var sandbox = result.Repositories.Single(x =>
            x.SourceRepositoryId == "repo-1");
        Assert.Equal(GitHubMultiRepositoryRecoveryTracking.PossibleSendEvidence,
            sandbox.RecoveryTracking);
        Assert.Equal(history.Latest!.BarrierId, sandbox.LatestBarrierId);
        Assert.Equal(GitHubRemotePostSendRecoveryStates.AwaitingIndependentReadback,
            sandbox.RecoveryState);
        Assert.True(sandbox.CanOpenRecovery);
        Assert.Null(result.Repositories.Single(x =>
            x.SourceRepositoryId == "repo-2").LatestBarrierId);
        Assert.False(sandbox.CanPush);
        Assert.False(sandbox.CanRetry);

        history.Latest = history.Latest with { CanRetry = true };
        var corrupt = await Assert.ThrowsAsync<
            GitHubMultiRepositorySyncPulseException>(() =>
                reader.InspectAsync(seed.Run.Id, plan.Id, default));
        Assert.Equal("MultiRepositoryPulseRecoveryCorrupt", corrupt.Code);
        history.Latest = history.Latest with {
            CanRetry = false, RepositoryId = 987654
        };
        corrupt = await Assert.ThrowsAsync<
            GitHubMultiRepositorySyncPulseException>(() =>
                reader.InspectAsync(seed.Run.Id, plan.Id, default));
        Assert.Equal("MultiRepositoryPulseRecoveryCorrupt", corrupt.Code);
    }

    [Fact]
    public async Task F089_invalid_or_mutated_plan_and_disabled_switch_fail_closed()
    {
        await using var db = fixture.NewContext();
        var clock = new PostgresIncrementalOperationPlanTests.ManualClock(
            DateTimeOffset.UtcNow);
        var seed = await PostgresIncrementalOperationPlanTests.SeedAsync(
            db, clock);
        var plan = await PostgresIncrementalOperationPlanTests.NewService(
            db, clock).CreateAsync(seed.Run.Id, seed.OperatorId,
            seed.Preflight.EvidenceSha256, default);
        var history = new RecoveryHistory();
        var disabled = await Assert.ThrowsAsync<
            GitHubMultiRepositorySyncPulseException>(() =>
                Reader(db, clock, history, enabled: false)
                    .InspectAsync(seed.Run.Id, plan.Id, default));
        Assert.Equal("MultiRepositoryPulseDisabled", disabled.Code);
        Assert.Equal("MultiRepositoryPulseNotFound",
            (await Assert.ThrowsAsync<GitHubMultiRepositorySyncPulseException>(
                () => Reader(db, clock, history).InspectAsync(
                    seed.Run.Id, Guid.NewGuid(), default))).Code);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE incremental_operation_plan_steps
            SET changed_refs = changed_refs + 9
            WHERE plan_id = {plan.Id} AND ordinal = 1
            """);
        Assert.Equal("MultiRepositoryPulsePlanCorrupt",
            (await Assert.ThrowsAsync<GitHubMultiRepositorySyncPulseException>(
                () => Reader(db, clock, history).InspectAsync(
                    seed.Run.Id, plan.Id, default))).Code);
        Assert.Equal(0, history.Reads);
    }
}
