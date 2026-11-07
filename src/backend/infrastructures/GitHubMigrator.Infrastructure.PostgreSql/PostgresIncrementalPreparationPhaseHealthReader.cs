using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-067: source-scoped, single-statement read of immutable queue events.
// A *successful final attempt* must have an exact event chain:
//   Queued(0) or RetryScheduled(previous attempt)
//   -> Claimed(current attempt) -> Prepared(current attempt).
// We never pair an old claim with a later retry or manufacture missing
// timestamps. Queue wait includes retry backoff; claim-to-prepared is
// elapsed lease occupancy, not CPU time or actual GitHub migration.
public sealed class PostgresIncrementalPreparationPhaseHealthReader(
    AppDbContext db, IncrementalPreparationQueueHealthOptions options)
    : IIncrementalPreparationPhaseHealthReader
{
    private const string PhaseSql = """
        WITH sample AS MATERIALIZED (
            SELECT clock_timestamp() AS observed_at_utc
        ),
        completed_runs AS MATERIALIZED (
            SELECT p.id, p.attempts, p.prepared_at_utc
            FROM incremental_preparations p
            CROSS JOIN sample
            WHERE p.source_connection_id = {0}
              AND p.state = 'Prepared'
              AND p.prepared_at_utc >=
                  sample.observed_at_utc - interval '24 hours'
              AND p.prepared_at_utc <= sample.observed_at_utc
        ),
        event_chains AS (
            SELECT
                p.attempts,
                origin.occurred_at_utc AS origin_at_utc,
                claimed.occurred_at_utc AS claimed_at_utc,
                finished.occurred_at_utc AS finished_at_utc,
                p.prepared_at_utc,
                sample.observed_at_utc
            FROM completed_runs p
            CROSS JOIN sample
            LEFT JOIN LATERAL (
                SELECT e.id, e.occurred_at_utc
                FROM incremental_preparation_events e
                WHERE e.preparation_id = p.id
                  AND e.event_type = 'Claimed'
                  AND e.attempt = p.attempts
                ORDER BY e.id DESC LIMIT 1
            ) claimed ON true
            LEFT JOIN LATERAL (
                SELECT e.id, e.occurred_at_utc
                FROM incremental_preparation_events e
                WHERE e.preparation_id = p.id
                  AND e.id < claimed.id
                  AND (
                    (p.attempts = 1 AND
                        e.event_type = 'Queued' AND e.attempt = 0)
                    OR
                    (p.attempts > 1 AND
                        e.event_type = 'RetryScheduled' AND
                        e.attempt = p.attempts - 1)
                  )
                ORDER BY e.id DESC LIMIT 1
            ) origin ON true
            LEFT JOIN LATERAL (
                SELECT e.id, e.occurred_at_utc
                FROM incremental_preparation_events e
                WHERE e.preparation_id = p.id
                  AND e.event_type = 'Prepared'
                  AND e.attempt = p.attempts
                  AND e.id > claimed.id
                ORDER BY e.id DESC LIMIT 1
            ) finished ON true
        ),
        measured AS (
            SELECT
                attempts,
                CASE WHEN
                    origin_at_utc IS NOT NULL
                    AND claimed_at_utc IS NOT NULL
                    AND finished_at_utc IS NOT NULL
                    AND origin_at_utc <= claimed_at_utc
                    AND claimed_at_utc <= finished_at_utc
                    AND finished_at_utc - claimed_at_utc <= interval '5 minutes'
                    AND finished_at_utc = prepared_at_utc
                    AND finished_at_utc <= observed_at_utc
                THEN extract(epoch FROM
                    claimed_at_utc - origin_at_utc)::double precision
                ELSE NULL END AS queue_seconds,
                CASE WHEN
                    origin_at_utc IS NOT NULL
                    AND claimed_at_utc IS NOT NULL
                    AND finished_at_utc IS NOT NULL
                    AND origin_at_utc <= claimed_at_utc
                    AND claimed_at_utc <= finished_at_utc
                    AND finished_at_utc - claimed_at_utc <= interval '5 minutes'
                    AND finished_at_utc = prepared_at_utc
                    AND finished_at_utc <= observed_at_utc
                THEN extract(epoch FROM
                    finished_at_utc - claimed_at_utc)::double precision
                ELSE NULL END AS processing_seconds
            FROM event_chains
        )
        SELECT
            sample.observed_at_utc AS "ObservedAtUtc",
            metrics."PreparedLast24Hours",
            metrics."ValidPhaseSamples",
            metrics."MissingOrInvalidSamples",
            metrics."RetriedPreparedLast24Hours",
            metrics."AverageQueueWaitSeconds",
            metrics."AverageClaimToPreparedSeconds",
            metrics."P95ClaimToPreparedSeconds",
            metrics."MaxClaimToPreparedSeconds"
        FROM sample
        CROSS JOIN LATERAL (
            SELECT
                count(*) AS "PreparedLast24Hours",
                count(queue_seconds) AS "ValidPhaseSamples",
                count(*) - count(queue_seconds) AS "MissingOrInvalidSamples",
                count(*) FILTER (
                    WHERE attempts > 1
                ) AS "RetriedPreparedLast24Hours",
                avg(queue_seconds) AS "AverageQueueWaitSeconds",
                avg(processing_seconds) AS "AverageClaimToPreparedSeconds",
                percentile_cont(0.95) WITHIN GROUP (
                    ORDER BY processing_seconds
                ) AS "P95ClaimToPreparedSeconds",
                max(processing_seconds) AS "MaxClaimToPreparedSeconds"
            FROM measured
        ) AS metrics
        """;

    public async Task<IncrementalPreparationPhaseHealth> ReadAsync(
        Guid sourceConnectionId, CancellationToken ct)
    {
        // No database query or existence oracle while feature-off.
        if (!options.Enabled)
            throw new IncrementalPreparationException(
                "IncrementalQueueHealthDisabled");
        if (sourceConnectionId == Guid.Empty)
            throw new IncrementalPreparationException(
                "IncrementalQueueHealthSourceInvalid");

        var row = await db.Database.SqlQueryRaw<PhaseSqlRow>(
            PhaseSql, sourceConnectionId).SingleAsync(ct);
        return new(row.ObservedAtUtc, row.PreparedLast24Hours,
            row.ValidPhaseSamples, row.MissingOrInvalidSamples,
            row.RetriedPreparedLast24Hours,
            row.AverageQueueWaitSeconds,
            row.AverageClaimToPreparedSeconds,
            row.P95ClaimToPreparedSeconds,
            row.MaxClaimToPreparedSeconds);
    }

    // EF Core unmapped SQL projection: no individual job/event columns.
    public sealed class PhaseSqlRow
    {
        public DateTimeOffset ObservedAtUtc { get; set; }
        public long PreparedLast24Hours { get; set; }
        public long ValidPhaseSamples { get; set; }
        public long MissingOrInvalidSamples { get; set; }
        public long RetriedPreparedLast24Hours { get; set; }
        public double? AverageQueueWaitSeconds { get; set; }
        public double? AverageClaimToPreparedSeconds { get; set; }
        public double? P95ClaimToPreparedSeconds { get; set; }
        public double? MaxClaimToPreparedSeconds { get; set; }
    }
}
