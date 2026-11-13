namespace GitHubMigrator.Infrastructure.PostgreSql;

// #109 / later F-031: a new append-only checkpoint-scoped target GET.
// Historical original F-029/F-031 rows and layouts are unchanged.
public sealed class GitCheckpointTargetObservationRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public Guid DestinationConnectionId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public string Organization { get; set; } = "";
    public string ObjectFormat { get; set; } = "";
    public Guid ReviewedBy { get; set; }
    public string CandidateEvidenceSha256 { get; set; } = "";
    public string HumanReviewEvidenceSha256 { get; set; } = "";
    public string ReadPreflightEvidenceSha256 { get; set; } = "";
    public string TargetEvidenceSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
    public int RefCount { get; set; }
    public string ReceiptJson { get; set; } = "{}";
    public bool CanObserveTarget { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitCheckpointTargetObservationEventRecord
{
    public long Id { get; set; }
    public Guid ObservationId { get; set; }
    public Guid ReviewedBy { get; set; }
    public string Type { get; set; } = "ObservedReadOnly";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
