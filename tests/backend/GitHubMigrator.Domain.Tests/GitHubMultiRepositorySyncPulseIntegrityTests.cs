using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubMultiRepositorySyncPulseIntegrityTests
{
    private static GitHubMultiRepositorySyncPulse Valid()
    {
        var snapshot = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            snapshot, "gh-org", new string('a', 64), true, false,
            "Prepared", GitHubMultiRepositoryStagingStates.LatestRecordedSnapshot,
            snapshot, now.AddMinutes(-4), now.AddMinutes(-1), now.AddMinutes(-1),
            false, 10, 3, 1, 6, 0, 3, 1, 0, 0,
            null, "DirectStagingWithoutJob", null, null,
            [
                new("repo-1", "migration-one",
                    2, 1, 0, 0, 3, "Blocked",
                    "DestinationWritePermissionUnverified",
                    GitHubMultiRepositoryRecoveryTracking.NotMonitored,
                    null, null, null, false, false, false, false),
                new("repo-2", "migration-two",
                    0, 0, 0, 0, 0, "Blocked",
                    "DestinationWritePermissionUnverified",
                    GitHubMultiRepositoryRecoveryTracking.NoMarkerRecorded,
                    null, null, null, false, false, false, false)
            ], false, false, false, false);
    }

    [Fact]
    public void F089_source_staging_is_not_a_GitHub_completion_or_freshness_grant()
    {
        var good = Valid();
        GitHubMultiRepositorySyncPulseIntegrity.Verify(good);
        foreach (var invalid in new[] {
            good with { ReadyForExecution = true },
            good with { CanPush = true },
            good with { CanRetry = true },
            good with { MigrationComplete = true },
            good with { ProviderSourceFreshnessVerified = true },
            good with { SourceInventoryComplete = true, CanRetry = true }
        })
            Assert.Throws<GitHubMultiRepositorySyncPulseException>(() =>
                GitHubMultiRepositorySyncPulseIntegrity.Verify(invalid));
    }

    [Fact]
    public void F089_per_repo_delta_count_and_recovery_scope_cannot_be_forged()
    {
        var pulse = Valid();
        GitHubMultiRepositorySyncPulseIntegrity.Verify(pulse);
        var a = pulse.Repositories[0];
        var b = pulse.Repositories[1];
        foreach (var forged in new[] {
            pulse with { Repositories = [a with { PlannedRefChanges = 4 }, b] },
            pulse with { Repositories = [a with { CanPush = true }, b] },
            pulse with { Repositories = [a with { CanRetry = true }, b] },
            pulse with { Repositories = [a with { MigrationComplete = true }, b] },
            pulse with { Repositories = [a with {
                RecoveryTracking =
                    GitHubMultiRepositoryRecoveryTracking.PossibleSendEvidence }, b] },
            pulse with { Repositories = [a with {
                StepState = "Succeeded" }, b] },
            pulse with { Repositories = [a, a] },
            pulse with { StagingState =
                GitHubMultiRepositoryStagingStates.SupersededInPostgres },
            pulse with { JournalAdded = 0 }
        })
            Assert.Throws<GitHubMultiRepositorySyncPulseException>(() =>
                GitHubMultiRepositorySyncPulseIntegrity.Verify(forged));
    }

    [Fact]
    public void F089_stale_source_is_still_historical_and_non_executable()
    {
        var pulse = Valid();
        var stale = pulse with {
            LatestKnownSnapshotId = Guid.NewGuid(),
            LatestKnownStagingAppliedAtUtc = pulse.StagingAppliedAtUtc.AddDays(1),
            StagingState =
                GitHubMultiRepositoryStagingStates.SupersededInPostgres,
            PlanIsCurrent = false
        };
        GitHubMultiRepositorySyncPulseIntegrity.Verify(stale);
        Assert.False(stale.ProviderSourceFreshnessVerified);
        Assert.False(stale.ReadyForExecution);
        Assert.False(stale.CanPush);
    }
}
