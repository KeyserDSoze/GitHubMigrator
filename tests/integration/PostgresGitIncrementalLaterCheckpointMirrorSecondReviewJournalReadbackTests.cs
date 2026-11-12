using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// #130: REAL PostgreSQL17 read-only triple-witness regression.
// Existing immutable F-061 journals deny ALL writes, so a proposal
// must NEVER masquerade as committed history. No fixtures INSERT into
// protected tables or turn off their triggers.
public sealed class PostgresGitIncrementalLaterCheckpointMirrorSecondReviewJournalReadbackTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    private static readonly DateTimeOffset IssuedAtUtc =
        new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Sha = new('a', 64);

    private static GitIncrementalLaterCheckpointMirrorSecondReviewJournalTuple
        Candidate(string decision)
    {
        var review = new GitIncrementalLaterCheckpointMirrorSecondReview(
            EvidenceVersion: 1,
            PhysicalMirrorId: Guid.NewGuid(),
            WorkpackId: Guid.NewGuid(),
            CheckpointId: Guid.NewGuid(),
            SourceSnapshotId: Guid.NewGuid(),
            SourceRepositoryId: "source-repo",
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
            IsolationDirectoryName: "quarantine",
            ObjectSetSha256: Sha,
            ObjectCount: 1,
            SourceReviewedAtUtc: IssuedAtUtc.AddSeconds(20),
            AssessedAtUtc: IssuedAtUtc.AddSeconds(22),
            ReviewedAtUtc: IssuedAtUtc.AddSeconds(30),
            EvidenceSha256: Sha,
            DurableSecondReviewPersisted: false,
            CanReAdmit: false, CanMaterialize: false,
            CanPush: false, CanRetry: false,
            CanDispatch: false, MigrationComplete: false);
        var session =
            GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity.Seal(
                review.PhysicalMirrorId, review.SecondReviewerId,
                review.Decision, review.EvidenceSha256,
                Guid.NewGuid(), Guid.NewGuid(),
                IssuedAtUtc, review.ReviewedAtUtc,
                IssuedAtUtc.AddSeconds(31));
        return GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
            .Prepare(review, session);
    }

    [Theory]
    [InlineData(GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
        .ApprovedForIndependentReview)]
    [InlineData(GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
        .RejectedForIndependentReview)]
    public async Task Proposed_approval_or_rejection_never_counts_as_a_committed_fact(
        string decision)
    {
        await using var scope = harness.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reader = new
            PostgresGitIncrementalLaterCheckpointMirrorSecondReviewJournalReadback(db);
        var candidate = Candidate(decision);

        // In a real database protected by three DENY-ALL triggers, the
        // presence of a well-formed self-sealed candidate proves nothing.
        Assert.False(await reader.HasExactNonAuthorizingThreeRowReceiptAsync(
            candidate));
        // The historic first-human actor/SHA is an independent SQL
        // witness; a caller changing first-decision SHA must never turn
        // an otherwise well-formed proposal into a committed journal.
        var spoofedFirstDecision = candidate with {
            Review = candidate.Review with {
                FirstDecisionSha256 = new string('b', 64)
            }
        };
        Assert.False(await reader.HasExactNonAuthorizingThreeRowReceiptAsync(
            spoofedFirstDecision));
        var changedEvent = candidate with {
            EventEvidenceSha256 = new string('b', 64)
        };
        var ex = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => reader.HasExactNonAuthorizingThreeRowReceiptAsync(
                changedEvent));
        Assert.Equal("LaterCheckpointMirrorSecondReviewJournalTupleInvalid",
            ex.Code);
        var forgedSession = candidate with {
            Session = candidate.Session with {
                ReviewSessionId = Guid.NewGuid()
            }
        };
        var ex2 = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => reader.HasExactNonAuthorizingThreeRowReceiptAsync(
                forgedSession));
        Assert.Equal("LaterCheckpointMirrorSecondReviewJournalTupleInvalid",
            ex2.Code);
    }
}
