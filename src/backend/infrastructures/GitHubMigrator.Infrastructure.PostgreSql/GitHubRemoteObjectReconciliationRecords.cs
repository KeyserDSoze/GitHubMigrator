namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteObjectReconciliationRecord
{
    public Guid Id { get; set; }
    public Guid GrantId { get; set; }
    public Guid ReadbackId { get; set; }
    public Guid HazardId { get; set; }
    public long RepositoryId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public string State { get; set; } = "";
    public string OriginalBaselineSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string TargetRefSetSha256 { get; set; } = "";
    public string RemoteRefObservationSha256 { get; set; } = "";
    public string FullObjectClosureSha256 { get; set; } = "";
    public string RemoteMirrorReceiptSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public Guid ObservedBy { get; set; }
    public DateTimeOffset VerifiedAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public bool RemotePushInvoked { get; set; }
    public bool RemoteWritesAuthorized { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitHubRemoteObjectReconciliationEventRecord
{
    public long Id { get; set; }
    public Guid ReconciliationId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
