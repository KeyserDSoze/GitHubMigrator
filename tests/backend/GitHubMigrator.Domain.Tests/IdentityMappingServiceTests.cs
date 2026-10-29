using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class IdentityMappingServiceTests
{
    [Fact]
    public async Task Saves_one_to_one_mapping_bound_to_snapshot_and_destination_without_granting_access()
    {
        var c = Context();
        var result = await c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0,
            [new("aad.alice", "alice-gh"), new("aad.bob", "bob-gh")], CancellationToken.None);
        Assert.Equal(1, result.Draft.Revision);
        Assert.Equal("enterprise-org", result.Draft.TargetOrganization);
        Assert.Equal(2, result.Preview.MappedIdentities);
        Assert.Equal(0, result.Preview.UnmappedIdentities);
        Assert.False(result.Preview.ReadyForGrants);
        Assert.False(result.Preview.EffectivePermissionsVerified);
        Assert.False(result.Preview.TargetAccountsVerified);
        Assert.Contains(result.Preview.Issues, x => x.Code == "EffectivePermissionsUnverified");
        Assert.Contains(result.Preview.Issues, x => x.Code == "TargetMembershipUnverified");
        Assert.Empty(c.Inventory.Snapshot.AccessGrants);
        var fetched = await c.Service.GetAsync(c.Inventory.Id, c.Destination.Id, CancellationToken.None);
        Assert.Equal("bob-gh", fetched.Draft.Mappings.Single(x => x.SourceIdentityId == "aad.bob").GitHubLogin);
    }

    [Fact]
    public async Task Partial_snapshot_and_missing_mapping_are_review_items_not_inferred_permissions()
    {
        var c = Context(identitiesComplete: false);
        var result = await c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0,
            [new("aad.alice", "alice-gh")], CancellationToken.None);
        Assert.Equal(1, result.Preview.UnmappedIdentities);
        Assert.Contains(result.Preview.Issues, x =>
            x.Code == "UnmappedSourceIdentity" && x.SourceIdentityId == "aad.bob");
        Assert.Contains(result.Preview.Issues, x => x.Code == "SourceIdentitiesIncomplete");
        Assert.Contains(result.Preview.Issues, x => x.Code == "AclPrincipalUnresolved");
        Assert.Equal(1, result.Preview.UnresolvedAclPrincipals);
    }

    [Theory]
    [InlineData("not a login")]
    [InlineData("someone@domain.com")]
    [InlineData("-starts-hyphen")]
    [InlineData("ends-hyphen-")]
    [InlineData("double--dash")]
    [InlineData("https://github.com/example")]
    public async Task Rejects_invalid_github_login_and_does_not_save(string login)
    {
        var c = Context();
        var ex = await Assert.ThrowsAsync<MappingInputException>(() =>
            c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0, [new("aad.alice", login)], CancellationToken.None));
        Assert.Equal("InvalidGitHubLogin", ex.Code);
        Assert.Null(await c.Drafts.GetAsync(c.Inventory.Id, c.Destination.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_login_and_unknown_identity_rejected()
    {
        var c = Context();
        var duplicate = await Assert.ThrowsAsync<MappingInputException>(() =>
            c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0,
                [new("aad.alice", "Alice-GH"), new("aad.bob", "alice-gh")], CancellationToken.None));
        Assert.Equal("DuplicateGitHubLogin", duplicate.Code);
        var unknown = await Assert.ThrowsAsync<MappingInputException>(() =>
            c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0, [new("aad.other", "other")], CancellationToken.None));
        Assert.Equal("UnknownSourceIdentity", unknown.Code);
    }

    [Fact]
    public async Task Stale_tab_must_not_overwrite_a_newer_revision()
    {
        var c = Context();
        await c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0,
            [new("aad.alice", "alice")], CancellationToken.None);
        await Assert.ThrowsAsync<MappingRevisionConflictException>(() =>
            c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0,
                [new("aad.alice", "attacker")], CancellationToken.None));
        var updated = await c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 1,
            [new("aad.alice", "alice2")], CancellationToken.None);
        Assert.Equal(2, updated.Draft.Revision);
        Assert.Equal("alice2", updated.Draft.Mappings.Single().GitHubLogin);
    }

    [Fact]
    public async Task Rejects_unsupported_destination_and_empty_snapshot()
    {
        var c = Context(isDestination: false);
        var invalid = await Assert.ThrowsAsync<MappingInputException>(() =>
            c.Service.GetAsync(c.Inventory.Id, c.Destination.Id, CancellationToken.None));
        Assert.Equal("NotGitHubDestination", invalid.Code);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            c.Service.GetAsync(Guid.NewGuid(), c.Destination.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Drafts_are_independent_for_each_snapshot()
    {
        var c = Context();
        await c.Service.SaveAsync(c.Inventory.Id, c.Destination.Id, 0,
            [new("aad.alice", "alice-gh")], CancellationToken.None);
        var newSnapshot = c.Inventory withNewSnapshot();
        var another = new IdentityMappingService(new FakeInventories([c.Inventory, newSnapshot]),
            new FakeConnections(c.Destination), c.Drafts, TimeProvider.System);
        var result = await another.GetAsync(newSnapshot.Id, c.Destination.Id, CancellationToken.None);
        Assert.Equal(0, result.Draft.Revision);
        Assert.Empty(result.Draft.Mappings);
    }

    private static Fixture Context(bool identitiesComplete = true, bool isDestination = true)
    {
        var sid = Guid.NewGuid();
        var clock = DateTimeOffset.UtcNow;
        var snap = new DiscoverySnapshot
        {
            SnapshotId = sid, SourceSystem = SourceSystemKind.AzureDevOpsServices,
            CollectedAt = clock, Projects = [],
            Identities = [new("aad.alice", "Alice", "alice@example.invalid", "legacy-alice"),
                new("aad.bob", "Bob", null, "legacy-bob")],
            AccessGrants = [], PermissionsComplete = false
        };
        var inventory = new DiscoveryInventory
        {
            Id = sid, ConnectionId = Guid.NewGuid(), SourceProvider = "azure-devops-services",
            Organization = "source-org", CollectedAtUtc = clock, Snapshot = snap,
            Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = identitiesComplete, PermissionsComplete = false,
            AclEvidence = [new("repo", "repoV2/project/repo", "unresolved-legacy",
                2, 0, true, "repository")]
        };
        var destination = new MigrationConnection(Guid.NewGuid(), "GitHub", "github-enterprise-cloud",
            isDestination ? ConnectionDirection.Destination : ConnectionDirection.Source,
            "enterprise-org", "never-return-me", clock, clock, "Verified", "admin-gh",
            "organization_write_unverified");
        var drafts = new FakeDrafts();
        return new(inventory, destination, drafts, new IdentityMappingService(
            new FakeInventories([inventory]), new FakeConnections(destination), drafts, TimeProvider.System));
    }

    private sealed record Fixture(DiscoveryInventory Inventory, MigrationConnection Destination,
        FakeDrafts Drafts, IdentityMappingService Service);

    private sealed class FakeInventories(IReadOnlyList<DiscoveryInventory> items) : IDiscoveryRepository
    {
        public Task SaveAsync(DiscoveryInventory inventory, CancellationToken ct) => Task.CompletedTask;
        public Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(items.FirstOrDefault(i => i.Id == id));
        public Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct) =>
            Task.FromResult(items);
    }

    private sealed class FakeConnections(MigrationConnection destination) : IConnectionRepository
    {
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<MigrationConnection?>(id == destination.Id ? destination : null);
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MigrationConnection>>([destination]);
        public Task SaveAsync(MigrationConnection connection, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDrafts : IIdentityMappingDraftRepository
    {
        private readonly Dictionary<(Guid, Guid), IdentityMappingDraft> _stored = new();
        public Task<IdentityMappingDraft?> GetAsync(Guid snapshot, Guid dest, CancellationToken ct) =>
            Task.FromResult(_stored.GetValueOrDefault((snapshot, dest)));
        public Task<bool> SaveAsync(IdentityMappingDraft draft, int revision, CancellationToken ct)
        {
            var key = (draft.SnapshotId, draft.DestinationConnectionId);
            var exists = _stored.TryGetValue(key, out var old);
            if ((exists ? old!.Revision : 0) != revision) return Task.FromResult(false);
            _stored[key] = draft;
            return Task.FromResult(true);
        }
    }
}

internal static class MappingTestInventoryExtensions
{
    public static DiscoveryInventory withNewSnapshot(this DiscoveryInventory original)
    {
        var id = Guid.NewGuid();
        return new DiscoveryInventory
        {
            Id = id, ConnectionId = original.ConnectionId, SourceProvider = original.SourceProvider,
            Organization = original.Organization, CollectedAtUtc = original.CollectedAtUtc,
            Snapshot = new DiscoverySnapshot
            {
                SnapshotId = id, SourceSystem = original.Snapshot.SourceSystem,
                CollectedAt = original.Snapshot.CollectedAt, Projects = original.Snapshot.Projects,
                Identities = original.Snapshot.Identities, AccessGrants = [], PermissionsComplete = false
            },
            Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = false, PermissionsComplete = false
        };
    }
}
