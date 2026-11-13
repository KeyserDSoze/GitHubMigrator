namespace GitHubMigrator.Business;

// F-089 is a PostgreSQL-only, multi-repository source-sync projection.
// Staging means "committed locally", NEVER "applied on GitHub".
// A possible-send marker means uncertainty, NEVER transfer success.
public sealed record GitHubMultiRepositorySyncPulseOptions(bool Enabled);

public sealed class GitHubMultiRepositorySyncPulseException(string code)
    : Exception("Multi-repository incremental sync pulse rejected")
{
    public string Code { get; } = code;
}

public static class GitHubMultiRepositoryRecoveryTracking
{
    public const string NotMonitored = "NotMonitored";
    public const string NoMarkerRecorded = "NoMarkerRecorded";
    public const string PossibleSendEvidence = "PossibleSendEvidence";
}

public static class GitHubMultiRepositoryStagingStates
{
    public const string LatestRecordedSnapshot = "LatestRecordedSnapshot";
    public const string SupersededInPostgres = "SupersededInPostgres";
}

public sealed record GitHubRepositorySourceDelta(
    string SourceRepositoryId, string TargetRepositoryName,
    int AddedRefs, int ChangedRefs, int RestoredRefs, int MissingRefs,
    int PlannedRefChanges, string StepState, string StepBlocker,
    string RecoveryTracking, string? RecoveryState,
    Guid? LatestBarrierId, DateTimeOffset? LatestPossibleSendUtc,
    bool CanOpenRecovery, bool CanPush, bool CanRetry,
    bool MigrationComplete)
{
    public int TotalRefChanges => AddedRefs + ChangedRefs + RestoredRefs + MissingRefs;
}

public sealed record GitHubMultiRepositorySyncPulse(
    Guid PreparationId, Guid PlanId, Guid SourceConnectionId,
    Guid SnapshotId, string Organization, string ManifestSha256,
    bool PlanIsCurrent, bool ReadyForExecution,
    string PreparationState, string StagingState, Guid LatestKnownSnapshotId,
    DateTimeOffset SourceCollectedAtUtc, DateTimeOffset StagingAppliedAtUtc,
    DateTimeOffset LatestKnownStagingAppliedAtUtc,
    bool SourceInventoryComplete, int StagingObserved,
    int StagingAdded, int StagingChanged, int StagingUnchanged,
    int StagingMarkedMissing, int JournalAdded, int JournalChanged,
    int JournalRestored, int JournalMissing,
    Guid? LatestStageJobId, string StageJobState,
    string? StageJobErrorCode, DateTimeOffset? StageJobUpdatedAtUtc,
    IReadOnlyList<GitHubRepositorySourceDelta> Repositories,
    bool ProviderSourceFreshnessVerified, bool CanPush, bool CanRetry,
    bool MigrationComplete);

// Only invariant-safe *observations* are accepted by the UI. It may not
// interpret a stale source sync, F-082, or F-085 as a completed transfer.
public static class GitHubMultiRepositorySyncPulseIntegrity
{
    public static void Verify(GitHubMultiRepositorySyncPulse value)
    {
        if (value is null || value.PreparationId == Guid.Empty ||
            value.PlanId == Guid.Empty || value.SourceConnectionId == Guid.Empty ||
            value.SnapshotId == Guid.Empty ||
            value.LatestKnownSnapshotId == Guid.Empty ||
            string.IsNullOrWhiteSpace(value.ManifestSha256) ||
            value.ManifestSha256.Length != 64 ||
            value.Repositories is null ||
            value.Repositories.Count is < 1 or > 10 ||
            value.Repositories.Select(x => x.SourceRepositoryId)
                .Distinct(StringComparer.Ordinal).Count() != value.Repositories.Count ||
            value.Repositories.Select(x => x.TargetRepositoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Repositories.Count ||
            value.SourceCollectedAtUtc == default ||
            value.StagingAppliedAtUtc == default ||
            value.LatestKnownStagingAppliedAtUtc == default ||
            value.StagingState is not (
                GitHubMultiRepositoryStagingStates.LatestRecordedSnapshot or
                GitHubMultiRepositoryStagingStates.SupersededInPostgres) ||
            (value.StagingState ==
                GitHubMultiRepositoryStagingStates.LatestRecordedSnapshot) !=
                (value.SnapshotId == value.LatestKnownSnapshotId) ||
            value.StagingObserved < 0 || value.StagingAdded < 0 ||
            value.StagingChanged < 0 || value.StagingUnchanged < 0 ||
            value.StagingMarkedMissing < 0 ||
            value.JournalAdded < 0 || value.JournalChanged < 0 ||
            value.JournalRestored < 0 || value.JournalMissing < 0 ||
            value.JournalAdded + value.JournalChanged +
                value.JournalRestored + value.JournalMissing < 0 ||
            value.ProviderSourceFreshnessVerified || value.CanPush ||
            value.CanRetry || value.MigrationComplete ||
            value.ReadyForExecution ||
            value.Repositories.Any(x =>
                x.AddedRefs < 0 || x.ChangedRefs < 0 ||
                x.RestoredRefs < 0 || x.MissingRefs < 0 ||
                x.PlannedRefChanges != x.TotalRefChanges ||
                x.StepState != "Blocked" ||
                string.IsNullOrWhiteSpace(x.StepBlocker) ||
                x.CanPush || x.CanRetry || x.MigrationComplete ||
                (x.RecoveryTracking !=
                    GitHubMultiRepositoryRecoveryTracking.NotMonitored &&
                 x.RecoveryTracking !=
                    GitHubMultiRepositoryRecoveryTracking.NoMarkerRecorded &&
                 x.RecoveryTracking !=
                    GitHubMultiRepositoryRecoveryTracking.PossibleSendEvidence) ||
                (x.RecoveryTracking ==
                    GitHubMultiRepositoryRecoveryTracking.PossibleSendEvidence) !=
                    (x.LatestBarrierId is not null &&
                     x.RecoveryState is not null &&
                     x.LatestPossibleSendUtc is not null) ||
                x.CanOpenRecovery !=
                    (x.RecoveryTracking ==
                     GitHubMultiRepositoryRecoveryTracking.PossibleSendEvidence)) ||
            value.Repositories.Sum(x => x.PlannedRefChanges) > value.JournalAdded +
                value.JournalChanged + value.JournalRestored +
                value.JournalMissing)
            throw new GitHubMultiRepositorySyncPulseException(
                "MultiRepositoryPulseIntegrityFailed");
    }
}
