using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// SHA-1 and SHA-256 Git object formats; never mistake a reviewed human
// decision or a source-readiness witness for destination push authority.
public sealed class GitCheckpointTargetObservationIntegrityTests
{
    private static GitCheckpointTargetReadPreflight Preflight(int oidLength)
    {
        var reviewId = Guid.NewGuid();
        var checkedAt = new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero);
        return new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "repo-1", "sandbox",
            "gh-org", oidLength == 40 ? "sha1" : "sha256", 2, reviewId,
            new string('a', 64), new string('b', 64), checkedAt,
            checkedAt.AddMinutes(4), new string('c', 64),
            false, false, false, false, false, false);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void F097c4c0d_target_observation_is_checkpoint_scoped_and_never_executable(
        int oidLength)
    {
        var prerequisite = Preflight(oidLength);
        var refs = new[] {
            new GitHubObservedRef("refs/heads/main",
                new string('a', oidLength), "commit"),
            new GitHubObservedRef("refs/tags/release",
                new string('b', oidLength), "tag")
        };
        var observed = GitHubRefObservationIntegrity.Seal("gh-org",
            "sandbox", prerequisite.ValidatedAtUtc.AddSeconds(2), refs);
        var id = Guid.NewGuid();
        var evidence = GitCheckpointTargetObservationIntegrity.Seal(
            prerequisite, observed, id);
        Assert.Equal(1, evidence.EvidenceVersion);
        Assert.Equal(prerequisite.CheckpointId, evidence.CheckpointId);
        Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
            evidence.ObjectFormat);
        Assert.Equal(2, evidence.RefCount);
        Assert.Equal(observed.EvidenceSha256, evidence.TargetEvidenceSha256);
        Assert.Equal(prerequisite.HumanReviewEvidenceSha256,
            evidence.HumanReviewEvidenceSha256);
        Assert.Equal(64, evidence.EvidenceSha256.Length);
        Assert.False(evidence.CanObserveTarget || evidence.CanPush ||
            evidence.CanRetry || evidence.CanDispatch ||
            evidence.MigrationComplete);
        GitCheckpointTargetObservationIntegrity.VerifyCurrent(
            evidence, prerequisite);
        GitCheckpointTargetObservationIntegrity.VerifyStored(evidence);
        Assert.Equal(evidence.EvidenceSha256,
            GitCheckpointTargetObservationIntegrity.Seal(
                prerequisite, observed, id).EvidenceSha256);

        foreach (var bad in new[] {
            evidence with { CheckpointId = Guid.NewGuid() },
            evidence with { SourceRepositoryId = "other" },
            evidence with { Organization = "other" },
            evidence with { ReviewedBy = Guid.NewGuid() },
            evidence with { ReadPreflightEvidenceSha256 = new string('0', 64) },
            evidence with { HumanReviewEvidenceSha256 = new string('0', 64) },
            evidence with { TargetEvidenceSha256 = new string('0', 64) },
            evidence with { CanPush = true },
            evidence with { CanObserveTarget = true },
            evidence with { CanRetry = true },
            evidence with { CanDispatch = true },
            evidence with { MigrationComplete = true },
            evidence with { EvidenceSha256 = new string('0', 64) },
            evidence with { Refs = new[] {
                new GitHubObservedRef("refs/heads/main", new string('d', oidLength),
                    "commit"), refs[1] } }
        })
            Assert.Equal("CheckpointTargetObservationEvidenceInvalid",
                Assert.Throws<GitCheckpointTargetObservationException>(
                    () => GitCheckpointTargetObservationIntegrity.VerifyStored(
                        bad)).Code);

        foreach (var invalid in new[] {
            observed with { EvidenceSha256 = new string('0', 64) },
            observed with { Repository = "foreign" },
            observed with { Organization = "foreign" },
            observed with { Refs = new[] {
                new GitHubObservedRef("refs/heads/main",
                    new string('f', oidLength == 40 ? 64 : 40), "commit")
            } },
            observed with { Refs = [] },
            observed with { ObservedAtUtc =
                prerequisite.ExpiresAtUtc.AddSeconds(1) }
        })
            Assert.Equal("CheckpointTargetObservationEvidenceInvalid",
                Assert.Throws<GitCheckpointTargetObservationException>(
                    () => GitCheckpointTargetObservationIntegrity.Seal(
                        prerequisite, invalid, Guid.NewGuid())).Code);

        Assert.Equal("CheckpointTargetObservationEvidenceInvalid",
            Assert.Throws<GitCheckpointTargetObservationException>(
                () => GitCheckpointTargetObservationIntegrity.Seal(
                    prerequisite with { ProviderReadAuthorized = true },
                    observed, Guid.NewGuid())).Code);
    }
}
