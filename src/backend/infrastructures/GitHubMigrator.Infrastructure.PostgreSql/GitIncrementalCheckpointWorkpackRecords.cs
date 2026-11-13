namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097c.4c.1: append-only v1 checkpoint-specific witness.
// Does NOT change any F-029 baseline F-032/F-038 hash.
public sealed class GitIncrementalCheckpointWorkpackRecord
{
    public Guid WorkpackId { get; set; }
    public Guid ReconciliationId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public Guid TargetObservationId { get; set; }
    public string FullRefProofSha256 { get; set; } = "";
    public string ReceiptJson { get; set; } = "{}";
    public string EvidenceSha256 { get; set; } = "";
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}
public sealed class GitIncrementalCheckpointWorkpackEventRecord
{
    public long Id { get; set; }
    public Guid WorkpackId { get; set; }
    public string Type { get; set; } = "Recorded";
    public Guid ActorId { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
