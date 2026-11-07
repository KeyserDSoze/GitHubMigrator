namespace GitHubMigrator.Business;

// F-066: source-scoped, read-only operational summary. No source,
// snapshot, destination, actor or lease identifiers are returned.
public sealed record IncrementalPreparationQueueHealth(
    DateTimeOffset ObservedAtUtc,
    long TotalRuns,
    long QueuedReady,
    long QueuedDeferred,
    long RunningCurrent,
    long RunningExpired,
    long Prepared,
    long Failed,
    long RetryingQueued,
    long PreparedLast24Hours,
    long PreparedReviewItemsLast24Hours,
    long FailedLast24Hours,
    long FailedSourceEvidence,
    long FailedTimeoutOrLease,
    long FailedOther,
    double? AverageTurnaroundSecondsLast24Hours,
    double? OldestReadyWaitSeconds)
{
    // A timed-out lease or a recent terminal failure calls for human
    // investigation, never authorization to start a GitHub write.
    public bool AttentionRequired =>
        RunningExpired > 0 || FailedLast24Hours > 0;
}

public sealed record IncrementalPreparationQueueHealthOptions(bool Enabled);

public interface IIncrementalPreparationQueueHealthReader
{
    Task<IncrementalPreparationQueueHealth> ReadAsync(
        Guid sourceConnectionId, CancellationToken ct);
}
