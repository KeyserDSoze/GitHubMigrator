namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-061: no secret, ref names, path, Git credential or remote transport.
public sealed class GitSourceMirrorLifecycleRecord
{
    public Guid MirrorId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public Guid WorkpackId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ReconciliationId { get; set; }
    public string BundleSha256 { get; set; } = "";
    public string SourceEvidenceSha256 { get; set; } = "";
    public string RefsSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public long LeaseGeneration { get; set; }
    public Guid? LeasedBy { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public string? ObjectSetSha256 { get; set; }
    public int? ObjectCount { get; set; }
    public string? MirrorProofSha256 { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool RemotePushInvoked { get; set; }
    public bool RemoteWritesAuthorized { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
    public bool MigrationComplete { get; set; }
}

public sealed class GitSourceMirrorLifecycleEventRecord
{
    public long Id { get; set; }
    public Guid MirrorId { get; set; }
    public string Type { get; set; } = "";
    public long Generation { get; set; }
    public Guid WorkerId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
