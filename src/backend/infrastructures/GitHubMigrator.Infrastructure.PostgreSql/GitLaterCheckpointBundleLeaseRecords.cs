namespace GitHubMigrator.Infrastructure.PostgreSql;

// #113: durable, independently fenced F-035v2 coordination. These
// mutable rows are NOT source-bundle evidence nor execution authority.
public sealed class GitLaterCheckpointBundleLeaseRecord
{
    public Guid WorkpackId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public Guid TargetObservationId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string BundlePlanSha256 { get; set; } = "";
    public string WorkpackEvidenceSha256 { get; set; } = "";
    public string FullRefProofSha256 { get; set; } = "";
    public string TargetReceiptSha256 { get; set; } = "";
    public Guid LeaseToken { get; set; }
    public long Generation { get; set; }
    public Guid ActorId { get; set; }
    public string State { get; set; } = "Held";
    public DateTimeOffset AcquiredAtUtc { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool DurableEvidencePersisted { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitLaterCheckpointBundleLeaseEventRecord
{
    public long Id { get; set; }
    public Guid WorkpackId { get; set; }
    public long Generation { get; set; }
    public Guid ActorId { get; set; }
    public string Type { get; set; } = "";
    public string BundlePlanSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
