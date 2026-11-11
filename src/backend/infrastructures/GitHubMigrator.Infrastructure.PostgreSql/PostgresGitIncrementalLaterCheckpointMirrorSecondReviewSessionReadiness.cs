using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #130: extra read-only, PostgreSQL-clock one-use SESSION PREFLIGHT.
// This is not a decision writer, session consumption, replay CAS or
// remotely executable authorization. No SQL transaction is held across
// the expensive source-first / F-060 POSIX double Git fsck. A future
// protected writer MUST recheck in its atomic source-locked transaction.
public sealed class PostgresGitIncrementalLaterCheckpointMirrorSecondReviewSessionReadiness(
    AppDbContext db)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    public async Task<DateTimeOffset> RequireUnusedAsync(
        Guid reviewSessionId, Guid decisionNonce,
        DateTimeOffset issuedAtUtc, DateTimeOffset reviewedAtUtc,
        Guid physicalMirrorId, CancellationToken ct)
    {
        if (reviewSessionId == Guid.Empty || decisionNonce == Guid.Empty ||
            reviewSessionId == decisionNonce ||
            issuedAtUtc == default ||
            issuedAtUtc.Offset != TimeSpan.Zero ||
            reviewedAtUtc.Offset != TimeSpan.Zero ||
            issuedAtUtc != DateTimeOffset.FromUnixTimeSeconds(
                issuedAtUtc.ToUnixTimeSeconds()) ||
            reviewedAtUtc != DateTimeOffset.FromUnixTimeMilliseconds(
                reviewedAtUtc.ToUnixTimeMilliseconds()) ||
            reviewedAtUtc < issuedAtUtc)
            throw Refused("LaterCheckpointMirrorSecondReviewPreflightInvalid");

        DateTimeOffset expiresAtUtc;
        try { expiresAtUtc = issuedAtUtc.AddMinutes(5); }
        catch (ArgumentOutOfRangeException)
        {
            throw Refused("LaterCheckpointMirrorSecondReviewPreflightInvalid");
        }

        // This is the real PostgreSQL server's current instant, NOT the
        // API/CI fixture TimeProvider. No caller may supply observedAt.
        // Truncate to the exact millisecond granularity of the immutable
        // C# SessionIntent and PostgreSQL CHECK v4.
        var databaseNow = await db.Database.SqlQueryRaw<DateTimeOffset>("""
            SELECT date_trunc('milliseconds', clock_timestamp()) AS "Value"
            """).SingleAsync(ct);
        if (databaseNow < issuedAtUtc ||
            databaseNow >= expiresAtUtc ||
            reviewedAtUtc > databaseNow ||
            databaseNow - reviewedAtUtc > TimeSpan.FromMinutes(2))
            throw Refused("LaterCheckpointMirrorSecondReviewPreflightExpired");

        // A fast read-only replay preflight. The initial call passes
        // Guid.Empty for a mirror that has not been re-audited; the
        // second call supplies only the server-derived physical mirror.
        // Actual one-winner replay protection comes exclusively from
        // the SQL UNIQUE constraints in a future locked atomic writer.
        var alreadyConsumed = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_second_review_session_consumptions
            WHERE review_session_id = {reviewSessionId}
                OR decision_nonce = {decisionNonce}
                OR mirror_id = {physicalMirrorId}
            """).SingleAsync(ct);
        if (alreadyConsumed != 0)
            throw Refused("LaterCheckpointMirrorSecondReviewPreflightAlreadyConsumed");

        return databaseNow;
    }
}
