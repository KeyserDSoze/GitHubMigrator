namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097b immutable, non-executable source Git ref/OID evidence.
public sealed class GitHubSourceCheckpointGitRefLedgerRecord
{
    public Guid Id { get; set; }
    public Guid CheckpointId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string SourceManifestRootSha256 { get; set; } = "";
    public string ProofJson { get; set; } = "{}";
    public string EvidenceSha256 { get; set; } = "";
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubSourceCheckpointGitRefLedgerEventRecord
{
    public long Id { get; set; }
    public Guid LedgerId { get; set; }
    public string Type { get; set; } = "Recorded";
    public Guid ActorId { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
