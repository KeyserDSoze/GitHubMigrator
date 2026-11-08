using System.Text.Json;

namespace GitHubMigrator.Business;

// F-092: bounded, source-only change inspection. A delta is evidence
// from an immutable F-025 snapshot, NOT a proposed GitHub write.
public sealed record GitHubSourceCheckpointRefChange(
    string SourceId, string SourceRepositoryId, string RefName,
    string Kind, string? PreviousFingerprint, string? CurrentFingerprint,
    int Revision, string? TargetRepositoryName);

public sealed record GitHubSourceCheckpointRefChangePage(
    Guid CheckpointId, Guid SnapshotId, string RefManifestSha256,
    string RefAttestationSha256, int TotalRefChanges,
    IReadOnlyList<GitHubSourceCheckpointRefChange> Items,
    string? NextAfter, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitHubSourceCheckpointRefChangeIntegrity
{
    public const int MaxPageSize = 100;

    private static GitHubSourceSyncCheckpointException Invalid() =>
        new("SourceSyncCheckpointRefChangesInvalid");

    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // A cursor is an exact canonical F-013 JSON natural key, not
    // an arbitrary SQL predicate, approximate prefix or offset.
    public static (string RepositoryId, string RefName) ParseKey(
        string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId) || sourceId.Length > 1024)
            throw Invalid();
        string[]? parts;
        try { parts = JsonSerializer.Deserialize<string[]>(sourceId); }
        catch (JsonException) { throw Invalid(); }
        if (parts is not { Length: 2 } ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            !parts[1].StartsWith("refs/", StringComparison.Ordinal) ||
            JsonSerializer.Serialize(parts) != sourceId)
            throw Invalid();
        return (parts[0], parts[1]);
    }

    public static GitHubSourceCheckpointRefChange Parse(
        StagingChangeView change, string? targetRepositoryName)
    {
        if (change is null || change.AssetType != "ref" ||
            change.Revision < 1 ||
            targetRepositoryName is { Length: 0 })
            throw Invalid();
        var key = ParseKey(change.SourceId);
        var before = change.PreviousFingerprint;
        var after = change.CurrentFingerprint;
        var safe = change.Kind switch {
            "Added" => before is null && Sha(after),
            "Changed" => Sha(before) && Sha(after) && before != after,
            "Restored" => Sha(before) && Sha(after),
            "Missing" => Sha(before) && after is null,
            _ => false
        };
        if (!safe)
            throw Invalid();
        return new(change.SourceId, key.RepositoryId, key.RefName,
            change.Kind, before, after, change.Revision,
            targetRepositoryName);
    }

    public static void VerifyPage(
        GitHubSourceCheckpointRefChangePage page, int maxItems)
    {
        if (page is null || page.CheckpointId == Guid.Empty ||
            page.SnapshotId == Guid.Empty || !Sha(page.RefManifestSha256) ||
            !Sha(page.RefAttestationSha256) ||
            page.TotalRefChanges < 0 ||
            maxItems is < 1 or > MaxPageSize ||
            page.Items is null || page.Items.Count > maxItems ||
            page.CanPush || page.CanRetry || page.CanDispatch ||
            page.MigrationComplete ||
            (page.NextAfter is not null &&
             (page.Items.Count == 0 ||
              page.NextAfter != page.Items[^1].SourceId)))
            throw Invalid();
        string? previous = null;
        foreach (var item in page.Items)
        {
            var row = new StagingChangeView("ref", item.SourceId,
                item.Kind, item.PreviousFingerprint,
                item.CurrentFingerprint, item.Revision);
            var parsed = Parse(row, item.TargetRepositoryName);
            if (parsed != item ||
                (previous is not null &&
                 string.CompareOrdinal(previous, item.SourceId) >= 0))
                throw Invalid();
            previous = item.SourceId;
        }
    }
}
