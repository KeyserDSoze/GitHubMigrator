using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class MigrationPlanTests
{
    [Fact]
    public void Orphaned_access_can_be_explicitly_resolved()
    {
        var plan = new MigrationPlan(Guid.NewGuid(), "destination");
        var grants = new[] { new RepositoryAccess("repo-1", "azure-user-1", "Read", false) };
        Assert.True(plan.HasOrphanedAccess("repo-1", grants));
        plan.MapIdentity("azure-user-1", "github-user-1");
        Assert.False(plan.HasOrphanedAccess("repo-1", grants));
    }

    [Fact]
    public void Explicit_user_can_resolve_unmapped_source_permissions()
    {
        var plan = new MigrationPlan(Guid.NewGuid(), "destination");
        plan.AddExplicitAccess("repo-1", "fallback-user");
        Assert.Equal("fallback-user", Assert.Single(plan.ResolveAccess("repo-1", Array.Empty<RepositoryAccess>())));
    }

    [Fact]
    public void Duplicate_target_repository_names_are_rejected()
    {
        var plan = new MigrationPlan(Guid.NewGuid(), "destination");
        plan.Select(new RepositorySelection("source-one", "shared", HistoryStrategy.GitMirror));
        Assert.Throws<InvalidOperationException>(() =>
            plan.Select(new RepositorySelection("source-two", "SHARED", HistoryStrategy.GitMirror)));
    }

    [Fact]
    public void Tfvc_cannot_be_mirrored_natively()
    {
        var snapshotId = Guid.NewGuid();
        var snapshot = new DiscoverySnapshot
        {
            SnapshotId = snapshotId,
            SourceSystem = SourceSystemKind.TfsServer,
            CollectedAt = DateTimeOffset.UtcNow,
            Projects = new[] { new SourceProject("proj", "Project", new[] {
                new SourceRepository("repo", "proj", "Repo", VersionControlKind.Tfvc) }) },
            Identities = Array.Empty<SourceIdentity>(),
            AccessGrants = Array.Empty<RepositoryAccess>()
        };
        var plan = new MigrationPlan(snapshotId, "destination");
        plan.Select(new RepositorySelection("repo", "repo", HistoryStrategy.GitMirror));
        Assert.Contains(new MigrationPlanner().Preview(snapshot, plan).Issues,
            x => x.Code == "InvalidHistoryStrategy");
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("../escape")]
    [InlineData("project.git")]
    public void Invalid_repository_names_are_rejected(string name)
    {
        var plan = new MigrationPlan(Guid.NewGuid(), "destination");
        Assert.Throws<ArgumentException>(() => plan.Select(new RepositorySelection("repo", name, HistoryStrategy.GitMirror)));
    }
}

public sealed class MigrationPlanPermissionCoverageTests
{
    [Fact]
    public void Missing_permission_inventory_is_not_misrepresented_as_orphaned_users()
    {
        var snapshotId = Guid.NewGuid();
        var snapshot = new DiscoverySnapshot
        {
            SnapshotId = snapshotId,
            SourceSystem = SourceSystemKind.AzureDevOpsServices,
            CollectedAt = DateTimeOffset.UtcNow,
            Projects = [new SourceProject("project", "Project", [
                new SourceRepository("repo", "project", "Repo", VersionControlKind.Git)])],
            Identities = [],
            AccessGrants = [],
            PermissionsComplete = false
        };
        var plan = new MigrationPlan(snapshotId, "target");
        plan.Select(new RepositorySelection("repo", "repo", HistoryStrategy.GitMirror));
        var issues = new MigrationPlanner().Preview(snapshot, plan).Issues;
        Assert.Contains(issues, x => x.Code == "PermissionsNotCollected");
        Assert.DoesNotContain(issues, x => x.Code == "OrphanedAccess");
    }
}
