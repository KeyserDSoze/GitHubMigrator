namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097c.4c.0b: immutable explicit checkpoint-specific HUMAN decision.
// Does not alter F-029/F-031 baseline tables, and never grants
// target observation, GitHub send, retry or execution authority.
public sealed class GitCheckpointTargetHumanReviewRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public Guid DestinationConnectionId { get; set; }
    public string CandidateEvidenceSha256 { get; set; } = "";
    public Guid RequestedBy { get; set; }
    public Guid ReviewedBy { get; set; }
    public string Decision { get; set; } = "";
    public string ReceiptJson { get; set; } = "{}";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset ReviewedAtUtc { get; set; }
    public bool CanObserveTarget { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitCheckpointTargetHumanReviewEventRecord
{
    public long Id { get; set; }
    public Guid DecisionId { get; set; }
    public Guid ReviewerId { get; set; }
    public string Type { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
