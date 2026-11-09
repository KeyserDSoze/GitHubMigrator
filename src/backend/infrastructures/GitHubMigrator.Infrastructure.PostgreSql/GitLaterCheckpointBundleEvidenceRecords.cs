namespace GitHubMigrator.Infrastructure.PostgreSql;

// #112: isolated future durable F-035v2 source artifact metadata.
// Never re-use legacy git_source_bundle_evidence (FK to old F-038).
public sealed class GitLaterCheckpointBundleEvidenceRecord
{
    public Guid WorkpackId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public Guid TargetObservationId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string WorkpackEvidenceSha256 { get; set; } = "";
    public string BundlePlanSha256 { get; set; } = "";
    public string FullRefProofSha256 { get; set; } = "";
    public string SourceEvidenceSha256 { get; set; } = "";
    public string SourceManifestRootSha256 { get; set; } = "";
    public string TargetReceiptSha256 { get; set; } = "";
    public string ReconciliationSha256 { get; set; } = "";
    public string BundleFileName { get; set; } = "";
    public string BundleSha256 { get; set; } = "";
    public long BundleBytes { get; set; }
    public int RefCount { get; set; }
    public DateTimeOffset AcquiredAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public Guid RecordedBy { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public bool ReadyForExecution { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitLaterCheckpointBundleEvidenceEventRecord
{
    public long Id { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid ActorId { get; set; }
    public string Type { get; set; } = "Recorded";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
