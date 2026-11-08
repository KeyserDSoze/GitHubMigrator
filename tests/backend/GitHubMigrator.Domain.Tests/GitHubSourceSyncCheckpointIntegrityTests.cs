using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubSourceSyncCheckpointIntegrityTests
{
    private static GitHubSourceSyncCheckpointProof Baseline()
    {
        var snapshot = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), snapshot, snapshot, null, null,
            new string('a', 64), at.AddMinutes(-1), at,
            false, true, 3, 1, 0, 1, 4, 1,
            [
                new("repo-1", "migration-one", 2, 1, 0, 0),
                new("repo-2", "migration-two", 0, 0, 0, 0)
            ]);
    }

    [Fact]
    public void F090_digest_is_canonical_and_stable_across_restart()
    {
        var proof = Baseline();
        var first = GitHubSourceSyncCheckpointIntegrity.Digest(proof);
        var json = System.Text.Json.JsonSerializer.Serialize(proof);
        var copy = System.Text.Json.JsonSerializer.Deserialize<
            GitHubSourceSyncCheckpointProof>(json)!;
        Assert.Equal(first,
            GitHubSourceSyncCheckpointIntegrity.Digest(copy));
        Assert.Equal(64, first.Length);
        var receipt = new GitHubSourceSyncCheckpointReceipt(
            Guid.NewGuid(), copy, first,
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            false, false, false, false);
        GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(receipt);
        Assert.False(receipt.CanPush);
        Assert.False(receipt.CanRetry);
        Assert.False(receipt.MigrationComplete);
    }

    [Fact]
    public void F090_cannot_forge_counts_skip_source_or_reorder_repo_evidence()
    {
        var p = Baseline();
        var a = p.Repositories[0];
        var b = p.Repositories[1];
        foreach (var forged in new[] {
            p with { SnapshotId = Guid.NewGuid() },
            p with { IsBaselineSnapshot = false },
            p with { PreviousCheckpointId = Guid.NewGuid() },
            p with { PreviousEvidenceSha256 = new string('b', 64) },
            p with { UnmappedRefChanges = -1 },
            p with { TotalRefChanges = 0 },
            p with { Repositories = [b, a] },
            p with { Repositories = [a, a] },
            p with { Repositories = [a with { Added = -1 }, b] },
            p with { Repositories = [a with { Missing = 8 }, b] },
            p with { ManifestSha256 = new string('q', 64) }
        })
            Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
                GitHubSourceSyncCheckpointIntegrity.Verify(forged));
    }

    [Fact]
    public void F090_previous_SHA_binds_new_checkpoint_and_never_authorizes_transmission()
    {
        var first = Baseline();
        var predecessor = Guid.NewGuid();
        var second = first with {
            SnapshotId = Guid.NewGuid(),
            PreviousCheckpointId = predecessor,
            PreviousEvidenceSha256 =
                GitHubSourceSyncCheckpointIntegrity.Digest(first),
            IsBaselineSnapshot = false,
            SourceCollectedAtUtc =
                first.SourceCollectedAtUtc.AddMinutes(3),
            StagingAppliedAtUtc = first.StagingAppliedAtUtc.AddMinutes(3),
            Repositories = [
                new("repo-1", "migration-one", 0, 1, 0, 0),
                new("repo-2", "migration-two", 1, 0, 0, 0)
            ],
            JournalAdded = 1, JournalChanged = 1,
            JournalMissing = 0, TotalRefChanges = 2, UnmappedRefChanges = 0
        };
        var digest = GitHubSourceSyncCheckpointIntegrity.Digest(second);
        Assert.NotEqual(
            GitHubSourceSyncCheckpointIntegrity.Digest(first), digest);
        Assert.NotEqual(digest, GitHubSourceSyncCheckpointIntegrity.Digest(
            second with { PreviousEvidenceSha256 = new string('b', 64) }));
        var receipt = new GitHubSourceSyncCheckpointReceipt(
            Guid.NewGuid(), second, digest,
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            false, false, false, false);
        GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(receipt);
        foreach (var bad in new[] {
            receipt with { CanPush = true },
            receipt with { CanRetry = true },
            receipt with { CanDispatch = true },
            receipt with { MigrationComplete = true },
            receipt with { EvidenceSha256 = new string('0', 64) }
        })
            Assert.Throws<GitHubSourceSyncCheckpointException>(() =>
                GitHubSourceSyncCheckpointIntegrity.VerifyReceipt(bad));
    }
}
