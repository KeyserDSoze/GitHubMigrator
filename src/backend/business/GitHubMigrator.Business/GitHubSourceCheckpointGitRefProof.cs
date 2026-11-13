using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-097a: internal, source-only proof core. The caller MUST independently
// verify the complete F-014-style snapshot and F-091 PostgreSQL checkpoint.
// A digest is not evidence that an arbitrary ref/OID input came from Azure.
public sealed record GitHubSourceCheckpointGitRefProof(
    int EvidenceVersion, Guid CheckpointId, Guid PreparationId, Guid PlanId,
    Guid SourceConnectionId, Guid SnapshotId, Guid BaselineSnapshotId,
    string SourceRepositoryId, string CheckpointEvidenceSha256,
    string CheckpointRefAttestationSha256, string SourceEvidenceSha256,
    string SourceManifestRootSha256, Guid? PreviousCheckpointId,
    string? PreviousFullRefProofSha256, IReadOnlyList<GitMirrorRef> Refs,
    string EvidenceSha256, bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);

public sealed class GitHubSourceCheckpointGitRefProofException(string code)
    : Exception("Source checkpoint full Git ref/OID evidence rejected")
{
    public string Code { get; } = code;
}

public static class GitHubSourceCheckpointGitRefProofIntegrity
{
    public const int MaxRefs = GitIncrementalAncestryIntegrity.MaxRefsPerSide;
    private const string Domain = "f097-source-checkpoint-full-ref-oids-v1";

    private static GitHubSourceCheckpointGitRefProofException Invalid() =>
        new("SourceCheckpointGitRefsInvalid");

    private static bool Sha(string? text) => text is { Length: 64 } &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitHubSourceCheckpointGitRefProof Seal(
        GitHubSourceSyncCheckpointReceipt checkpoint,
        VerifiedSourceGitRefs source,
        GitHubSourceCheckpointGitRefProof? predecessor = null)
    {
        if (checkpoint is null || source is null)
            throw Invalid();
        try { GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(checkpoint); }
        catch (GitHubSourceSyncCheckpointException) { throw Invalid(); }

        var epoch = checkpoint.Proof;
        if (checkpoint.EvidenceVersion != 2 ||
            checkpoint.RefChangeManifest is null ||
            !Sha(checkpoint.RefChangeAttestationSha256) ||
            // Global F-013 completeness also includes effective ACL grants.
            // F-014's independently sealed Git ref coverage is required by
            // the trusted PostgreSQL reader instead; a partial ACL scan
            // must not turn complete, verified Git refs into unknown OIDs.
            source.SnapshotId != epoch.SnapshotId ||
            source.SourceConnectionId != epoch.SourceConnectionId ||
            source.SourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(source.SourceRepositoryId) ||
            !Sha(source.RefManifestRootSha256) ||
            !Sha(source.SourceEvidenceSha256) ||
            source.Refs is null ||
            source.Refs.Count is < 1 or > MaxRefs)
            throw Invalid();

        IReadOnlyList<GitMirrorRef> canonical;
        try
        {
            canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(source.Refs);
            var recomputed = VerifiedSourceGitRefIntegrity.Seal(
                source.SnapshotId, source.SourceConnectionId,
                source.SourceRepositoryId, source.RefManifestRootSha256,
                source.Refs);
            if (!canonical.SequenceEqual(source.Refs) ||
                recomputed.SourceEvidenceSha256 != source.SourceEvidenceSha256)
                throw Invalid();
        }
        catch (GitMirrorRefReconciliationException) { throw Invalid(); }
        catch (IncrementalGitRefReconciliationException) { throw Invalid(); }

        // A complete manifest may be a SHA-1 or SHA-256 repository, never
        // a mix. An empty ref list cannot support the F-094 graph audit.
        if (canonical.Any(r => r.ObjectId.Length != canonical[0].ObjectId.Length))
            throw Invalid();

        if (epoch.IsBaselineSnapshot)
        {
            if (predecessor is not null)
                throw Invalid();
        }
        else
        {
            if (predecessor is null)
                throw Invalid();
            VerifyEnvelope(predecessor);
            if (predecessor.CheckpointId != epoch.PreviousCheckpointId ||
                predecessor.CheckpointEvidenceSha256 !=
                    epoch.PreviousEvidenceSha256 ||
                predecessor.PreparationId != epoch.PreparationId ||
                predecessor.PlanId != epoch.PlanId ||
                predecessor.SourceConnectionId != epoch.SourceConnectionId ||
                predecessor.BaselineSnapshotId != epoch.BaselineSnapshotId ||
                predecessor.SourceRepositoryId != source.SourceRepositoryId ||
                predecessor.SnapshotId == epoch.SnapshotId ||
                predecessor.Refs[0].ObjectId.Length != canonical[0].ObjectId.Length)
                throw Invalid();
        }

        var proof = new GitHubSourceCheckpointGitRefProof(
            1, checkpoint.Id, epoch.PreparationId, epoch.PlanId,
            epoch.SourceConnectionId, epoch.SnapshotId,
            epoch.BaselineSnapshotId, source.SourceRepositoryId,
            checkpoint.EvidenceSha256, checkpoint.RefChangeAttestationSha256!,
            source.SourceEvidenceSha256, source.RefManifestRootSha256!,
            epoch.PreviousCheckpointId, predecessor?.EvidenceSha256,
            canonical.ToArray(), "", false, false, false, false);
        var sealedProof = proof with { EvidenceSha256 = Digest(proof) };
        VerifyEnvelope(sealedProof);
        return sealedProof;
    }

    // Must revalidate the independently loaded checkpoint, source snapshot
    // manifest and predecessor. Self-verified receipts are NOT authority to
    // reconstruct ref/OID sets from F-025 fingerprint-only journal rows.
    public static void Verify(
        GitHubSourceCheckpointGitRefProof proof,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        VerifiedSourceGitRefs source,
        GitHubSourceCheckpointGitRefProof? predecessor = null)
    {
        VerifyEnvelope(proof);
        var expected = Seal(checkpoint, source, predecessor);
        if (!proof.Refs.SequenceEqual(expected.Refs) ||
            proof with { Refs = expected.Refs } != expected)
            throw Invalid();
    }

    private static void VerifyEnvelope(GitHubSourceCheckpointGitRefProof p)
    {
        if (p is null || p.EvidenceVersion != 1 ||
            p.CheckpointId == Guid.Empty || p.PreparationId == Guid.Empty ||
            p.PlanId == Guid.Empty || p.SourceConnectionId == Guid.Empty ||
            p.SnapshotId == Guid.Empty || p.BaselineSnapshotId == Guid.Empty ||
            p.SourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(p.SourceRepositoryId) ||
            !Sha(p.CheckpointEvidenceSha256) ||
            !Sha(p.CheckpointRefAttestationSha256) ||
            !Sha(p.SourceEvidenceSha256) ||
            !Sha(p.SourceManifestRootSha256) ||
            !Sha(p.EvidenceSha256) ||
            p.PreviousCheckpointId == Guid.Empty ||
            (p.PreviousCheckpointId is null) !=
                (p.PreviousFullRefProofSha256 is null) ||
            (p.PreviousFullRefProofSha256 is not null &&
                !Sha(p.PreviousFullRefProofSha256)) ||
            ((p.SnapshotId == p.BaselineSnapshotId) !=
                (p.PreviousCheckpointId is null)) ||
            p.Refs is null || p.Refs.Count is < 1 or > MaxRefs ||
            p.CanPush || p.CanRetry || p.CanDispatch || p.MigrationComplete)
            throw Invalid();
        try
        {
            var canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(p.Refs);
            if (!canonical.SequenceEqual(p.Refs) ||
                canonical.Any(x => x.ObjectId.Length != canonical[0].ObjectId.Length))
                throw Invalid();
        }
        catch (GitMirrorRefReconciliationException) { throw Invalid(); }
        if (p.EvidenceSha256 != Digest(p))
            throw Invalid();
    }

    private static string Digest(GitHubSourceCheckpointGitRefProof p)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new object?[] {
            Domain, p.EvidenceVersion,
            p.CheckpointId.ToString("D"),
            p.PreparationId.ToString("D"), p.PlanId.ToString("D"),
            p.SourceConnectionId.ToString("D"), p.SnapshotId.ToString("D"),
            p.BaselineSnapshotId.ToString("D"), p.SourceRepositoryId,
            p.CheckpointEvidenceSha256, p.CheckpointRefAttestationSha256,
            p.SourceEvidenceSha256, p.SourceManifestRootSha256,
            p.PreviousCheckpointId?.ToString("D"),
            p.PreviousFullRefProofSha256,
            p.Refs[0].ObjectId.Length == 40 ? "sha1" : "sha256",
            p.Refs.Count, false, false, false, false
        });
        foreach (var reference in p.Refs)
            Append(new object?[] { reference.Name, reference.ObjectId });
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(object?[] fields) => hash.AppendData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields) + "\n"));
    }
}
