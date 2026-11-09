using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubSourceCheckpointGitRefProofTests
{
    private static readonly Guid Connection = Guid.NewGuid();
    private static readonly Guid Preparation = Guid.NewGuid();
    private static readonly Guid Plan = Guid.NewGuid();
    private static readonly Guid Baseline = Guid.NewGuid();
    private static readonly string Root = new('a', 64);

    private static GitHubSourceSyncCheckpointReceipt Checkpoint(
        GitHubSourceSyncCheckpointReceipt? predecessor = null,
        bool complete = true, bool legacy = false)
    {
        var snapshot = predecessor is null ? Baseline : Guid.NewGuid();
        var changed = predecessor is null ? 0 : 1;
        var proof = new GitHubSourceSyncCheckpointProof(
            Preparation, Plan, Connection, snapshot, Baseline,
            predecessor?.Id, predecessor?.EvidenceSha256,
            new string('b', 64), DateTimeOffset.Parse("2026-11-09T08:00:00Z"),
            DateTimeOffset.Parse("2026-11-09T08:01:00Z"),
            complete, predecessor is null,
            0, changed, 0, 0, changed, 0,
            [new GitHubSourceRefDelta("repo-a", "target", 0, changed, 0, 0)]);
        var digest = GitHubSourceSyncCheckpointIntegrity.Digest(proof);
        var manifest = new GitHubSourceRefChangeManifest(
            Connection, snapshot, 0, changed, 0, 0, new string('c', 64));
        var id = Guid.NewGuid();
        return new GitHubSourceSyncCheckpointReceipt(
            id, proof, digest, Guid.NewGuid(),
            DateTimeOffset.Parse("2026-11-09T08:02:00Z"),
            false, false, false, false,
            legacy ? null : manifest,
            legacy ? null : GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
                id, digest, manifest),
            legacy ? 1 : 2);
    }

    private static VerifiedSourceGitRefs Source(
        GitHubSourceSyncCheckpointReceipt checkpoint, int oidLength = 40,
        string? oid = null)
        => VerifiedSourceGitRefIntegrity.Seal(
            checkpoint.Proof.SnapshotId, Connection, "repo-a", Root,
            [
                new GitMirrorRef("refs/heads/main", oid ?? new string('1', oidLength)),
                new GitMirrorRef("refs/tags/v1", new string('2', oidLength))
            ]);

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Complete_baseline_and_incremental_full_oid_proofs_are_sealed(
        int oidLength)
    {
        var baseline = Checkpoint();
        var source = Source(baseline, oidLength);
        var original = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            baseline, source);
        GitHubSourceCheckpointGitRefProofIntegrity.Verify(
            original, baseline, source);
        Assert.Equal(1, original.EvidenceVersion);
        Assert.Equal(64, original.EvidenceSha256.Length);
        Assert.False(original.CanPush);
        Assert.False(original.CanDispatch);
        Assert.False(original.CanRetry);
        Assert.False(original.MigrationComplete);

        var reversed = VerifiedSourceGitRefIntegrity.Seal(
            baseline.Proof.SnapshotId, Connection, "repo-a", Root,
            [source.Refs[1] with { ObjectId = source.Refs[1].ObjectId.ToUpperInvariant() },
             source.Refs[0]]);
        Assert.Equal(original.EvidenceSha256,
            GitHubSourceCheckpointGitRefProofIntegrity.Seal(
                baseline, reversed).EvidenceSha256);

        var next = Checkpoint(baseline);
        var nextSource = Source(next, oidLength, new string('3', oidLength));
        var advanced = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            next, nextSource, original);
        GitHubSourceCheckpointGitRefProofIntegrity.Verify(
            advanced, next, nextSource, original);
        Assert.Equal(original.EvidenceSha256, advanced.PreviousFullRefProofSha256);
        Assert.Equal(original.CheckpointId, advanced.PreviousCheckpointId);
        Assert.NotEqual(original.EvidenceSha256, advanced.EvidenceSha256);
        Assert.NotEqual(source.Refs[0].ObjectId, advanced.Refs[0].ObjectId);
    }

    [Fact]
    public void Only_F091_v2_with_sealed_Git_refs_can_pass_even_with_partial_ACLs()
    {
        var incompletePermissions = Checkpoint(complete: false);
        var provenRefs = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            incompletePermissions, Source(incompletePermissions));
        Assert.False(incompletePermissions.Proof.SourceInventoryComplete);
        GitHubSourceCheckpointGitRefProofIntegrity.Verify(
            provenRefs, incompletePermissions, Source(incompletePermissions));

        var legacy = Checkpoint(legacy: true);
        Assert.Equal("SourceCheckpointGitRefsInvalid",
            Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
                GitHubSourceCheckpointGitRefProofIntegrity.Seal(
                    legacy, Source(legacy))).Code);

        var baseline = Checkpoint();
        var source = Source(baseline);
        foreach (var invalid in new[] {
            source with { RefManifestRootSha256 = null },
            source with { SnapshotId = Guid.NewGuid() },
            source with { SourceEvidenceSha256 = new string('f', 64) },
            source with { SourceRepositoryId = "another" }
        })
            Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
                GitHubSourceCheckpointGitRefProofIntegrity.Seal(
                    baseline, invalid));
    }

    [Fact]
    public void Incremental_epochs_require_the_exact_authentic_predecessor()
    {
        var baseline = Checkpoint();
        var original = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            baseline, Source(baseline));
        var next = Checkpoint(baseline);
        var source = Source(next);
        Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
            GitHubSourceCheckpointGitRefProofIntegrity.Seal(next, source));
        Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
            GitHubSourceCheckpointGitRefProofIntegrity.Seal(next, source,
                original with { EvidenceSha256 = new string('f', 64) }));
        var unrelatedCheckpoint = Checkpoint();
        var unrelated = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            unrelatedCheckpoint, Source(unrelatedCheckpoint));
        Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
            GitHubSourceCheckpointGitRefProofIntegrity.Seal(
                next, source, unrelated));
        Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
            GitHubSourceCheckpointGitRefProofIntegrity.Seal(
                baseline, Source(baseline), original));
    }

    [Fact]
    public void Changes_to_any_proof_identity_oid_or_authority_flag_are_rejected()
    {
        var checkpoint = Checkpoint();
        var source = Source(checkpoint);
        var proof = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            checkpoint, source);
        foreach (var changed in new[] {
            proof with { CheckpointId = Guid.NewGuid() },
            proof with { SourceManifestRootSha256 = new string('f', 64) },
            proof with { SourceEvidenceSha256 = new string('f', 64) },
            proof with { CheckpointRefAttestationSha256 = new string('f', 64) },
            proof with { EvidenceSha256 = new string('f', 64) },
            proof with { CanPush = true },
            proof with { CanRetry = true },
            proof with { CanDispatch = true },
            proof with { MigrationComplete = true },
            proof with { Refs = [
                new("refs/heads/main", new string('5', 40)),
                proof.Refs[1]] }
        })
            Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
                GitHubSourceCheckpointGitRefProofIntegrity.Verify(
                    changed, checkpoint, source));
    }

    [Fact]
    public void Ambiguous_mixed_or_unbounded_git_oid_sets_are_refused()
    {
        var checkpoint = Checkpoint();
        var trusted = Source(checkpoint);
        foreach (var refs in new IReadOnlyList<GitMirrorRef>[] {
            [],
            [new("refs/heads/main", new string('1', 40)),
             new("refs/tags/v1", new string('2', 64))],
            [new("refs/heads/Main", new string('1', 40)),
             new("refs/heads/main", new string('2', 40))],
            Enumerable.Range(0,
                GitHubSourceCheckpointGitRefProofIntegrity.MaxRefs + 1)
                .Select(i => new GitMirrorRef(
                    "refs/heads/test-" + i, new string('1', 40))).ToArray()
        })
        {
            // Forged/invalid ref observations should fail at the new F-097
            // boundary, even if they bypass the earlier F-032 constructor.
            Assert.Throws<GitHubSourceCheckpointGitRefProofException>(() =>
                GitHubSourceCheckpointGitRefProofIntegrity.Seal(
                    checkpoint, trusted with { Refs = refs }));
        }
    }
}
