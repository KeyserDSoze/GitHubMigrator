using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// The remote Git URL is derived from the exact immutable F-029/F-014
// historical source inventory, NEVER from a UI field or mutable staging.
public sealed class PostgresAzureGitSourceLocationResolver(AppDbContext db)
    : IAzureGitSourceLocationResolver
{
    public async Task<AzureGitSourceLocation> ResolveAsync(
        Guid snapshotId, Guid connectionId, string repositoryId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || connectionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(repositoryId) || repositoryId.Length > 256)
            throw new GitSourceBundleException("AzureGitSourceMappingInvalid");

        var inventory = await new PostgresDiscoveryRepository(db)
            .GetWithoutRefsAsync(snapshotId, ct);
        if (inventory is null || inventory.Id != snapshotId ||
            inventory.ConnectionId != connectionId ||
            inventory.SourceProvider != "azure-devops-services" ||
            inventory.Snapshot.SourceSystem != SourceSystemKind.AzureDevOpsServices)
            throw new GitSourceBundleException("AzureGitSourceSnapshotUnavailable");

        var matches = inventory.Snapshot.Projects
            .SelectMany(project => project.Repositories
                .Where(repo => repo.ProviderId == repositoryId &&
                    repo.VersionControl == VersionControlKind.Git)
                .Select(repo => new { Project = project, Repo = repo }))
            .Take(2).ToArray();
        if (matches.Length != 1)
            throw new GitSourceBundleException("AzureGitSourceMappingInvalid");
        return new(connectionId, inventory.Organization,
            matches[0].Project.Name, matches[0].Repo.Name, repositoryId);
    }
}
