using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// #130: domain-only fail-closed proof. Real one-time consumption,
// source/physical review, PostgreSQL and authentication remain separate.
public sealed class GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentTests
{
    private static readonly DateTimeOffset Issued =
        new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent
        Valid(string decision =
            GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                .ApprovedForIndependentReview)
    {
        return GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
            .Seal(Guid.NewGuid(), Guid.NewGuid(), decision,
                new string('a', 64), Guid.NewGuid(), Guid.NewGuid(),
                Issued, Issued.AddSeconds(30), Issued.AddSeconds(31));
    }

    [Theory]
    [InlineData(
        GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
            .ApprovedForIndependentReview)]
    [InlineData(
        GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
            .RejectedForIndependentReview)]
    public void Both_human_judgments_are_deterministic_and_never_authorize_recovery(
        string decision)
    {
        var x = Valid(decision);
        GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
            .Verify(x);
        Assert.Equal(1, x.EvidenceVersion);
        Assert.Equal(Issued.AddMinutes(5), x.ExpiresAtUtc);
        Assert.Equal(64, x.EvidenceSha256.Length);
        Assert.False(x.CanReAdmit || x.CanMaterialize || x.CanPush ||
            x.CanRetry || x.CanDispatch || x.MigrationComplete);
        Assert.Equal(x, GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
            .Seal(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
                x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
                x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc));
    }

    [Fact]
    public void Session_nonce_actor_scope_and_decision_are_all_cryptographically_bound()
    {
        var x = Valid();
        var integrity = GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
            .Seal;
        var changedSession = integrity(x.PhysicalMirrorId, x.SecondReviewerId,
            x.Decision, x.ReviewEvidenceSha256, Guid.NewGuid(), x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc);
        var changedNonce = integrity(x.PhysicalMirrorId, x.SecondReviewerId,
            x.Decision, x.ReviewEvidenceSha256, x.ReviewSessionId, Guid.NewGuid(),
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc);
        var changedActor = integrity(x.PhysicalMirrorId, Guid.NewGuid(),
            x.Decision, x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc);
        var rejected = integrity(x.PhysicalMirrorId, x.SecondReviewerId,
            GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                .RejectedForIndependentReview, x.ReviewEvidenceSha256,
            x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc);
        Assert.Equal(4, new HashSet<string> {
            changedSession.EvidenceSha256, changedNonce.EvidenceSha256,
            changedActor.EvidenceSha256, rejected.EvidenceSha256
        }.Count);
        Assert.DoesNotContain(x.EvidenceSha256, new[] {
            changedSession.EvidenceSha256, changedNonce.EvidenceSha256,
            changedActor.EvidenceSha256, rejected.EvidenceSha256
        });
    }

    [Fact]
    public void Tampered_proof_or_permission_bit_cannot_verify()
    {
        var x = Valid();
        var invalid = new[] {
            x with { EvidenceVersion = 2 },
            x with { EvidenceSha256 = new string('b', 64) },
            x with { ReviewEvidenceSha256 = new string('c', 64) },
            x with { PhysicalMirrorId = Guid.NewGuid() },
            x with { SecondReviewerId = Guid.NewGuid() },
            x with { ReviewSessionId = Guid.NewGuid() },
            x with { DecisionNonce = Guid.NewGuid() },
            x with { ConsumedAtUtc = x.ConsumedAtUtc.AddMilliseconds(1) },
            x with { ExpiresAtUtc = x.ExpiresAtUtc.AddSeconds(1) },
            x with { CanReAdmit = true },
            x with { CanMaterialize = true },
            x with { CanPush = true },
            x with { CanRetry = true },
            x with { CanDispatch = true },
            x with { MigrationComplete = true }
        };
        foreach (var forged in invalid)
            Reject(() =>
                GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
                    .Verify(forged));
    }

    [Fact]
    public void Invalid_identity_sha_decision_and_noncanonical_times_fail_closed()
    {
        var x = Valid();
        void Check(Guid mirror, Guid reviewer, string decision, string sha,
            Guid session, Guid nonce, DateTimeOffset issued,
            DateTimeOffset reviewed, DateTimeOffset consumed) =>
            GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
                .Seal(mirror, reviewer, decision, sha, session, nonce,
                    issued, reviewed, consumed);

        Reject(() => Check(Guid.Empty, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc));
        Reject(() => Check(x.PhysicalMirrorId, Guid.Empty, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, "Approve",
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc));
        foreach (var badSha in new[] {
            new string('A', 64), new string('z', 64), new string('a', 63)
        })
            Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
                badSha, x.ReviewSessionId, x.DecisionNonce,
                x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.ReviewSessionId,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc.AddTicks(1), x.ReviewedAtUtc, x.ConsumedAtUtc));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc.AddTicks(1), x.ConsumedAtUtc));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.ConsumedAtUtc.AddTicks(1)));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc, x.IssuedAtUtc.AddMinutes(5)));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.IssuedAtUtc.AddSeconds(5),
            x.IssuedAtUtc.AddSeconds(126)));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.IssuedAtUtc.AddSeconds(-1),
            x.IssuedAtUtc.AddSeconds(1)));
        Reject(() => Check(x.PhysicalMirrorId, x.SecondReviewerId, x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId, x.DecisionNonce,
            x.IssuedAtUtc, x.ReviewedAtUtc,
            x.ReviewedAtUtc.AddSeconds(-1)));
    }

    private static void Reject(Action attempt)
    {
        var ex = Assert.Throws<GitSourceSnapshotMirrorException>(attempt);
        Assert.Equal("LaterCheckpointMirrorSecondReviewSessionIntentInvalid",
            ex.Code);
    }
}
