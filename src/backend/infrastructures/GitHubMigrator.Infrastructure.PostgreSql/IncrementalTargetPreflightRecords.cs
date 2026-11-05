namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-028: bounded, credential-free provider GET observations.
// Nothing in this schema is an execution permit or permission grant.
public sealed class IncrementalTargetPreflightRecord
{
    public Guid PreparationId { get; set; }
    public string Organization { get; set; } = "";
    public string ReviewSha256 { get; set; } = "";
    public string EvidenceSha256 { get; set; } = "";
    public DateTimeOffset CheckedAtUtc { get; set; }
    public int Revision { get; set; }
    public string State { get; set; } = "";
    public string MembershipStatus { get; set; } = "";
    public string MembershipRole { get; set; } = "";
    public string PrivateRepositoryPolicy { get; set; } = "";
    public DateTimeOffset? SourceValidatedAtUtc { get; set; }
    public DateTimeOffset? DestinationValidatedAtUtc { get; set; }
    public int RepositoryCount { get; set; }
    public int VisibleCollisions { get; set; }
    public int UncertainRepositories { get; set; }
}
public sealed class IncrementalTargetProbeRecord
{
    public Guid PreparationId { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public string HistoryStrategy { get; set; } = "";
    public string Status { get; set; } = "";
}
public sealed class IncrementalTargetPreflightEventRecord
{
    public long Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
    public string EvidenceSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public int Revision { get; set; }
}
