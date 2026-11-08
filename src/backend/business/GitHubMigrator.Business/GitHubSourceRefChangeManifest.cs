using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-091 foundation: source-only, deterministic commitment to the F-025
// per-ref before/after staging fingerprints. Does not authorize GitHub writes.
public sealed record GitHubSourceRefChangeManifest(
    Guid SourceConnectionId, Guid SnapshotId,
    int Added, int Changed, int Restored, int Missing,
    string EvidenceSha256)
{
    public long Total => (long)Added + Changed + Restored + Missing;
}

public sealed class GitHubSourceRefChangeManifestException(string code)
    : Exception("Source ref change evidence refused")
{
    public string Code { get; } = code;
}

public static class GitHubSourceRefChangeManifestIntegrity
{
    private const string Domain = "f091-source-ref-change-manifest-v1";

    private static bool Sha256(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GitHubSourceRefChangeManifestException Invalid() =>
        new("SourceRefChangeManifestInvalid");

    public static void Verify(GitHubSourceRefChangeManifest manifest)
    {
        if (manifest.SourceConnectionId == Guid.Empty ||
            manifest.SnapshotId == Guid.Empty ||
            manifest.Added < 0 || manifest.Changed < 0 ||
            manifest.Restored < 0 || manifest.Missing < 0 ||
            manifest.Total > int.MaxValue ||
            !Sha256(manifest.EvidenceSha256))
            throw Invalid();
    }

    // The input must be sorted by canonical F-013 source_id (UTF-8 byte order).
    // Each row is hashed immediately; no collection of ref names or OIDs is
    // retained. Fingerprints already bind the source ObjectId in F-013.
    public static async Task<GitHubSourceRefChangeManifest> ComputeAsync(
        Guid sourceConnectionId, Guid snapshotId,
        IAsyncEnumerable<StagingChangeView> changes,
        CancellationToken ct = default)
    {
        if (sourceConnectionId == Guid.Empty || snapshotId == Guid.Empty ||
            changes is null)
            throw Invalid();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, JsonSerializer.Serialize(new[] {
            Domain, sourceConnectionId.ToString("D"),
            snapshotId.ToString("D")
        }) + "\n");

        string? previousKey = null;
        long added = 0, changed = 0, restored = 0, missing = 0;
        await foreach (var change in changes.WithCancellation(ct))
        {
            if (change is null || change.AssetType != "ref" ||
                string.IsNullOrWhiteSpace(change.SourceId) ||
                change.SourceId.Length > 1024 || change.Revision < 1)
                throw Invalid();

            string[]? id;
            try { id = JsonSerializer.Deserialize<string[]>(change.SourceId); }
            catch (JsonException) { throw Invalid(); }
            if (id is not { Length: 2 } ||
                string.IsNullOrWhiteSpace(id[0]) ||
                string.IsNullOrWhiteSpace(id[1]) ||
                !id[1].StartsWith("refs/", StringComparison.Ordinal) ||
                !string.Equals(JsonSerializer.Serialize(id), change.SourceId,
                    StringComparison.Ordinal) ||
                (previousKey is not null &&
                 string.CompareOrdinal(previousKey, change.SourceId) >= 0))
                throw Invalid();

            var previous = change.PreviousFingerprint;
            var current = change.CurrentFingerprint;
            switch (change.Kind)
            {
                case "Added" when previous is null && Sha256(current):
                    added++;
                    break;
                case "Changed" when Sha256(previous) && Sha256(current) &&
                                    previous != current:
                    changed++;
                    break;
                case "Restored" when Sha256(previous) && Sha256(current):
                    restored++;
                    break;
                case "Missing" when Sha256(previous) && current is null:
                    missing++;
                    break;
                default:
                    throw Invalid();
            }
            if (added + changed + restored + missing > int.MaxValue)
                throw Invalid();

            // Length/escape-safe canonical JSON, domain separated and scoped
            // to source connection + immutable snapshot. No GitHub authority.
            Append(hash, JsonSerializer.Serialize(new object?[] {
                change.SourceId, change.Kind, previous,
                current, change.Revision
            }) + "\n");
            previousKey = change.SourceId;
        }

        var result = new GitHubSourceRefChangeManifest(
            sourceConnectionId, snapshotId,
            checked((int)added), checked((int)changed),
            checked((int)restored), checked((int)missing),
            Convert.ToHexStringLower(hash.GetHashAndReset()));
        Verify(result);
        return result;
    }

    private static void Append(IncrementalHash hash, string line) =>
        hash.AppendData(Encoding.UTF8.GetBytes(line));
}
