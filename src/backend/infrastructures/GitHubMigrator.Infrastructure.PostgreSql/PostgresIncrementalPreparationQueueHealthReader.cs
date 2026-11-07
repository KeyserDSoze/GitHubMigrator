using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// A single source-filtered aggregate query, with one database timestamp.
// This reader does not retrieve tokens, detailed error strings, Git refs,
// connection secrets, individual work items or raw source identities.
public sealed class PostgresIncrementalPreparationQueueHealthReader(
    AppDbContext db, IncrementalPreparationQueueHealthOptions options)
    : IIncrementalPreparationQueueHealthReader
{
    private const string HealthSql = """
        WITH sample AS MATERIALIZED (
            SELECT clock_timestamp() AS observed_at_utc
        )
        SELECT
            sample.observed_at_utc AS "ObservedAtUtc",
            metrics."TotalRuns",
            metrics."QueuedReady",
            metrics."QueuedDeferred",
            metrics."RunningCurrent",
            metrics."RunningExpired",
            metrics."Prepared",
            metrics."Failed",
            metrics."RetryingQueued",
            metrics."PreparedLast24Hours",
            metrics."PreparedReviewItemsLast24Hours",
            metrics."FailedLast24Hours",
            metrics."FailedSourceEvidence",
            metrics."FailedTimeoutOrLease",
            metrics."FailedOther",
            metrics."AverageTurnaroundSecondsLast24Hours",
            metrics."OldestReadyWaitSeconds"
        FROM sample
        CROSS JOIN LATERAL (
            SELECT
                count(*) AS "TotalRuns",
                count(*) FILTER (
                    WHERE state = 'Queued'
                      AND available_at_utc <= sample.observed_at_utc
                ) AS "QueuedReady",
                count(*) FILTER (
                    WHERE state = 'Queued'
                      AND available_at_utc > sample.observed_at_utc
                ) AS "QueuedDeferred",
                count(*) FILTER (
                    WHERE state = 'Running'
                      AND lease_until_utc > sample.observed_at_utc
                ) AS "RunningCurrent",
                count(*) FILTER (
                    WHERE state = 'Running'
                      AND lease_until_utc <= sample.observed_at_utc
                ) AS "RunningExpired",
                count(*) FILTER (
                    WHERE state = 'Prepared'
                ) AS "Prepared",
                count(*) FILTER (
                    WHERE state = 'Failed'
                ) AS "Failed",
                count(*) FILTER (
                    WHERE state = 'Queued' AND attempts > 0
                ) AS "RetryingQueued",
                count(*) FILTER (
                    WHERE state = 'Prepared'
                      AND prepared_at_utc >= sample.observed_at_utc - interval '24 hours'
                ) AS "PreparedLast24Hours",
                coalesce(sum(item_count) FILTER (
                    WHERE state = 'Prepared'
                      AND prepared_at_utc >= sample.observed_at_utc - interval '24 hours'
                ), 0)::bigint AS "PreparedReviewItemsLast24Hours",
                count(*) FILTER (
                    WHERE state = 'Failed'
                      AND updated_at_utc >= sample.observed_at_utc - interval '24 hours'
                ) AS "FailedLast24Hours",
                count(*) FILTER (
                    WHERE state = 'Failed'
                      AND last_error_code IN (
                          'IncrementalSnapshotSuperseded',
                          'IncrementalJournalUnavailable',
                          'IncrementalJournalMismatch',
                          'IncrementalSourceMismatch')
                ) AS "FailedSourceEvidence",
                count(*) FILTER (
                    WHERE state = 'Failed'
                      AND last_error_code IN (
                          'PreparationDeadlineExceeded',
                          'LeaseExpiredAfterMaxAttempts')
                ) AS "FailedTimeoutOrLease",
                count(*) FILTER (
                    WHERE state = 'Failed'
                      AND (last_error_code IS NULL
                        OR last_error_code NOT IN (
                            'IncrementalSnapshotSuperseded',
                            'IncrementalJournalUnavailable',
                            'IncrementalJournalMismatch',
                            'IncrementalSourceMismatch',
                            'PreparationDeadlineExceeded',
                            'LeaseExpiredAfterMaxAttempts'))
                ) AS "FailedOther",
                avg(greatest(0, extract(epoch FROM
                    prepared_at_utc - created_at_utc))) FILTER (
                    WHERE state = 'Prepared'
                      AND prepared_at_utc >= sample.observed_at_utc - interval '24 hours'
                )::double precision AS "AverageTurnaroundSecondsLast24Hours",
                greatest(0, extract(epoch FROM
                    sample.observed_at_utc - min(created_at_utc) FILTER (
                        WHERE state = 'Queued'
                          AND available_at_utc <= sample.observed_at_utc
                    )))::double precision AS "OldestReadyWaitSeconds"
            FROM incremental_preparations
            WHERE source_connection_id = {0}
        ) AS metrics
        """;

    public async Task<IncrementalPreparationQueueHealth> ReadAsync(
        Guid sourceConnectionId, CancellationToken ct)
    {
        // Validate before even attempting a database query. Default-off
        // operation exposes nothing to a caller, including source presence.
        if (!options.Enabled)
            throw new IncrementalPreparationException(
                "IncrementalQueueHealthDisabled");
        if (sourceConnectionId == Guid.Empty)
            throw new IncrementalPreparationException(
                "IncrementalQueueHealthSourceInvalid");

        var row = await db.Database.SqlQueryRaw<HealthSqlRow>(
            HealthSql, sourceConnectionId).SingleAsync(ct);
        return new(row.ObservedAtUtc, row.TotalRuns,
            row.QueuedReady, row.QueuedDeferred,
            row.RunningCurrent, row.RunningExpired,
            row.Prepared, row.Failed, row.RetryingQueued,
            row.PreparedLast24Hours,
            row.PreparedReviewItemsLast24Hours,
            row.FailedLast24Hours, row.FailedSourceEvidence,
            row.FailedTimeoutOrLease, row.FailedOther,
            row.AverageTurnaroundSecondsLast24Hours,
            row.OldestReadyWaitSeconds);
    }

    // Unmapped projection: only aggregate metrics, not individual records.
    public sealed class HealthSqlRow
    {
        public DateTimeOffset ObservedAtUtc { get; set; }
        public long TotalRuns { get; set; }
        public long QueuedReady { get; set; }
        public long QueuedDeferred { get; set; }
        public long RunningCurrent { get; set; }
        public long RunningExpired { get; set; }
        public long Prepared { get; set; }
        public long Failed { get; set; }
        public long RetryingQueued { get; set; }
        public long PreparedLast24Hours { get; set; }
        public long PreparedReviewItemsLast24Hours { get; set; }
        public long FailedLast24Hours { get; set; }
        public long FailedSourceEvidence { get; set; }
        public long FailedTimeoutOrLease { get; set; }
        public long FailedOther { get; set; }
        public double? AverageTurnaroundSecondsLast24Hours { get; set; }
        public double? OldestReadyWaitSeconds { get; set; }
    }
}
