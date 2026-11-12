namespace GitHubMigrator.Business;

// #130: canonical NON-AUTHORIZING three-row write-intent binding.
// This is a proposed immutable review + matching event + one-use
// session consumption. It is NOT evidence that any row was committed.
// Callers MUST independently authenticate the current reviewer,
// re-seal Review against live source/physical evidence, and enforce
// one-winner PostgreSQL uniqueness inside a source-first SQL transaction.
public sealed record GitIncrementalLaterCheckpointMirrorSecondReviewJournalTuple(
    GitIncrementalLaterCheckpointMirrorSecondReview Review,
    GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent Session,
    Guid EventMirrorId, Guid EventSecondReviewerId, string EventDecision,
    string EventEvidenceSha256, DateTimeOffset EventOccurredAtUtc);

public static class GitIncrementalLaterCheckpointMirrorSecondReviewJournalTupleIntegrity
{
    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorSecondReviewJournalTupleInvalid");

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Pure structural parity for the existing exact SQL event FK and
    // session-consumption FK. The separate ReviewIntegrity.Verify()
    // requires the FULL trusted stage, bundle, source and first review;
    // it cannot be replaced with this structural check or a client SHA.
    public static GitIncrementalLaterCheckpointMirrorSecondReviewJournalTuple
        Prepare(
            GitIncrementalLaterCheckpointMirrorSecondReview review,
            GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent session)
    {
        if (review is null || session is null)
            throw Refused();
        try
        {
            GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
                .Verify(session);
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw Refused();
        }

        if (review.EvidenceVersion != 1 ||
            review.PhysicalMirrorId == Guid.Empty ||
            review.SecondReviewerId == Guid.Empty ||
            review.FirstReviewerId == Guid.Empty ||
            review.OriginalBundleActorId == Guid.Empty ||
            review.OriginalWorkerId == Guid.Empty ||
            review.SecondReviewerId == review.FirstReviewerId ||
            review.SecondReviewerId == review.OriginalBundleActorId ||
            review.SecondReviewerId == review.OriginalWorkerId ||
            review.OriginalGeneration is < 1 or > 1999 ||
            review.SuggestedNextGeneration != review.OriginalGeneration + 1 ||
            !IsSha256(review.EvidenceSha256) ||
            review.ReviewedAtUtc == default ||
            review.ReviewedAtUtc.Offset != TimeSpan.Zero ||
            review.ReviewedAtUtc != DateTimeOffset.FromUnixTimeMilliseconds(
                review.ReviewedAtUtc.ToUnixTimeMilliseconds()) ||
            review.SourceReviewedAtUtc > review.ReviewedAtUtc ||
            review.ReviewedAtUtc - review.SourceReviewedAtUtc >
                TimeSpan.FromMinutes(2) ||
            review.AssessedAtUtc > review.ReviewedAtUtc ||
            review.DurableSecondReviewPersisted ||
            review.CanReAdmit || review.CanMaterialize ||
            review.CanPush || review.CanRetry || review.CanDispatch ||
            review.MigrationComplete ||
            session.PhysicalMirrorId != review.PhysicalMirrorId ||
            session.SecondReviewerId != review.SecondReviewerId ||
            session.Decision != review.Decision ||
            session.ReviewEvidenceSha256 != review.EvidenceSha256 ||
            session.ReviewedAtUtc != review.ReviewedAtUtc)
            throw Refused();

        // All five event columns match the SQL compound FK *exactly*.
        // SessionId, nonce and mirror uniqueness are NOT established
        // here; only PostgreSQL can atomically consume those keys.
        return new(review, session, review.PhysicalMirrorId,
            review.SecondReviewerId, review.Decision,
            review.EvidenceSha256, review.ReviewedAtUtc);
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorSecondReviewJournalTuple candidate)
    {
        if (candidate is null ||
            candidate != Prepare(candidate.Review, candidate.Session))
            throw Refused();
    }
}
