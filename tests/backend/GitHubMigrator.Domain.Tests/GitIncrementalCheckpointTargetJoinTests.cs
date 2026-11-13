using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// Synthetic domain contracts only. Real latest-epoch source proof, human
// audit and F-031 persistence MUST still be verified independently in
// PostgreSQL, under the source-first advisory lock, before F-032/F-038.
public sealed class GitIncrementalCheckpointTargetJoinTests
{
    private static (
        IncrementalOperationPlan Plan,
        GitHubSourceSyncCheckpointReceipt Checkpoint,
        GitHubSourceCheckpointGitRefProof Source,
        GitCheckpointTargetObservation Target) Fixture(int oidLength)
    {
        var now = new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);
        var prepId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sourceConnection = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var baseline = Guid.NewGuid();
        var later = Guid.NewGuid();
        var preceding = Guid.NewGuid();
        var manifest = new string('a', 64);
        var plan = new IncrementalOperationPlan(planId, prepId, baseline,
            sourceConnection, destination, "gh-org",
            new string('b', 64), new string('c', 64),
            new string('d', 64), manifest, now, Guid.NewGuid(), 1, 6,
            1, 0, false, false, [],
            [new IncrementalOperationStep(1, null, "repo-1", "sandbox",
                "ReviewTargetName", "Blocked", "Blocked", 0)]);
        var epoch = new GitHubSourceSyncCheckpointProof(
            prepId, planId, sourceConnection, later, baseline,
            preceding, new string('e', 64), manifest,
            now.AddMinutes(-10), now.AddMinutes(-9),
            true, false, 0, 0, 0, 0, 0, 0,
            [new GitHubSourceRefDelta("repo-1", "sandbox", 0, 0, 0, 0)]);
        var checkpointId = Guid.NewGuid();
        var checkpointDigest = GitHubSourceSyncCheckpointIntegrity.Digest(epoch);
        var refChanges = new GitHubSourceRefChangeManifest(
            sourceConnection, later, 0, 0, 0, 0, new string('f', 64));
        var attestation = GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
            checkpointId, checkpointDigest, refChanges);
        var checkpoint = new GitHubSourceSyncCheckpointReceipt(
            checkpointId, epoch, checkpointDigest, Guid.NewGuid(),
            now.AddMinutes(-8), false, false, false, false,
            refChanges, attestation, 2);
        GitHubSourceCheckpointGitRefProof source = new(
            1, checkpointId, prepId, planId, sourceConnection, later,
            baseline, "repo-1", checkpointDigest, attestation,
            new string('b', 64), new string('c', 64),
            preceding, new string('d', 64),
            [new GitMirrorRef("refs/heads/main", new string('1', oidLength)),
             new GitMirrorRef("refs/tags/v1", new string('2', oidLength))],
            new string('e', 64), false, false, false, false);
        var preflight = new GitCheckpointTargetReadPreflight(
            1, prepId, planId, checkpointId, later, destination,
            "repo-1", "sandbox", "gh-org",
            oidLength == 40 ? "sha1" : "sha256", 2, Guid.NewGuid(),
            new string('6', 64), new string('7', 64),
            now.AddMinutes(-4), now.AddMinutes(1),
            new string('8', 64), false, false, false, false, false, false);
        var observed = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", now.AddMinutes(-3),
            [new GitHubObservedRef("refs/heads/main",
                new string('1', oidLength), "commit")]);
        var target = GitCheckpointTargetObservationIntegrity.Seal(
            preflight, observed, Guid.NewGuid());
        return (plan, checkpoint, source, target);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Later_epoch_F031_join_is_v2_read_only_and_never_revives_F029(
        int oidLength)
    {
        var x = Fixture(oidLength);
        var actual = GitIncrementalCheckpointTargetJoin.ForReadOnlyComparison(
            x.Plan, x.Checkpoint, x.Source, x.Target);
        Assert.Equal(2, actual.EvidenceVersion);
        Assert.False(x.Plan.IsCurrent);
        Assert.Equal(x.Source.EvidenceSha256, actual.SourceFullRefProofSha256);
        Assert.Equal(x.Target.EvidenceSha256, actual.TargetEvidenceSha256);
        Assert.NotEqual(x.Target.TargetEvidenceSha256,
            actual.TargetEvidenceSha256);
        Assert.Equal(x.Target.Id, actual.TargetObservationId);
        Assert.Equal(x.Checkpoint.Id, actual.SourceCheckpointId);
        Assert.Equal(2, actual.ExpectedSourceRefs.Count);
        Assert.Single(actual.ExpectedTargetRefs);
        Assert.False(actual.CanPush || actual.CanRetry ||
            actual.CanDispatch || actual.MigrationComplete);
        var result = GitMirrorRefReconciler.Compare(new(
            x.Plan.Id, x.Plan.ManifestSha256,
            x.Source.SourceEvidenceSha256, actual.TargetEvidenceSha256,
            actual.ExpectedSourceRefs, actual.ExpectedTargetRefs));
        Assert.False(result.ReadyForExecution);
        Assert.Equal(1, result.CreateCandidateCount);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Join_refuses_wrong_epoch_mapping_review_or_unsafe_evidence(
        int oidLength)
    {
        var x = Fixture(oidLength);
        var wrong = new (IncrementalOperationPlan Plan,
            GitHubSourceSyncCheckpointReceipt Checkpoint,
            GitHubSourceCheckpointGitRefProof Source,
            GitCheckpointTargetObservation Target)[] {
            (x.Plan with { IsCurrent = true }, x.Checkpoint, x.Source, x.Target),
            (x.Plan with { ReadyForExecution = true }, x.Checkpoint, x.Source, x.Target),
            (x.Plan with { Organization = "foreign" }, x.Checkpoint, x.Source, x.Target),
            (x.Plan, x.Checkpoint with { EvidenceVersion = 1 }, x.Source, x.Target),
            (x.Plan, x.Checkpoint, x.Source with { SnapshotId = x.Plan.SnapshotId }, x.Target),
            (x.Plan, x.Checkpoint, x.Source with { CheckpointId = Guid.NewGuid() }, x.Target),
            (x.Plan, x.Checkpoint, x.Source with { CanPush = true }, x.Target),
            (x.Plan, x.Checkpoint, x.Source with { Refs = [
                new GitMirrorRef("refs/heads/main", new string('1', oidLength)),
                new GitMirrorRef("refs/heads/MAIN", new string('2', oidLength))
            ] }, x.Target),
            (x.Plan, x.Checkpoint, x.Source, x.Target with { SourceSnapshotId = Guid.NewGuid() }),
            (x.Plan, x.Checkpoint, x.Source, x.Target with { ReviewedBy = Guid.NewGuid() }),
            (x.Plan, x.Checkpoint, x.Source, x.Target with { CanDispatch = true }),
            (x.Plan, x.Checkpoint, x.Source, x.Target with { EvidenceSha256 = new string('0', 64) }),
            (x.Plan, x.Checkpoint, x.Source, x.Target with { ObjectFormat =
                oidLength == 40 ? "sha256" : "sha1" })
        };
        foreach (var item in wrong)
            Assert.Equal("GitCheckpointTargetJoinInvalid",
                Assert.Throws<GitIncrementalCheckpointPreflightException>(() =>
                    GitIncrementalCheckpointTargetJoin.ForReadOnlyComparison(
                        item.Plan, item.Checkpoint,
                        item.Source, item.Target)).Code);
    }
}
