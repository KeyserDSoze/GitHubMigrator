namespace GitHubMigrator.Business;

// F-093 compares attested F-091 source-change *names* with an independently
// observed, read-only F-031 GitHub ref set. F-013 SHA-256 fingerprints are
// NOT Git OIDs: no object equality, ancestry, transfer or push is inferred.
public static class GitHubCheckpointTargetRefReviewStates
{
    public const string SourceAddedTargetAbsent = "SourceAddedTargetAbsentReview";
    public const string SourceAddedTargetPresent = "SourceAddedTargetPresentReview";
    public const string SourceRestoredTargetAbsent = "SourceRestoredTargetAbsentReview";
    public const string SourceRestoredTargetPresent = "SourceRestoredTargetPresentReview";
    public const string SourceChangedTargetAbsent = "SourceChangedTargetAbsentBlocked";
    public const string SourceChangedTargetPresent = "SourceChangedTargetPresentBlocked";
    public const string SourceMissingTargetAbsent = "SourceMissingTargetAbsentPreserved";
    public const string SourceMissingTargetPresent = "SourceMissingTargetPresentPreserved";
}

public sealed record GitHubCheckpointTargetRefReviewItem(
    GitHubSourceCheckpointRefChange SourceChange,
    bool TargetRefObserved,
    string ReviewState);

public sealed record GitHubCheckpointTargetRefReviewPage(
    Guid CheckpointId, Guid SourceSnapshotId,
    Guid TargetObservationId, string SourceRepositoryId,
    string TargetRepositoryName, string SourceRefManifestSha256,
    string SourceRefAttestationSha256, string TargetEvidenceSha256,
    DateTimeOffset TargetObservedAtUtc, int RepositoryRefChanges,
    IReadOnlyList<GitHubCheckpointTargetRefReviewItem> Items,
    string? NextAfter, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitHubCheckpointTargetRefReviewIntegrity
{
    public const int MaxPageSize = 50;

    private static GitHubSourceSyncCheckpointException Invalid() =>
        new("SourceSyncCheckpointTargetReviewInvalid");

    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string ReviewState(string changeKind, bool targetRefObserved) =>
        changeKind switch
        {
            "Added" => targetRefObserved
                ? GitHubCheckpointTargetRefReviewStates.SourceAddedTargetPresent
                : GitHubCheckpointTargetRefReviewStates.SourceAddedTargetAbsent,
            "Restored" => targetRefObserved
                ? GitHubCheckpointTargetRefReviewStates.SourceRestoredTargetPresent
                : GitHubCheckpointTargetRefReviewStates.SourceRestoredTargetAbsent,
            "Changed" => targetRefObserved
                ? GitHubCheckpointTargetRefReviewStates.SourceChangedTargetPresent
                : GitHubCheckpointTargetRefReviewStates.SourceChangedTargetAbsent,
            "Missing" => targetRefObserved
                ? GitHubCheckpointTargetRefReviewStates.SourceMissingTargetPresent
                : GitHubCheckpointTargetRefReviewStates.SourceMissingTargetAbsent,
            _ => throw Invalid()
        };

    public static void VerifyPage(GitHubCheckpointTargetRefReviewPage page,
        int take, string? after = null)
    {
        if (page is null || page.CheckpointId == Guid.Empty ||
            page.SourceSnapshotId == Guid.Empty ||
            page.TargetObservationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(page.SourceRepositoryId) ||
            string.IsNullOrWhiteSpace(page.TargetRepositoryName) ||
            !Sha(page.SourceRefManifestSha256) ||
            !Sha(page.SourceRefAttestationSha256) ||
            !Sha(page.TargetEvidenceSha256) ||
            page.TargetObservedAtUtc == default ||
            page.RepositoryRefChanges < 0 ||
            take is < 1 or > MaxPageSize ||
            page.Items is null || page.Items.Count > take ||
            page.CanPush || page.CanRetry || page.CanDispatch ||
            page.MigrationComplete ||
            (page.NextAfter is not null &&
                (page.Items.Count == 0 ||
                 page.NextAfter != page.Items[^1].SourceChange.SourceId)))
            throw Invalid();

        if (after is not null &&
            GitHubSourceCheckpointRefChangeIntegrity.ParseKey(after)
                .RepositoryId != page.SourceRepositoryId)
            throw Invalid();
        var previous = after;
        foreach (var item in page.Items)
        {
            if (item?.SourceChange is null)
                throw Invalid();
            var change = item.SourceChange;
            var parsed = GitHubSourceCheckpointRefChangeIntegrity.Parse(
                new StagingChangeView("ref", change.SourceId,
                    change.Kind, change.PreviousFingerprint,
                    change.CurrentFingerprint, change.Revision),
                change.TargetRepositoryName);
            if (parsed != change ||
                change.SourceRepositoryId != page.SourceRepositoryId ||
                change.TargetRepositoryName != page.TargetRepositoryName ||
                (previous is not null &&
                 string.CompareOrdinal(previous, change.SourceId) >= 0) ||
                item.ReviewState != ReviewState(
                    change.Kind, item.TargetRefObserved))
                throw Invalid();
            previous = change.SourceId;
        }
    }
}
