using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #130: deterministic, NON-AUTHORIZING one-use session/nonce intent.
// This record is NOT a JWT, a database consumption or an operator approval.
// A future writer must authenticate the actor again, re-audit Git under the
// shared F-060 POSIX lock, and atomically persist an independently verified
// second review, event and session consumption under source-first SQL locks.
public sealed record GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent(
    int EvidenceVersion, Guid PhysicalMirrorId, Guid SecondReviewerId,
    string Decision, string ReviewEvidenceSha256,
    Guid ReviewSessionId, Guid DecisionNonce,
    DateTimeOffset IssuedAtUtc, DateTimeOffset ReviewedAtUtc,
    DateTimeOffset ConsumedAtUtc, DateTimeOffset ExpiresAtUtc,
    string EvidenceSha256,
    bool CanReAdmit, bool CanMaterialize, bool CanPush,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
{
    private const string Domain =
        "f097c4-f061v2e-second-review-one-use-session-intent-v1";

    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorSecondReviewSessionIntentInvalid");

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    // SQL's unique session, mirror and nonce keys are the *only* actual
    // replay fences. This pure function provides a canonical candidate
    // for those exact immutable columns without giving it write authority.
    public static GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent Seal(
        Guid physicalMirrorId, Guid secondReviewerId, string decision,
        string reviewEvidenceSha256, Guid reviewSessionId, Guid decisionNonce,
        DateTimeOffset issuedAtUtc, DateTimeOffset reviewedAtUtc,
        DateTimeOffset consumedAtUtc)
    {
        if (physicalMirrorId == Guid.Empty || secondReviewerId == Guid.Empty ||
            reviewSessionId == Guid.Empty || decisionNonce == Guid.Empty ||
            reviewSessionId == decisionNonce ||
            decision is not (
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                    .ApprovedForIndependentReview or
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                    .RejectedForIndependentReview) ||
            !IsLowerSha256(reviewEvidenceSha256) ||
            !IsUtc(issuedAtUtc) || !IsUtc(reviewedAtUtc) ||
            !IsUtc(consumedAtUtc) ||
            // The JWT claims issue whole seconds; the reviewed decision
            // uses the canonical UTC millisecond timestamp from GET.
            DateTimeOffset.FromUnixTimeSeconds(
                issuedAtUtc.ToUnixTimeSeconds()) != issuedAtUtc ||
            DateTimeOffset.FromUnixTimeMilliseconds(
                reviewedAtUtc.ToUnixTimeMilliseconds()) != reviewedAtUtc ||
            DateTimeOffset.FromUnixTimeMilliseconds(
                consumedAtUtc.ToUnixTimeMilliseconds()) != consumedAtUtc)
            throw Refused();

        // A syntactically valid UTC instant near DateTimeOffset.MaxValue
        // cannot represent a complete five-minute reviewer session.
        // Fail closed with the domain error rather than leaking an
        // ArgumentOutOfRangeException into the API's 500 response.
        DateTimeOffset expiry;
        try { expiry = issuedAtUtc.AddMinutes(5); }
        catch (ArgumentOutOfRangeException) { throw Refused(); }
        if (reviewedAtUtc < issuedAtUtc ||
            consumedAtUtc < reviewedAtUtc ||
            consumedAtUtc >= expiry ||
            consumedAtUtc - reviewedAtUtc > TimeSpan.FromMinutes(2))
            throw Refused();

        var candidate =
            new GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent(
                1, physicalMirrorId, secondReviewerId, decision,
                reviewEvidenceSha256, reviewSessionId, decisionNonce,
                issuedAtUtc, reviewedAtUtc, consumedAtUtc, expiry, "",
                false, false, false, false, false, false);
        return candidate with { EvidenceSha256 = Digest(candidate) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent candidate)
    {
        if (candidate is null || candidate.EvidenceVersion != 1 ||
            candidate.CanReAdmit || candidate.CanMaterialize ||
            candidate.CanPush || candidate.CanRetry || candidate.CanDispatch ||
            candidate.MigrationComplete ||
            candidate != Seal(candidate.PhysicalMirrorId,
                candidate.SecondReviewerId, candidate.Decision,
                candidate.ReviewEvidenceSha256, candidate.ReviewSessionId,
                candidate.DecisionNonce, candidate.IssuedAtUtc,
                candidate.ReviewedAtUtc, candidate.ConsumedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntent x)
    {
        object[] parts = {
            Domain, x.EvidenceVersion, x.PhysicalMirrorId.ToString("D"),
            x.SecondReviewerId.ToString("D"), x.Decision,
            x.ReviewEvidenceSha256, x.ReviewSessionId.ToString("D"),
            x.DecisionNonce.ToString("D"),
            x.IssuedAtUtc.ToString("O"), x.ReviewedAtUtc.ToString("O"),
            x.ConsumedAtUtc.ToString("O"), x.ExpiresAtUtc.ToString("O"),
            false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }
}
