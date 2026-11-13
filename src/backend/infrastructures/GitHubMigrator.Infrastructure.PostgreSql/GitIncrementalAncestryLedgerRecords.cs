namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-095 intentionally cannot represent a write, dispatch or migration
// completion. Immutable proof receipt + one append-only Recorded event.
public sealed class GitIncrementalAncestryLedgerRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid TargetObservationId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public string OperationManifestSha256 { get; set; } = "";
    public string SourceEvidenceSha256 { get; set; } = "";
    public string SourceCheckpointAttestationSha256 { get; set; } = "";
    public string TargetEvidenceSha256 { get; set; } = "";
    public string ReceiptSha256 { get; set; } = "";
    public string ReceiptJson { get; set; } = "{}";
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitIncrementalAncestryLedgerEventRecord
{
    public long Id { get; set; }
    public Guid LedgerId { get; set; }
    public string Type { get; set; } = "Recorded";
    public Guid ActorId { get; set; }
    public string ReceiptSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
