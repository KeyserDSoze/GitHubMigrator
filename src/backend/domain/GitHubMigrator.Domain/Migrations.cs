using System.Text.RegularExpressions;

namespace GitHubMigrator.Domain;

public enum SourceSystemKind { AzureDevOpsServices, AzureDevOpsServer, TfsServer, Other }
public enum VersionControlKind { Git, Tfvc }
public enum HistoryStrategy { GitMirror, TipOnly, FullHistoryBestEffort, ArchiveAndTip }
public enum MigrationRunState { Draft, Validated, Approved, Queued, Running, Verifying, Succeeded, Failed, PartiallySucceeded }

public sealed record SourceIdentity(string ProviderId, string DisplayName, string? Email);
public sealed record SourceRepository(string ProviderId, string ProjectId, string Name, VersionControlKind VersionControl, string? SourcePath = null);
public sealed record SourceProject(string ProviderId, string Name, IReadOnlyList<SourceRepository> Repositories);
public sealed record RepositoryAccess(string RepositoryId, string SourceIdentityId, string Permission, bool FromGroup);
public sealed record IdentityMapping(string SourceIdentityId, string GitHubLogin);
public sealed record RepositorySelection(string SourceRepositoryId, string TargetRepositoryName, HistoryStrategy Strategy, bool Include = true);

public sealed class DiscoverySnapshot
{
    public required Guid SnapshotId { get; init; }
    public required SourceSystemKind SourceSystem { get; init; }
    public required DateTimeOffset CollectedAt { get; init; }
    public required IReadOnlyList<SourceProject> Projects { get; init; }
    public required IReadOnlyList<SourceIdentity> Identities { get; init; }
    public required IReadOnlyList<RepositoryAccess> AccessGrants { get; init; }
    // Must be explicitly true before inferring that missing target grants imply orphaned access.
    public bool PermissionsComplete { get; init; } = false;
    // Credentials and Git object contents intentionally excluded.
}

public sealed class MigrationPlan
{
    private static readonly Regex RepoName = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private readonly Dictionary<string, IdentityMapping> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RepositorySelection> _selections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _extraAccess = new(StringComparer.OrdinalIgnoreCase);

    public MigrationPlan(Guid snapshotId, string targetOrganization)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot id is required.", nameof(snapshotId));
        if (string.IsNullOrWhiteSpace(targetOrganization)) throw new ArgumentException("Target organization is required.", nameof(targetOrganization));
        SnapshotId = snapshotId;
        TargetOrganization = targetOrganization.Trim();
    }

    public Guid SnapshotId { get; }
    public string TargetOrganization { get; }
    public IReadOnlyCollection<IdentityMapping> IdentityMappings => _identities.Values;
    public IReadOnlyCollection<RepositorySelection> Selections => _selections.Values;

    public void MapIdentity(string sourceIdentityId, string githubLogin)
    {
        if (string.IsNullOrWhiteSpace(sourceIdentityId) || string.IsNullOrWhiteSpace(githubLogin))
            throw new ArgumentException("Source identity and GitHub login are required.");
        _identities[sourceIdentityId] = new IdentityMapping(sourceIdentityId, githubLogin.Trim());
    }

    public void Select(RepositorySelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.SourceRepositoryId)) throw new ArgumentException("Source repo id is required.");
        if (!RepoName.IsMatch(selection.TargetRepositoryName) || selection.TargetRepositoryName is "." or ".." || selection.TargetRepositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid target GitHub repository name.", nameof(selection));
        if (_selections.Values.Any(s => s.Include && selection.Include &&
            !s.SourceRepositoryId.Equals(selection.SourceRepositoryId, StringComparison.OrdinalIgnoreCase) &&
            s.TargetRepositoryName.Equals(selection.TargetRepositoryName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Two selected repositories cannot use the same target name.");
        _selections[selection.SourceRepositoryId] = selection;
    }

    public void AddExplicitAccess(string sourceRepoId, string githubLogin)
    {
        if (string.IsNullOrWhiteSpace(sourceRepoId) || string.IsNullOrWhiteSpace(githubLogin))
            throw new ArgumentException("Repository and login are required.");
        if (!_extraAccess.TryGetValue(sourceRepoId, out var users))
            _extraAccess[sourceRepoId] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        users.Add(githubLogin.Trim());
    }

    public IReadOnlyList<string> ResolveAccess(string sourceRepoId, IEnumerable<RepositoryAccess> grants)
    {
        var users = grants
            .Where(g => g.RepositoryId.Equals(sourceRepoId, StringComparison.OrdinalIgnoreCase))
            .Where(g => _identities.ContainsKey(g.SourceIdentityId))
            .Select(g => _identities[g.SourceIdentityId].GitHubLogin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_extraAccess.TryGetValue(sourceRepoId, out var explicitUsers))
            users.UnionWith(explicitUsers);
        return users.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool HasOrphanedAccess(string sourceRepoId, IEnumerable<RepositoryAccess> grants) =>
        ResolveAccess(sourceRepoId, grants).Count == 0;
}
