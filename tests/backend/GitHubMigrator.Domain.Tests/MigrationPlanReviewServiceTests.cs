using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class MigrationPlanReviewServiceTests
{
    [Fact]
    public async Task Preview_reports_renames_conflicts_and_unverified_permissions_without_approval()
    {
        var (service, snapshot, destination) = Fixture();
        var result = await service.PreviewAsync(snapshot.Id, destination.Id,
        [
            new("git-1", "shared", HistoryStrategy.GitMirror),
            new("tfvc-1", "SHARED", HistoryStrategy.GitMirror)
        ], CancellationToken.None);

        Assert.Equal(2, result.SelectedRepositories);
        Assert.Equal(2, result.AvailableRepositories);
        Assert.Equal(2, result.Repositories.Single(r => r.SourceRepositoryId == "git-1").RefCount);
        Assert.Contains(result.Checks, x => x.Code == "DuplicateTargetRepositoryName");
        Assert.Contains(result.Checks, x => x.Code == "TfvcCannotGitMirror");
        Assert.Contains(result.Checks, x => x.Code == "EffectivePermissionsUnverified");
        Assert.Contains(result.Checks, x => x.Code == "TargetRepositoryAvailabilityUnverified");
        Assert.All(result.Repositories, r => Assert.Empty(r.CandidateGitHubLogins));
        Assert.False(result.ReadyForApproval);
        Assert.False(result.TargetMembershipVerified);
    }

    [Fact]
    public async Task Includes_only_selected_repositories_and_rejects_unexpected_source_ids()
    {
        var (service, snapshot, destination) = Fixture();
        var result = await service.PreviewAsync(snapshot.Id, destination.Id,
        [
            new("git-1", "renamed", HistoryStrategy.GitMirror),
            new("tfvc-1", "tfvc-archived", HistoryStrategy.ArchiveAndTip, false)
        ], CancellationToken.None);
        Assert.Single(result.Repositories);
        Assert.Equal("renamed", result.Repositories[0].TargetRepositoryName);

        var exception = await Assert.ThrowsAsync<PlanInputException>(() =>
            service.PreviewAsync(snapshot.Id, destination.Id,
                [new("not-from-snapshot", "repo", HistoryStrategy.GitMirror)], CancellationToken.None));
        Assert.Equal("UnknownSourceRepository", exception.Code);
    }

    [Fact]
    public async Task Invalid_names_and_duplicate_source_selection_do_not_pass_validation()
    {
        var (service, snapshot, destination) = Fixture();
        var result = await service.PreviewAsync(snapshot.Id, destination.Id,
            [new("git-1", "../bad", HistoryStrategy.GitMirror)], CancellationToken.None);
        Assert.Contains(result.Checks, x => x.Code == "InvalidTargetRepositoryName");
        Assert.False(result.ReadyForApproval);

        var exception = await Assert.ThrowsAsync<PlanInputException>(() =>
            service.PreviewAsync(snapshot.Id, destination.Id,
            [
                new("git-1", "first", HistoryStrategy.GitMirror),
                new("git-1", "second", HistoryStrategy.GitMirror)
            ], CancellationToken.None));
        Assert.Equal("DuplicateOrMissingSourceRepository", exception.Code);
    }

    [Fact]
    public async Task Verified_source_evidence_only_yields_candidate_logins_not_real_github_grants()
    {
        var (service, snapshot, destination) = Fixture(verifiedPermissions: true);
        var result = await service.PreviewAsync(snapshot.Id, destination.Id,
            [new("git-1", "renamed", HistoryStrategy.GitMirror)], CancellationToken.None);
        Assert.True(result.EffectivePermissionsVerified);
        Assert.Equal("alice-gh", Assert.Single(result.Repositories[0].CandidateGitHubLogins));
        Assert.False(result.TargetMembershipVerified);
        Assert.False(result.ReadyForApproval);
    }

    private static (MigrationPlanReviewService service, DiscoveryInventory inventory,
        MigrationConnection destination) Fixture(bool verifiedPermissions = false)
    {
        var id = Guid.NewGuid();
        var destination = new MigrationConnection(Guid.NewGuid(), "Target", "github-enterprise-cloud",
            ConnectionDirection.Destination, "gh-org", "secret-ref", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "Verified", "operator", "not_checked");
        var inventory = new DiscoveryInventory
        {
            Id = id, ConnectionId = Guid.NewGuid(), Organization = "source-org",
            SourceProvider = "azure-devops-services", CollectedAtUtc = DateTimeOffset.UtcNow,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = id, CollectedAt = DateTimeOffset.UtcNow,
                SourceSystem = SourceSystemKind.AzureDevOpsServices,
                Projects = [new SourceProject("project", "Project",
                [
                    new SourceRepository("git-1", "project", "GitRepo", VersionControlKind.Git),
                    new SourceRepository("tfvc-1", "project", "TfvcRoot", VersionControlKind.Tfvc)
                ])],
                Identities = [new SourceIdentity("alice", "Alice", null)],
                AccessGrants = [new RepositoryAccess("git-1", "alice", "Read", false)],
                PermissionsComplete = verifiedPermissions
            },
            Refs = [new RepositoryRef("git-1", "refs/heads/main", "abc"),
                new RepositoryRef("git-1", "refs/tags/v1", "def")],
            Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = true, PermissionsComplete = verifiedPermissions,
            SecurityCoverage = new DiscoveryCoverage(true, true, verifiedPermissions, verifiedPermissions)
        };
        var mapping = new IdentityMappingDraft(id, destination.Id, "gh-org", 1,
            DateTimeOffset.UtcNow, [new IdentityMapping("alice", "alice-gh")]);
        return (new MigrationPlanReviewService(new FakeDiscovery(inventory),
            new FakeConnections(destination), new FakeMappings(mapping)), inventory, destination);
    }

    private sealed class FakeDiscovery(DiscoveryInventory inventory) : IDiscoveryRepository
    {
        public Task SaveAsync(DiscoveryInventory item, CancellationToken ct) =>
            throw new NotSupportedException("Plan preview may not write a discovery");
        public Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(id == inventory.Id ? inventory : null);
        public Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveryInventory>>([inventory]);
    }

    private sealed class FakeConnections(MigrationConnection connection) : IConnectionRepository
    {
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MigrationConnection>>([connection]);
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(id == connection.Id ? connection : null);
        public Task SaveAsync(MigrationConnection item, CancellationToken ct) =>
            throw new NotSupportedException("Plan preview may not write a connection");
        public Task DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException("Plan preview may not delete a connection");
    }

    private sealed class FakeMappings(IdentityMappingDraft mapping) : IIdentityMappingDraftRepository
    {
        public Task<IdentityMappingDraft?> GetAsync(Guid snapshot, Guid destination, CancellationToken ct) =>
            Task.FromResult(snapshot == mapping.SnapshotId &&
                destination == mapping.DestinationConnectionId ? mapping : null);
        public Task<bool> SaveAsync(IdentityMappingDraft draft, int expectedRevision, CancellationToken ct) =>
            throw new NotSupportedException("Plan preview may not modify mapping drafts");
    }
}
