using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// One consistent, read-only PostgreSQL statement. Do NOT materialize
// individual lifecycle rows in .NET or expose worker identifiers.
public sealed class PostgresGitSourceMirrorFleetHealthReader(
    AppDbContext db, GitSourceMirrorLifecycleOptions options)
    : IGitSourceMirrorFleetHealthReader
{
    private const string HealthSql = """
        WITH sample AS MATERIALIZED (
            SELECT clock_timestamp() AS observed_at_utc
        )
        SELECT
            sample.observed_at_utc AS "ObservedAtUtc",
            counters."TotalMirrors",
            counters."ActiveLeases",
            counters."ExpiredLeases",
            counters."ExpiringSoonLeases",
            counters."VerifiedMirrors",
            counters."RecoveryRequiredMirrors",
            counters."OldestExpiredAtUtc"
        FROM sample
        CROSS JOIN LATERAL (
            SELECT
                count(*) AS "TotalMirrors",
                count(*) FILTER (
                    WHERE state = 'Leased'
                      AND lease_expires_at_utc > sample.observed_at_utc
                ) AS "ActiveLeases",
                count(*) FILTER (
                    WHERE state = 'Leased'
                      AND lease_expires_at_utc <= sample.observed_at_utc
                ) AS "ExpiredLeases",
                count(*) FILTER (
                    WHERE state = 'Leased'
                      AND lease_expires_at_utc > sample.observed_at_utc
                      AND lease_expires_at_utc <=
                          sample.observed_at_utc + interval '5 minutes'
                ) AS "ExpiringSoonLeases",
                count(*) FILTER (
                    WHERE state = 'Verified'
                ) AS "VerifiedMirrors",
                count(*) FILTER (
                    WHERE state = 'RecoveryRequired'
                ) AS "RecoveryRequiredMirrors",
                min(lease_expires_at_utc) FILTER (
                    WHERE state = 'Leased'
                      AND lease_expires_at_utc <= sample.observed_at_utc
                ) AS "OldestExpiredAtUtc"
            FROM git_source_mirror_lifecycles
        ) AS counters
        """;

    public async Task<GitSourceMirrorFleetHealth> ReadAsync(CancellationToken ct)
    {
        if (!options.Enabled)
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleDisabled");

        // Scalar aggregate, one row even for an empty catalog. The
        // MATERIALIZED CTE fixes a single DB-clock observation instant
        // instead of relying on inconsistent replica clocks.
        var row = await db.Database.SqlQueryRaw<HealthSqlRow>(HealthSql)
            .SingleAsync(ct);
        return new(row.ObservedAtUtc, row.TotalMirrors,
            row.ActiveLeases, row.ExpiredLeases, row.ExpiringSoonLeases,
            row.VerifiedMirrors, row.RecoveryRequiredMirrors,
            row.OldestExpiredAtUtc);
    }

    // EF Core 10 unmapped SQL projection; deliberately limited to safe
    // numeric aggregates and database timestamps.
    public sealed class HealthSqlRow
    {
        public DateTimeOffset ObservedAtUtc { get; set; }
        public long TotalMirrors { get; set; }
        public long ActiveLeases { get; set; }
        public long ExpiredLeases { get; set; }
        public long ExpiringSoonLeases { get; set; }
        public long VerifiedMirrors { get; set; }
        public long RecoveryRequiredMirrors { get; set; }
        public DateTimeOffset? OldestExpiredAtUtc { get; set; }
    }
}
