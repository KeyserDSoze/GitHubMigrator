namespace GitHubMigrator.Infrastructure.PostgreSql;

// Dry-run and planning-review evidence. A decision here does NOT authorize
// provider writes, migration execution, deletion, or GitHub permission grants.
public sealed class IncrementalReviewRecord
{
    public Guid PreparationId { get; set; }
    public string State { get; set; } = "DryRunReady";
    public string EvidenceSha256 { get; set; } = "";
    public string DryRunSha256 { get; set; } = "";
    public DateTimeOffset DryRunAtUtc { get; set; }
    public DateTimeOffset? SourceValidatedAtUtc { get; set; }
    public DateTimeOffset? DestinationValidatedAtUtc { get; set; }
    public int ItemCount { get; set; }
    public int SensitiveItemCount { get; set; }
    public int RemovalItemCount { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? ReasonCode { get; set; }
    public bool SensitiveAcknowledged { get; set; }
}

public sealed class IncrementalReviewEventRecord
{
    public long Id { get; set; }
    public Guid PreparationId { get; set; }
    public string EventType { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string DryRunSha256 { get; set; } = "";
    public string? ReasonCode { get; set; }
}
