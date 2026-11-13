namespace GitHubMigrator.Infrastructure.PostgreSql;

// #104: isolated F-032v2 and F-038v2 storage. No legacy dispatch,
// transport, F-035, or historical F-031 foreign keys are reachable.
public sealed class GitLaterCheckpointReconciliationRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public Guid TargetObservationId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string SourceEvidenceSha256 { get; set; } = "";
    public string FullRefProofSha256 { get; set; } = "";
    public string TargetReceiptSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
    public int SourceRefCount { get; set; }
    public int TargetRefCount { get; set; }
    public int MatchingCount { get; set; }
    public int CreateCandidateCount { get; set; }
    public int DivergedCount { get; set; }
    public int TargetOnlyCount { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitLaterCheckpointWorkpackRecord
{
    public Guid Id { get; set; }
    public Guid ReconciliationId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public Guid TargetObservationId { get; set; }
    public string TargetReceiptSha256 { get; set; } = "";
    public string FullRefProofSha256 { get; set; } = "";
    public string ReceiptJson { get; set; } = "{}";
    public string EvidenceSha256 { get; set; } = "";
    public string State { get; set; } = "Blocked";
    public string Phase { get; set; } = "WriteBlocked";
    public int Attempts { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool ReadyForExecution { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitLaterCheckpointWorkpackEventRecord
{
    public long Id { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid ActorId { get; set; }
    public string Type { get; set; } = "Recorded";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
