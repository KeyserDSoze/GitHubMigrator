using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// The DB must independently verify every F-014/F-091/F-097b input.
// These pure proof tests check binding only, never provider privileges.
public sealed class GitCheckpointTargetReReviewIntegrityTests
{
    private static (
        IncrementalOperationPlan Plan,
        GitHubSourceSyncCheckpointReceipt Checkpoint,
        GitHubSourceCheckpointGitRefProof Source,
        Guid ReviewerId, Guid ActorId) Make(int length)
    {
        var p = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var srcId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        var reviewedBy = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var digest = new string('a', 64);
        var now = DateTimeOffset.Parse("2026-11-09T13:00:00Z");
        var plan = new IncrementalOperationPlan(
            planId, p, baselineId, srcId,
            Guid.NewGuid(), "gh-org", digest, new string('b', 64),
            new string('c', 64), new string('d', 64), now,
            Guid.NewGuid(), 1, 6, 2, 0, false, false, [],
            [new IncrementalOperationStep(1, null, "repo-a", "sandbox",
                "ReviewTargetName", "Blocked", "Blocked", 1)]);

        GitHubSourceSyncCheckpointReceipt Checkpoint(
            Guid snapshot, GitHubSourceSyncCheckpointReceipt? parent)
        {
            var count = parent is null ? 0 : 1;
            var proof = new GitHubSourceSyncCheckpointProof(
                p, planId, srcId, snapshot, baselineId, parent?.Id,
                parent?.EvidenceSha256, plan.ManifestSha256,
                now, now.AddSeconds(1), false, parent is null,
                0, count, 0, 0, count, 0,
                [new GitHubSourceRefDelta("repo-a", "sandbox",
                    0, count, 0, 0)]);
            var sha = GitHubSourceSyncCheckpointIntegrity.Digest(proof);
            var manifest = new GitHubSourceRefChangeManifest(
                srcId, snapshot, 0, count, 0, 0, new string('e', 64));
            var id = Guid.NewGuid();
            return new(id, proof, sha, actor, now,
                false, false, false, false, manifest,
                GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
                    id, sha, manifest), 2);
        }

        VerifiedSourceGitRefs RefSet(Guid snapshot, char tip) =>
            VerifiedSourceGitRefIntegrity.Seal(
                snapshot, srcId, "repo-a", new string('f', 64),
                [new("refs/heads/main", new string(tip, length)),
                 new("refs/tags/v1", new string('2', length))]);

        var initial = Checkpoint(baselineId, null);
        var firstSource = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            initial, RefSet(baselineId, '1'));
        var later = Checkpoint(Guid.NewGuid(), initial);
        var laterSource = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            later, RefSet(later.Proof.SnapshotId, '3'), firstSource);
        return (plan, later, laterSource, reviewedBy, actor);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Distinct_later_checkpoint_is_cryptographically_bound_but_never_authorizes_a_GET(
        int length)
    {
        var x = Make(length);
        var a = GitCheckpointTargetReReviewIntegrity.Seal(
            x.Plan, x.Checkpoint, x.Source, x.ReviewerId, x.ActorId);
        var b = GitCheckpointTargetReReviewIntegrity.Seal(
            x.Plan, x.Checkpoint, x.Source, x.ReviewerId, x.ActorId);
        Assert.Equal(a, b);
        Assert.Equal(1, a.EvidenceVersion);
        Assert.NotEqual(x.Plan.SnapshotId, a.SourceSnapshotId);
        Assert.Equal(x.Source.EvidenceSha256, a.SourceFullRefProofSha256);
        Assert.Equal(x.Source.SourceManifestRootSha256,
            a.SourceManifestRootSha256);
        Assert.Equal(length == 40 ? "sha1" : "sha256", a.ObjectFormat);
        Assert.Equal(2, a.SourceRefCount);
        Assert.Equal(64, a.EvidenceSha256.Length);
        Assert.False(a.CanObserveTarget || a.CanPush ||
            a.CanRetry || a.CanDispatch || a.MigrationComplete);
        GitCheckpointTargetReReviewIntegrity.Verify(
            a, x.Plan, x.Checkpoint, x.Source, x.ReviewerId);

        var newActor = GitCheckpointTargetReReviewIntegrity.Seal(
            x.Plan, x.Checkpoint, x.Source, x.ReviewerId, Guid.NewGuid());
        Assert.NotEqual(a.EvidenceSha256, newActor.EvidenceSha256);
        foreach (var modified in new[] {
            a with { CanObserveTarget = true },
            a with { CanPush = true },
            a with { CheckpointId = Guid.NewGuid() },
            a with { OriginalReviewerId = Guid.NewGuid() },
            a with { SourceManifestRootSha256 = new string('a', 64) },
            a with { EvidenceSha256 = new string('0', 64) }
        })
            Assert.Equal("CheckpointTargetReReviewEvidenceInvalid",
                Assert.Throws<GitCheckpointTargetReReviewException>(() =>
                    GitCheckpointTargetReReviewIntegrity.Verify(
                        modified, x.Plan, x.Checkpoint,
                        x.Source, x.ReviewerId)).Code);

        Assert.Equal("CheckpointTargetReReviewEvidenceInvalid",
            Assert.Throws<GitCheckpointTargetReReviewException>(() =>
                GitCheckpointTargetReReviewIntegrity.Seal(
                    x.Plan with { IsCurrent = true },
                    x.Checkpoint, x.Source,
                    x.ReviewerId, x.ActorId)).Code);
        Assert.Equal("CheckpointTargetReReviewEvidenceInvalid",
            Assert.Throws<GitCheckpointTargetReReviewException>(() =>
                GitCheckpointTargetReReviewIntegrity.Seal(
                    x.Plan, x.Checkpoint,
                    x.Source with { SnapshotId = x.Plan.SnapshotId },
                    x.ReviewerId, x.ActorId)).Code);
        Assert.Equal("CheckpointTargetReReviewEvidenceInvalid",
            Assert.Throws<GitCheckpointTargetReReviewException>(() =>
                GitCheckpointTargetReReviewIntegrity.Seal(
                    x.Plan, x.Checkpoint,
                    x.Source with { CanDispatch = true },
                    x.ReviewerId, x.ActorId)).Code);
    }
}
