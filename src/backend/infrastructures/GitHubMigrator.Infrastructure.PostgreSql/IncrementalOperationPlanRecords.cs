namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-029: no executable command fields; the DB CHECK constraints prohibit
// promoting an operation or execution authorization to a runnable state.
public sealed class IncrementalOperationPlanRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public Guid DestinationConnectionId { get; set; }
    public string Organization { get; set; } = "";
    public string ReviewSha256 { get; set; } = "";
    public string PreparationSha256 { get; set; } = "";
    public string PreflightSha256 { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public int RepositoryCount { get; set; }
    public int StepCount { get; set; }
    public int ChangedRefCount { get; set; }
    public int SensitiveChangeCount { get; set; }
}

public sealed class IncrementalOperationStepRecord
{
    public Guid PlanId { get; set; }
    public int Ordinal { get; set; }
    public int? DependsOnOrdinal { get; set; }
    public string SourceRepositoryId { get; set; } = "";
    public string TargetRepositoryName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "Blocked";
    public string BlockerCode { get; set; } = "";
    public int ChangedRefs { get; set; }
}

public sealed class IncrementalExecutionAuthorizationAttemptRecord
{
    public long Id { get; set; }
    public Guid PlanId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string Decision { get; set; } = "Denied";
    public string ReasonCode { get; set; } = "";
}
