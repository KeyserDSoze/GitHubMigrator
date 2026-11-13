using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubSourceSyncCheckpointRefAttestationTests
{
    private static (GitHubSourceSyncCheckpointReceipt Receipt,
        GitHubSourceRefChangeManifest Manifest) Sample()
    {
        var source = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var proof = new GitHubSourceSyncCheckpointProof(
            Guid.NewGuid(), Guid.NewGuid(), source, snapshot, snapshot,
            null, null, new string('a', 64),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            true, true, 2, 1, 0, 0, 3, 0,
            [new GitHubSourceRefDelta("repo-1", "target-1", 2, 1, 0, 0)]);
        var manifest = new GitHubSourceRefChangeManifest(
            source, snapshot, 2, 1, 0, 0, new string('b', 64));
        var id = Guid.NewGuid();
        var f090Sha = GitHubSourceSyncCheckpointIntegrity.Digest(proof);
        var f091Sha = GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
            id, f090Sha, manifest);
        return (new GitHubSourceSyncCheckpointReceipt(id, proof, f090Sha,
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            false, false, false, false, manifest, f091Sha, 2), manifest);
    }

    [Fact]
    public void F091_v2_digest_binds_identity_source_epoch_and_all_change_counts()
    {
        var (receipt, refs) = Sample();
        GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(receipt);
        Assert.Equal(2, receipt.EvidenceVersion);
        Assert.Equal(64, receipt.RefChangeAttestationSha256!.Length);
        var digest = receipt.RefChangeAttestationSha256;
        Assert.NotEqual(digest,
            GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
                Guid.NewGuid(), receipt.EvidenceSha256, refs));
        Assert.NotEqual(digest,
            GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
                receipt.Id, receipt.EvidenceSha256,
                refs with { EvidenceSha256 = new string('c', 64) }));
        Assert.NotEqual(digest,
            GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
                receipt.Id, receipt.EvidenceSha256,
                refs with { Changed = 0, Added = 3 }));
    }

    [Fact]
    public void F091_missing_or_forged_v2_manifest_fails_closed()
    {
        var (receipt, refs) = Sample();
        var tampered = new[] {
            receipt with { EvidenceVersion = 3 },
            receipt with { EvidenceVersion = 1 },
            receipt with { RefChangeManifest = null },
            receipt with { RefChangeAttestationSha256 = null },
            receipt with { RefChangeAttestationSha256 = new string('d', 64) },
            receipt with { RefChangeManifest = refs with {
                SnapshotId = Guid.NewGuid() } },
            receipt with { RefChangeManifest = refs with { Added = 0 } },
            receipt with { RefChangeManifest = refs with {
                EvidenceSha256 = new string('c', 64) } },
            receipt with { CanPush = true },
            receipt with { CanRetry = true },
            receipt with { CanDispatch = true },
            receipt with { MigrationComplete = true }
        };
        foreach (var bad in tampered)
            Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
                GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(bad));
    }

    [Fact]
    public void F091_original_F090_receipt_is_explicitly_legacy_not_mislabeled()
    {
        var (v2, _) = Sample();
        var legacy = v2 with {
            EvidenceVersion = 1,
            RefChangeManifest = null,
            RefChangeAttestationSha256 = null
        };
        GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(legacy);
        Assert.Null(legacy.RefChangeManifest);
        Assert.Null(legacy.RefChangeAttestationSha256);
        Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
            GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(
                legacy with { RefChangeAttestationSha256 = new string('a', 64) }));
    }
}
