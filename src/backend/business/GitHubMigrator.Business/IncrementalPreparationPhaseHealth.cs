namespace GitHubMigrator.Business;

// F-067: aggregate timing evidence from immutable F-026 events.
// Neither individual runs nor identifiers, raw event payloads, Git
// paths, credentials or lease handles appear in the response.
public sealed record IncrementalPreparationPhaseHealth(
    DateTimeOffset ObservedAtUtc,
    long PreparedLast24Hours,
    long ValidPhaseSamples,
    long MissingOrInvalidSamples,
    long RetriedPreparedLast24Hours,
    double? AverageQueueWaitSeconds,
    double? AverageClaimToPreparedSeconds,
    double? P95ClaimToPreparedSeconds,
    double? MaxClaimToPreparedSeconds)
{
    public bool TimingEvidenceIncomplete => MissingOrInvalidSamples > 0;
}

public interface IIncrementalPreparationPhaseHealthReader
{
    Task<IncrementalPreparationPhaseHealth> ReadAsync(
        Guid sourceConnectionId, CancellationToken ct);
}
