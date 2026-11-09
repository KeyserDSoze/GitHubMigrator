using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// A synthetic already-validated #106 candidate is for digest semantics
// only; the PostgreSQL reader must still revalidate F-014/F-091/F-097b.
public sealed class GitCheckpointTargetHumanReviewIntegrityTests
{
    private static GitCheckpointTargetReReviewCandidate Candidate(int oidSize)
    {
        var requested = Guid.NewGuid();
        return new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "repo-1", "sandbox", "org", new string('a', 64),
            new string('b', 64), Guid.NewGuid(), new string('c', 64),
            new string('d', 64), new string('e', 64),
            oidSize == 40 ? "sha1" : "sha256", 2, requested,
            new string('f', 64), false, false, false, false, false);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Explicit_independent_signoff_is_stable_and_never_authorizes_observation(
        int oidSize)
    {
        var candidate = Candidate(oidSize);
        var reviewer = Guid.NewGuid();
        var id = Guid.NewGuid();
        var time = new DateTimeOffset(2026, 11, 9, 14, 15, 0,
            TimeSpan.Zero);
        var first = GitCheckpointTargetHumanReviewIntegrity.Seal(
            candidate, id, reviewer,
            GitCheckpointTargetHumanReviewStates.ApprovedForObservation, time);
        var repeated = GitCheckpointTargetHumanReviewIntegrity.Seal(
            candidate, id, reviewer,
            GitCheckpointTargetHumanReviewStates.ApprovedForObservation, time);
        Assert.Equal(first, repeated);
        Assert.Equal(64, first.EvidenceSha256.Length);
        Assert.Equal(candidate.CheckpointId, first.CheckpointId);
        Assert.Equal(candidate.SourceSnapshotId, first.SourceSnapshotId);
        Assert.Equal(candidate.EvidenceSha256,
            first.CandidateEvidenceSha256);
        Assert.False(first.CanObserveTarget || first.CanPush ||
            first.CanRetry || first.CanDispatch || first.MigrationComplete);
        GitCheckpointTargetHumanReviewIntegrity.Verify(first, candidate);
        GitCheckpointTargetHumanReviewIntegrity.VerifyStored(first);

        var rejected = GitCheckpointTargetHumanReviewIntegrity.Seal(
            candidate, id, reviewer, GitCheckpointTargetHumanReviewStates.Rejected,
            time);
        Assert.NotEqual(first.EvidenceSha256, rejected.EvidenceSha256);
        Assert.False(rejected.CanObserveTarget);
        foreach (var bad in new[] {
            first with { CanObserveTarget = true },
            first with { CanPush = true },
            first with { CandidateEvidenceSha256 = new string('0', 64) },
            first with { ReviewedBy = Guid.NewGuid() },
            first with { Decision = GitCheckpointTargetHumanReviewStates.Rejected },
            first with { EvidenceSha256 = new string('1', 64) }
        })
            Assert.Equal("CheckpointHumanReviewEvidenceInvalid",
                Assert.Throws<GitCheckpointTargetHumanReviewException>(() =>
                    GitCheckpointTargetHumanReviewIntegrity.Verify(
                        bad, candidate)).Code);
        foreach (var bad in new[] {
            first with { SourceRepositoryId = "different-repo" },
            first with { TargetRepositoryName = "different-target" },
            first with { ReviewedBy = candidate.RequestedBy },
            first with { ReviewedAtUtc = time.AddMinutes(1) },
            first with { Decision = GitCheckpointTargetHumanReviewStates.Rejected },
            first with { CanObserveTarget = true },
            first with { EvidenceSha256 = new string('a', 64) }
        })
            Assert.Equal("CheckpointHumanReviewEvidenceInvalid",
                Assert.Throws<GitCheckpointTargetHumanReviewException>(() =>
                    GitCheckpointTargetHumanReviewIntegrity.VerifyStored(bad)).Code);

        Assert.Equal("CheckpointHumanReviewEvidenceInvalid",
            Assert.Throws<GitCheckpointTargetHumanReviewException>(() =>
                GitCheckpointTargetHumanReviewIntegrity.Seal(
                    candidate, id, candidate.RequestedBy,
                    GitCheckpointTargetHumanReviewStates.ApprovedForObservation,
                    time)).Code);
        Assert.Equal("CheckpointHumanReviewEvidenceInvalid",
            Assert.Throws<GitCheckpointTargetHumanReviewException>(() =>
                GitCheckpointTargetHumanReviewIntegrity.Seal(
                    candidate with { CanObserveTarget = true },
                    id, reviewer,
                    GitCheckpointTargetHumanReviewStates.ApprovedForObservation,
                    time)).Code);
    }
}
