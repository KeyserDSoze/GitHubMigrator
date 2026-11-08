using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-090 is a source-only, append-only sequence of independently inspectable
// PostgreSQL staging snapshots for ONE existing F-029 operation plan.
// It NEVER constitutes a GitHub import/send or a migration completion.
public sealed record GitHubSourceSyncCheckpointOptions(bool Enabled);

public sealed class GitHubSourceSyncCheckpointException(string code)
    : Exception("Source sync checkpoint refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubSourceRefDelta(
    string SourceRepositoryId, string TargetRepositoryName,
    int Added, int Changed, int Restored, int Missing)
{
    public int Total => checked(Added + Changed + Restored + Missing);
}

public sealed record GitHubSourceSyncCheckpointProof(
    Guid PreparationId, Guid PlanId, Guid SourceConnectionId,
    Guid SnapshotId, Guid BaselineSnapshotId,
    Guid? PreviousCheckpointId, string? PreviousEvidenceSha256,
    string ManifestSha256,
    DateTimeOffset SourceCollectedAtUtc,
    DateTimeOffset StagingAppliedAtUtc,
    bool SourceInventoryComplete, bool IsBaselineSnapshot,
    int JournalAdded, int JournalChanged, int JournalRestored,
    int JournalMissing, int TotalRefChanges, int UnmappedRefChanges,
    IReadOnlyList<GitHubSourceRefDelta> Repositories);

public sealed record GitHubSourceSyncCheckpointReceipt(
    Guid Id, GitHubSourceSyncCheckpointProof Proof,
    string EvidenceSha256, Guid RecordedBy, DateTimeOffset RecordedAtUtc,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete,
    GitHubSourceRefChangeManifest? RefChangeManifest = null,
    string? RefChangeAttestationSha256 = null,
    int EvidenceVersion = 1);

public sealed record GitHubSourceSyncCheckpointPage(
    Guid PreparationId, Guid PlanId,
    IReadOnlyList<GitHubSourceSyncCheckpointReceipt> Items,
    Guid? NextAfter, Guid? LatestCheckpointId,
    Guid? NextEligibleSnapshotId, string? LatestEvidenceSha256,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitHubSourceSyncCheckpointIntegrity
{
    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void Verify(GitHubSourceSyncCheckpointProof p)
    {
        if (p.PreparationId == Guid.Empty || p.PlanId == Guid.Empty ||
            p.SourceConnectionId == Guid.Empty || p.SnapshotId == Guid.Empty ||
            p.BaselineSnapshotId == Guid.Empty ||
            p.SourceCollectedAtUtc == default ||
            p.StagingAppliedAtUtc == default ||
            !Sha(p.ManifestSha256) ||
            (p.PreviousCheckpointId is null) !=
                (p.PreviousEvidenceSha256 is null) ||
            (p.PreviousEvidenceSha256 is not null &&
                !Sha(p.PreviousEvidenceSha256)) ||
            (p.IsBaselineSnapshot !=
                (p.SnapshotId == p.BaselineSnapshotId)) ||
            (p.IsBaselineSnapshot !=
                (p.PreviousCheckpointId is null)) ||
            p.Repositories is null || p.Repositories.Count is < 1 or > 10 ||
            p.Repositories.Select(r => r.SourceRepositoryId)
                .Distinct(StringComparer.Ordinal).Count() != p.Repositories.Count ||
            p.Repositories.Select(r => r.TargetRepositoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Repositories.Count ||
            p.Repositories.Any(r =>
                string.IsNullOrWhiteSpace(r.SourceRepositoryId) ||
                string.IsNullOrWhiteSpace(r.TargetRepositoryName) ||
                r.Added < 0 || r.Changed < 0 || r.Restored < 0 ||
                r.Missing < 0) ||
            p.JournalAdded < 0 || p.JournalChanged < 0 ||
            p.JournalRestored < 0 || p.JournalMissing < 0 ||
            p.TotalRefChanges < 0 || p.UnmappedRefChanges < 0 ||
            p.TotalRefChanges > checked(
                p.JournalAdded + p.JournalChanged +
                p.JournalRestored + p.JournalMissing) ||
            p.Repositories.Sum(r => r.Total) + p.UnmappedRefChanges !=
                p.TotalRefChanges ||
            !p.Repositories.SequenceEqual(p.Repositories.OrderBy(
                r => r.SourceRepositoryId, StringComparer.Ordinal)))
            throw new GitHubSourceSyncCheckpointException(
                "SourceSyncCheckpointEvidenceInvalid");
    }

    // Fully deterministic: never includes generated receipt ID or local
    // request time. This is a stable chain of plan, previous evidence,
    // source snapshot metadata and canonical per-repository counts.
    public static string Digest(GitHubSourceSyncCheckpointProof proof)
    {
        Verify(proof);
        var material = JsonSerializer.Serialize(new object?[] {
            "f090-immutable-source-sync-checkpoint-v1",
            proof.PreparationId.ToString("D"),
            proof.PlanId.ToString("D"),
            proof.SourceConnectionId.ToString("D"),
            proof.SnapshotId.ToString("D"),
            proof.BaselineSnapshotId.ToString("D"),
            proof.PreviousCheckpointId?.ToString("D"),
            proof.PreviousEvidenceSha256,
            proof.ManifestSha256,
            proof.SourceCollectedAtUtc.ToUniversalTime().ToString("O"),
            proof.StagingAppliedAtUtc.ToUniversalTime().ToString("O"),
            proof.SourceInventoryComplete, proof.IsBaselineSnapshot,
            proof.JournalAdded, proof.JournalChanged, proof.JournalRestored,
            proof.JournalMissing, proof.TotalRefChanges,
            proof.UnmappedRefChanges,
            proof.Repositories.Select(r => new object?[] {
                r.SourceRepositoryId, r.TargetRepositoryName,
                r.Added, r.Changed, r.Restored, r.Missing
            }).ToArray()
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // Versioned extension: preserve the F-090 count-only digest and bind a
    // separate source ref fingerprint to the immutable checkpoint identity.
    public static string RefAttestationDigest(Guid checkpointId,
        string checkpointSha256, GitHubSourceRefChangeManifest manifest)
    {
        if (checkpointId == Guid.Empty || !Sha(checkpointSha256))
            throw new GitHubSourceSyncCheckpointException(
                "SourceSyncCheckpointEvidenceInvalid");
        try { GitHubSourceRefChangeManifestIntegrity.Verify(manifest); }
        catch (GitHubSourceRefChangeManifestException)
        {
            throw new GitHubSourceSyncCheckpointException(
                "SourceSyncCheckpointEvidenceInvalid");
        }
        var material = JsonSerializer.Serialize(new object[] {
            "f091-source-ref-checkpoint-attestation-v1",
            checkpointId.ToString("D"), checkpointSha256,
            manifest.SourceConnectionId.ToString("D"),
            manifest.SnapshotId.ToString("D"),
            manifest.Added, manifest.Changed, manifest.Restored,
            manifest.Missing, manifest.EvidenceSha256
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static void VerifyReceipt(GitHubSourceSyncCheckpointReceipt receipt)
    {
        if (receipt.Id == Guid.Empty || receipt.RecordedBy == Guid.Empty ||
            receipt.RecordedAtUtc == default ||
            receipt.CanPush || receipt.CanRetry ||
            receipt.CanDispatch || receipt.MigrationComplete ||
            receipt.EvidenceSha256 != Digest(receipt.Proof))
            throw new GitHubSourceSyncCheckpointException(
                "SourceSyncCheckpointHistoryCorrupt");

        if (receipt.EvidenceVersion == 1 &&
            receipt.RefChangeManifest is null &&
            receipt.RefChangeAttestationSha256 is null)
            return; // Existing F-090 records remain explicitly unattested.
        if (receipt.EvidenceVersion != 2 ||
            receipt.RefChangeManifest is not { } refs ||
            !Sha(receipt.RefChangeAttestationSha256) ||
            refs.SourceConnectionId != receipt.Proof.SourceConnectionId ||
            refs.SnapshotId != receipt.Proof.SnapshotId ||
            refs.Total != receipt.Proof.TotalRefChanges ||
            refs.Added > receipt.Proof.JournalAdded ||
            refs.Changed > receipt.Proof.JournalChanged ||
            refs.Restored > receipt.Proof.JournalRestored ||
            refs.Missing > receipt.Proof.JournalMissing ||
            receipt.RefChangeAttestationSha256 != RefAttestationDigest(
                receipt.Id, receipt.EvidenceSha256, refs))
            throw new GitHubSourceSyncCheckpointException(
                "SourceSyncCheckpointHistoryCorrupt");
    }
}

public interface IGitHubSourceSyncCheckpointJournal
{
    Task<GitHubSourceCheckpointRefChangePage> ListRefChangesAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        string? after, int take, CancellationToken ct);

    Task<GitHubSourceSyncCheckpointReceipt> CaptureAsync(
        Guid preparationId, Guid planId, Guid expectedSnapshotId,
        Guid? expectedPreviousCheckpointId, string expectedManifestSha256,
        Guid actorId, CancellationToken ct);

    Task<GitHubSourceSyncCheckpointPage> ListAsync(
        Guid preparationId, Guid planId, Guid? after, int take,
        CancellationToken ct);
}
