namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubSourceSyncCheckpointRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid? PreviousCheckpointId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string ProofJson { get; set; } = "{}";
    public string EvidenceSha256 { get; set; } = "";
    // Version 1 = legacy count-only; version 2 = ref-fingerprint evidence.
    public int EvidenceVersion { get; set; } = 1;
    public string? RefChangeManifestJson { get; set; }
    public string? RefChangeAttestationSha256 { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool CanPush { get; set; }
    public bool CanRetry { get; set; }
    public bool CanDispatch { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubSourceSyncCheckpointEventRecord
{
    public long Id { get; set; }
    public Guid CheckpointId { get; set; }
    public string Type { get; set; } = "Recorded";
    public Guid ActorId { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
