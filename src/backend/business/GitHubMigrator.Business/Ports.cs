using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// Providers report capabilities explicitly. Missing capabilities must never be silently ignored.
public sealed record ProviderCapabilities(bool GitRepositories, bool Tfvc, bool IdentityDiscovery, bool PermissionDiscovery, bool FullHistory);
public sealed record SourceConnection(string ReferenceId, SourceSystemKind Kind, Uri BaseUri, string SecretReference);
public sealed record DestinationConnection(string ReferenceId, Uri BaseUri, string Organization, string SecretReference);
public sealed record SourceRepositoryArtifact(string RepositoryId, VersionControlKind Kind, string WorkspacePath);

public interface ISourceProvider
{
    string ProviderKey { get; }
    ProviderCapabilities Capabilities { get; }
    Task<DiscoverySnapshot> DiscoverAsync(SourceConnection connection, CancellationToken cancellationToken);
    Task<SourceRepositoryArtifact> ExportRepositoryAsync(SourceConnection connection, string repositoryId, CancellationToken cancellationToken);
}

public interface IDestinationProvider
{
    string ProviderKey { get; }
    ProviderCapabilities Capabilities { get; }
    Task EnsureRepositoryAsync(DestinationConnection connection, RepositorySelection selection, CancellationToken cancellationToken);
    Task<bool> VerifyRefsAsync(DestinationConnection connection, string repositoryName, IReadOnlyDictionary<string, string> expectedRefs, CancellationToken cancellationToken);
}

public interface IArtifactStorage
{
    Task PutAsync(string objectKey, Stream contents, string contentType, CancellationToken cancellationToken);
    Task<Stream> ReadAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record MigrationIssue(string Code, string RepositoryId, string Description);
public sealed record MigrationPreview(IReadOnlyList<MigrationIssue> Issues, int SelectedRepositories);

public sealed class MigrationPlanner
{
    public MigrationPreview Preview(DiscoverySnapshot snapshot, MigrationPlan plan)
    {
        if (snapshot.SnapshotId != plan.SnapshotId) throw new ArgumentException("Plan belongs to another discovery snapshot.");
        var issues = new List<MigrationIssue>();
        var repositories = snapshot.Projects.SelectMany(x => x.Repositories).ToDictionary(r => r.ProviderId);
        foreach (var selection in plan.Selections.Where(s => s.Include))
        {
            if (!repositories.TryGetValue(selection.SourceRepositoryId, out var repo))
            {
                issues.Add(new("RepositoryNotFound", selection.SourceRepositoryId, "Repository is not present in snapshot."));
                continue;
            }
            if (repo.VersionControl == VersionControlKind.Tfvc && selection.Strategy == HistoryStrategy.GitMirror)
                issues.Add(new("InvalidHistoryStrategy", repo.ProviderId, "TFVC cannot be mirrored as native Git."));
            if (plan.HasOrphanedAccess(repo.ProviderId, snapshot.AccessGrants))
                issues.Add(new("OrphanedAccess", repo.ProviderId, "No mapped or explicitly assigned target users."));
        }
        return new(issues, plan.Selections.Count(s => s.Include));
    }
}
