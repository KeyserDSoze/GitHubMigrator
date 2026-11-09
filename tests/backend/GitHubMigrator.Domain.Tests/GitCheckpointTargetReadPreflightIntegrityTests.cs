using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// This does NOT mock a provider authorization: it checks only the hash
// semantics of a non-authorizing, ephemeral PostgreSQL readiness witness.
public sealed class GitCheckpointTargetReadPreflightIntegrityTests
{
    private static GitCheckpointTargetReReviewCandidate Candidate(int oidSize) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "repo-1", "sandbox", "gh-org", new string('a', 64),
            new string('b', 64), Guid.NewGuid(), new string('c', 64),
            new string('d', 64), new string('e', 64),
            oidSize == 40 ? "sha1" : "sha256", 2, Guid.NewGuid(),
            new string('f', 64), false, false, false, false, false);

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Approved_independent_review_can_only_seal_non_authorizing_read_preflight(int oidSize)
    {
        var candidate = Candidate(oidSize);
        var reviewedAt = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var reviewer = Guid.NewGuid();
        var approved = GitCheckpointTargetHumanReviewIntegrity.Seal(
            candidate, Guid.NewGuid(), reviewer,
            GitCheckpointTargetHumanReviewStates.ApprovedForObservation,
            reviewedAt);
        var now = reviewedAt.AddMinutes(1);
        var expiry = reviewedAt.AddMinutes(4);
        var witness = GitCheckpointTargetReadPreflightIntegrity.Seal(
            candidate, approved, now, expiry);
        Assert.Equal(1, witness.EvidenceVersion);
        Assert.Equal(candidate.CheckpointId, witness.CheckpointId);
        Assert.Equal(candidate.SourceSnapshotId, witness.SourceSnapshotId);
        Assert.Equal(candidate.EvidenceSha256,
            witness.CandidateEvidenceSha256);
        Assert.Equal(approved.EvidenceSha256, witness.HumanReviewEvidenceSha256);
        Assert.Equal(candidate.ObjectFormat, witness.ObjectFormat);
        Assert.Equal(64, witness.EvidenceSha256.Length);
        Assert.False(witness.ProviderReadAuthorized || witness.CanObserveTarget ||
            witness.CanPush || witness.CanRetry || witness.CanDispatch ||
            witness.MigrationComplete);
        GitCheckpointTargetReadPreflightIntegrity.Verify(
            witness, candidate, approved);
        Assert.Equal(witness, GitCheckpointTargetReadPreflightIntegrity.Seal(
            candidate, approved, now, expiry));

        foreach (var forged in new[] {
            witness with { ProviderReadAuthorized = true },
            witness with { CanObserveTarget = true },
            witness with { CanPush = true },
            witness with { CanRetry = true },
            witness with { CanDispatch = true },
            witness with { MigrationComplete = true },
            witness with { CheckpointId = Guid.NewGuid() },
            witness with { TargetRepositoryName = "other" },
            witness with { HumanReviewEvidenceSha256 = new string('0', 64) },
            witness with { ReviewedBy = Guid.NewGuid() },
            witness with { ExpiresAtUtc = expiry.AddMinutes(2) },
            witness with { EvidenceSha256 = new string('0', 64) }
        })
            Assert.Equal("CheckpointTargetReadPreflightInvalidEvidence",
                Assert.Throws<GitCheckpointTargetReadPreflightException>(() =>
                    GitCheckpointTargetReadPreflightIntegrity.Verify(
                        forged, candidate, approved)).Code);
        var rejected = GitCheckpointTargetHumanReviewIntegrity.Seal(
            candidate, Guid.NewGuid(), reviewer,
            GitCheckpointTargetHumanReviewStates.Rejected, reviewedAt);
        Assert.Equal("CheckpointTargetReadPreflightInvalidEvidence",
            Assert.Throws<GitCheckpointTargetReadPreflightException>(() =>
                GitCheckpointTargetReadPreflightIntegrity.Seal(
                    candidate, rejected, now, expiry)).Code);
        Assert.Equal("CheckpointTargetReadPreflightInvalidEvidence",
            Assert.Throws<GitCheckpointTargetReadPreflightException>(() =>
                GitCheckpointTargetReadPreflightIntegrity.Seal(
                    candidate, approved, now, now.AddMinutes(6))).Code);
    }
}
