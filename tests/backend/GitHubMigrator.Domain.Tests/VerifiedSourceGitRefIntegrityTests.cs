using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class VerifiedSourceGitRefIntegrityTests
{
    private static readonly Guid SnapshotId =
        Guid.Parse("12345678-1234-4321-8234-123456789abc");
    private static readonly Guid SourceId =
        Guid.Parse("87654321-4321-4321-8321-abcdefabcdef");
    private const string Oid = "1234567890abcdef1234567890abcdef12345678";
    private const string NextOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string Root = new('a', 64);

    [Fact]
    public void Snapshot_proof_is_deterministic_for_ref_order_and_hex_case()
    {
        var refs = new[] {
            new GitMirrorRef("refs/tags/v1", NextOid),
            new GitMirrorRef("refs/heads/main", Oid)
        };
        var baseline = VerifiedSourceGitRefIntegrity.Seal(
            SnapshotId, SourceId, "repo", Root, refs);
        var reordered = VerifiedSourceGitRefIntegrity.Seal(
            SnapshotId, SourceId, "repo", Root,
            [refs[1] with { ObjectId = Oid.ToUpperInvariant() }, refs[0]]);
        Assert.Equal(2, baseline.Refs.Count);
        Assert.Equal("refs/heads/main", baseline.Refs[0].Name);
        Assert.Equal(baseline.SourceEvidenceSha256, reordered.SourceEvidenceSha256);
        Assert.Equal(64, baseline.SourceEvidenceSha256.Length);
        Assert.Equal(Root, baseline.RefManifestRootSha256);
    }

    [Fact]
    public void Snapshot_id_connection_repo_root_and_every_ref_oid_are_proof_bound()
    {
        var refs = new[] { new GitMirrorRef("refs/heads/main", Oid) };
        var original = VerifiedSourceGitRefIntegrity.Seal(
            SnapshotId, SourceId, "repo", Root, refs).SourceEvidenceSha256;
        string Hash(Guid snapshot, Guid connection, string repo, string? root,
            IReadOnlyList<GitMirrorRef> input) =>
            VerifiedSourceGitRefIntegrity.Seal(snapshot, connection,
                repo, root, input).SourceEvidenceSha256;

        Assert.NotEqual(original, Hash(Guid.NewGuid(), SourceId, "repo", Root, refs));
        Assert.NotEqual(original, Hash(SnapshotId, Guid.NewGuid(), "repo", Root, refs));
        Assert.NotEqual(original, Hash(SnapshotId, SourceId, "another", Root, refs));
        Assert.NotEqual(original, Hash(SnapshotId, SourceId, "repo", new string('b',64), refs));
        Assert.NotEqual(original, Hash(SnapshotId, SourceId, "repo", null, refs));
        Assert.NotEqual(original, Hash(SnapshotId, SourceId, "repo", Root,
            [new GitMirrorRef("refs/heads/main", NextOid)]));
    }

    [Fact]
    public void Invalid_source_evidence_never_yields_an_acceptable_snapshot_proof()
    {
        Assert.Equal("SourceRefEvidenceInvalid",
            Assert.Throws<IncrementalGitRefReconciliationException>(() =>
                VerifiedSourceGitRefIntegrity.Seal(SnapshotId, SourceId, "repo",
                    "not-a-root", [])).Code);
        Assert.Equal("SourceRefEvidenceInvalid",
            Assert.Throws<IncrementalGitRefReconciliationException>(() =>
                VerifiedSourceGitRefIntegrity.Seal(SnapshotId, SourceId, "repo",
                    Root, [
                        new("refs/heads/Main", Oid),
                        new("refs/heads/main", NextOid)
                    ])).Code);
        Assert.Equal("SourceRefEvidenceInvalid",
            Assert.Throws<IncrementalGitRefReconciliationException>(() =>
                VerifiedSourceGitRefIntegrity.Seal(SnapshotId, SourceId, "repo",
                    Root, [new("refs/pull/1/head", Oid)])).Code);
    }

    [Fact]
    public void Empty_but_verified_source_remains_a_distinct_proof()
    {
        var empty = VerifiedSourceGitRefIntegrity.Seal(
            SnapshotId, SourceId, "repo", Root, []);
        Assert.Empty(empty.Refs);
        Assert.Equal(64, empty.SourceEvidenceSha256.Length);
        Assert.NotEqual(empty.SourceEvidenceSha256,
            VerifiedSourceGitRefIntegrity.Seal(
                SnapshotId, SourceId, "repo", null, []).SourceEvidenceSha256);
    }
}
