namespace GitHubMigrator.Domain;

// Canonical source metadata. No PAT, cookie, authorization header, credential URL, or secret reference.
public sealed record RepositoryRef(string RepositoryId, string Name, string ObjectId);
public sealed record DiscoveryIssue(string Code, string? ProjectId = null, string? RepositoryId = null);
public enum DiscoveryCompleteness { Complete, Partial }

public sealed class DiscoveryInventory
{
    public required Guid Id { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string SourceProvider { get; init; }
    public required string Organization { get; init; }
    public required DateTimeOffset CollectedAtUtc { get; init; }
    public required DiscoverySnapshot Snapshot { get; init; }
    public required IReadOnlyList<RepositoryRef> Refs { get; init; }
    public required IReadOnlyList<DiscoveryIssue> Issues { get; init; }
    public required DiscoveryCompleteness Completeness { get; init; }
    // Granular coverage flag prevents calling missing Graph scope an empty identity inventory.
    public required bool IdentitiesComplete { get; init; }
    public required bool PermissionsComplete { get; init; }
}
