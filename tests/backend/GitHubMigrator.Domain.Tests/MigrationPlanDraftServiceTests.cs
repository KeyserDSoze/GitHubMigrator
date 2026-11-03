using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class MigrationPlanDraftServiceTests
{
    [Fact]
    public async Task Saves_and_reloads_versioned_plan_without_approving_or_writing_to_providers()
    {
        var fixture = NewFixture();
        var initial = await fixture.Service.GetAsync(fixture.Inventory.Id, fixture.Destination.Id, default);
        Assert.Equal(0, initial.Revision);
        Assert.Empty(initial.Repositories);

        var saved = await fixture.Service.SaveAsync(fixture.Inventory.Id, fixture.Destination.Id,
            0, [new("source-git", "renamed", HistoryStrategy.GitMirror)], default);
        Assert.Equal(1, saved.Revision);
        Assert.Equal("gh-target", saved.TargetOrganization);
        Assert.Equal("renamed", Assert.Single(saved.Repositories).TargetRepositoryName);
        var fetched = await fixture.Service.GetAsync(fixture.Inventory.Id, fixture.Destination.Id, default);
        Assert.Equal(1, fetched.Revision);
        Assert.Empty((await fixture.Preview.PreviewAsync(fixture.Inventory.Id, fixture.Destination.Id,
            saved.Repositories, default)).Repositories[0].CandidateGitHubLogins);
    }

    [Fact]
    public async Task A_stale_revision_cannot_overwrite_another_admins_changes()
    {
        var fixture = NewFixture();
        await fixture.Service.SaveAsync(fixture.Inventory.Id, fixture.Destination.Id,
            0, [new("source-git", "first", HistoryStrategy.GitMirror)], default);
        await Assert.ThrowsAsync<PlanRevisionConflictException>(() =>
            fixture.Service.SaveAsync(fixture.Inventory.Id, fixture.Destination.Id,
                0, [new("source-git", "stale", HistoryStrategy.GitMirror)], default));
        var latest = await fixture.Service.SaveAsync(fixture.Inventory.Id, fixture.Destination.Id,
            1, [new("source-git", "updated", HistoryStrategy.GitMirror)], default);
        Assert.Equal(2, latest.Revision);
    }

    [Fact]
    public async Task Rejected_source_ids_and_null_batches_never_get_persisted()
    {
        var fixture = NewFixture();
        var unknown = await Assert.ThrowsAsync<PlanInputException>(() =>
            fixture.Service.SaveAsync(fixture.Inventory.Id, fixture.Destination.Id,
                0, [new("foreign-repo", "repo", HistoryStrategy.GitMirror)], default));
        Assert.Equal("UnknownSourceRepository", unknown.Code);
        await Assert.ThrowsAsync<PlanInputException>(() =>
            fixture.Service.SaveAsync(fixture.Inventory.Id, fixture.Destination.Id, 0, null, default));
        Assert.Equal(0, (await fixture.Service.GetAsync(fixture.Inventory.Id, fixture.Destination.Id, default)).Revision);
    }

    private static (MigrationPlanDraftService Service, MigrationPlanReviewService Preview,
        DiscoveryInventory Inventory, MigrationConnection Destination) NewFixture()
    {
        var id = Guid.NewGuid();
        var destination = new MigrationConnection(Guid.NewGuid(), "Destination",
            "github-enterprise-cloud", ConnectionDirection.Destination, "gh-target",
            "dummy-secret", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Verified",
            "operator", "not_checked");
        var inventory = new DiscoveryInventory
        {
            Id = id, ConnectionId = Guid.NewGuid(), SourceProvider = "azure-devops-services",
            Organization = "az-org", CollectedAtUtc = DateTimeOffset.UtcNow,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = id, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = DateTimeOffset.UtcNow,
                Projects = [new SourceProject("project", "Project", [
                    new SourceRepository("source-git", "project", "Source", VersionControlKind.Git)])],
                Identities = [], AccessGrants = [], PermissionsComplete = false
            },
            Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
        var reader = new FakeDiscovery(inventory);
        var connections = new FakeConnections(destination);
        var mappings = new EmptyMappings();
        var preview = new MigrationPlanReviewService(reader, connections, mappings);
        return (new MigrationPlanDraftService(preview, new InMemoryDrafts(), TimeProvider.System),
            preview, inventory, destination);
    }

    private sealed class InMemoryDrafts : IMigrationPlanDraftRepository
    {
        private MigrationPlanDraft? _draft;
        public Task<MigrationPlanDraft?> GetAsync(Guid snapshotId, Guid destinationId, CancellationToken ct) =>
            Task.FromResult(_draft is not null && _draft.SnapshotId == snapshotId &&
                _draft.DestinationConnectionId == destinationId ? _draft : null);
        public Task<bool> SaveAsync(MigrationPlanDraft draft, int expectedRevision, CancellationToken ct)
        {
            if ((_draft?.Revision ?? 0) != expectedRevision) return Task.FromResult(false);
            _draft = draft;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDiscovery(DiscoveryInventory inventory) : IDiscoveryRepository
    {
        public Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(id == inventory.Id ? inventory : null);
        public Task SaveAsync(DiscoveryInventory item, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveryInventory>>([inventory]);
    }

    private sealed class FakeConnections(MigrationConnection item) : IConnectionRepository
    {
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(id == item.Id ? item : null);
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MigrationConnection>>([item]);
        public Task SaveAsync(MigrationConnection connection, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyMappings : IIdentityMappingDraftRepository
    {
        public Task<IdentityMappingDraft?> GetAsync(Guid snapshot, Guid destination, CancellationToken ct) =>
            Task.FromResult<IdentityMappingDraft?>(null);
        public Task<bool> SaveAsync(IdentityMappingDraft draft, int revision, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
