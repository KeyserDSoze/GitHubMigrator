using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// #130: this intentionally does NOT mock an authenticated SQL writer.
// It proves that a proposed immutable second-review event and one-use
// session cannot be rebound to a different candidate without refusal.
public sealed class GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Sha = new('a', 64);

    private static GitIncrementalLaterCheckpointMirrorSecondReview Review(
        string decision)
    {
        return new GitIncrementalLaterCheckpointMirrorSecondReview(
            EvidenceVersion: 1,
            PhysicalMirrorId: Guid.NewGuid(),
            WorkpackId: Guid.NewGuid(),
            CheckpointId: Guid.NewGuid(),
            SourceSnapshotId: Guid.NewGuid(),
            SourceRepositoryId: "repo-1",
            FirstReviewerId: Guid.NewGuid(),
            SecondReviewerId: Guid.NewGuid(),
            Decision: decision,
            OriginalWorkerId: Guid.NewGuid(),
            OriginalLeaseToken: Guid.NewGuid(),
            OriginalGeneration: 1,
            SuggestedNextGeneration: 2,
            StagingEvidenceSha256: Sha,
            SourceFullRefProofSha256: Sha,
            SourceRefSetSha256: Sha,
            TargetReceiptSha256: Sha,
            BundleSha256: Sha,
            BundleBytes: 128,
            FirstDecisionSha256: Sha,
            IsolationReceiptSha256: Sha,
            PreparedIntentSha256: Sha,
            AssessmentEvidenceSha256: Sha,
            IsolationDirectoryName: "reviewed-quarantine",
            ObjectSetSha256: Sha,
            ObjectCount: 1,
            SourceReviewedAtUtc: IssuedAt.AddSeconds(20),
            AssessedAtUtc: IssuedAt.AddSeconds(22),
            ReviewedAtUtc: IssuedAt.AddSeconds(30),
            EvidenceSha256: Sha,
            DurableSecondReviewPersisted: false,
            CanReAdmit: false,
            CanMaterialize: false,
            CanPush: false,
            CanRetry: false,
            CanDispatch: false,
            MigrationComplete: false);
    }

    private static GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent
        Session(GitIncrementalLaterCheckpointMirrorSecondReview review) =>
        GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
            .Seal(review.PhysicalMirrorId, review.SecondReviewerId,
                review.Decision, review.EvidenceSha256,
                Guid.NewGuid(), Guid.NewGuid(), IssuedAt,
                review.ReviewedAtUtc, IssuedAt.AddSeconds(31));

    [Theory]
    [InlineData(GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
        .ApprovedForIndependentReview)]
    [InlineData(GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
        .RejectedForIndependentReview)]
    public void Matching_three_row_intent_has_exact_event_fk_and_no_authority(
        string decision)
    {
        var review = Review(decision);
        var session = Session(review);
        var row = GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
            .Prepare(review, session);
        GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
            .Verify(row);
        Assert.Equal(review.PhysicalMirrorId, row.EventMirrorId);
        Assert.Equal(review.SecondReviewerId, row.EventSecondReviewerId);
        Assert.Equal(decision, row.EventDecision);
        Assert.Equal(review.EvidenceSha256, row.EventEvidenceSha256);
        Assert.Equal(review.ReviewedAtUtc, row.EventOccurredAtUtc);
        Assert.False(row.Review.DurableSecondReviewPersisted);
        Assert.False(row.Review.CanReAdmit || row.Review.CanMaterialize ||
            row.Review.CanPush || row.Review.CanRetry ||
            row.Review.CanDispatch || row.Review.MigrationComplete);
        Assert.False(row.Session.CanReAdmit || row.Session.CanMaterialize ||
            row.Session.CanPush || row.Session.CanRetry ||
            row.Session.CanDispatch || row.Session.MigrationComplete);
    }

    [Fact]
    public void Review_identity_decision_generation_and_lease_flags_are_fenced()
    {
        var review = Review(
            GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                .ApprovedForIndependentReview);
        var session = Session(review);
        var variants = new[] {
            review with { PhysicalMirrorId = Guid.NewGuid() },
            review with { SecondReviewerId = Guid.NewGuid() },
            review with { SecondReviewerId = review.FirstReviewerId },
            review with { SecondReviewerId = review.OriginalWorkerId },
            review with { Decision =
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                    .RejectedForIndependentReview },
            review with { EvidenceSha256 = new string('b', 64) },
            review with { ReviewedAtUtc = review.ReviewedAtUtc.AddMilliseconds(1) },
            review with { SourceReviewedAtUtc = IssuedAt.AddMinutes(-3) },
            review with { SuggestedNextGeneration = 3 },
            review with { OriginalGeneration = 2000 },
            review with { DurableSecondReviewPersisted = true },
            review with { CanReAdmit = true },
            review with { CanMaterialize = true },
            review with { CanPush = true },
            review with { CanRetry = true },
            review with { CanDispatch = true },
            review with { MigrationComplete = true }
        };
        foreach (var changed in variants)
            Reject(() =>
                GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
                    .Prepare(changed, session));
    }

    [Fact]
    public void Session_tampering_and_event_mismatch_are_never_a_commit_proof()
    {
        var review = Review(
            GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                .RejectedForIndependentReview);
        var session = Session(review);
        var variants = new[] {
            session with { EvidenceSha256 = new string('b', 64) },
            session with { ReviewSessionId = session.DecisionNonce },
            session with { DecisionNonce = Guid.NewGuid() },
            session with { SecondReviewerId = Guid.NewGuid() },
            session with { ReviewedAtUtc = session.ReviewedAtUtc.AddMilliseconds(1) },
            session with { ReviewEvidenceSha256 = new string('b', 64) },
            session with { CanPush = true }
        };
        foreach (var changed in variants)
            Reject(() =>
                GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
                    .Prepare(review, changed));

        var valid = GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
            .Prepare(review, session);
        foreach (var changed in new[] {
            valid with { EventMirrorId = Guid.NewGuid() },
            valid with { EventSecondReviewerId = Guid.NewGuid() },
            valid with { EventDecision =
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                    .ApprovedForIndependentReview },
            valid with { EventEvidenceSha256 = new string('b', 64) },
            valid with { EventOccurredAtUtc = valid.EventOccurredAtUtc.AddMilliseconds(1) }
        })
            Reject(() =>
                GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
                    .Verify(changed));
    }

    private static void Reject(Action action)
    {
        var ex = Assert.Throws<GitSourceSnapshotMirrorException>(action);
        Assert.Equal("LaterCheckpointMirrorSecondReviewJournalTupleInvalid",
            ex.Code);
    }
}
