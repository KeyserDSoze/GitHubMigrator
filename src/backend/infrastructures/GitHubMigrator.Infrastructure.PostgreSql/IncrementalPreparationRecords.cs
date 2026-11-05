namespace GitHubMigrator.Infrastructure.PostgreSql;

// Review-only workflow. A prepared item cannot issue a GitHub HTTP request.
public sealed class IncrementalPreparationRecord
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public Guid DestinationConnectionId { get; set; }
    public Guid EnqueuedBy { get; set; }
    public string State { get; set; } = "Queued";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? PreparedAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public int ItemCount { get; set; }
    public int SensitiveItemCount { get; set; }
    public string? EvidenceSha256 { get; set; }
}

public sealed class IncrementalPreparationItemRecord
{
    public Guid PreparationId { get; set; }
    public string AssetType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string ChangeKind { get; set; } = "";
    public string? PreviousFingerprint { get; set; }
    public string? CurrentFingerprint { get; set; }
    public int Revision { get; set; }
    public string ReviewCategory { get; set; } = "SourceChange";
    public string ReviewState { get; set; } = "NeedsReview";
}

public sealed class IncrementalPreparationEventRecord
{
    public long Id { get; set; }
    public Guid PreparationId { get; set; }
    public string EventType { get; set; } = "";
    public int Attempt { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? ErrorCode { get; set; }
}
