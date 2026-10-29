using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class DiscoveryServiceTests
{
    [Fact]
    public async Task Scan_reads_saved_secret_privately_and_persists_inventory()
    {
        var connection = NewConnection(ConnectionDirection.Source);
        var connRepo = new FakeConnections(connection);
        var secrets = new FakeSecrets(connection.SecretReference, "test-private-pat");
        var reader = new FakeReader();
        var storage = new FakeInventories();
        var service = new DiscoveryService(connRepo, secrets, [reader], storage);
        var inventory = await service.ScanAsync(connection.Id, CancellationToken.None);
        Assert.Equal(connection.Id, inventory.ConnectionId);
        Assert.Equal("test-private-pat", reader.ReceivedPat);
        Assert.DoesNotContain("test-private-pat", System.Text.Json.JsonSerializer.Serialize(inventory));
        Assert.Single(await storage.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Destination_cannot_be_scanned_as_source()
    {
        var connection = NewConnection(ConnectionDirection.Destination);
        var service = new DiscoveryService(new FakeConnections(connection),
            new FakeSecrets(connection.SecretReference, "test"), [new FakeReader()], new FakeInventories());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ScanAsync(connection.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_token_does_not_persist_empty_snapshot()
    {
        var connection = NewConnection(ConnectionDirection.Source);
        var storage = new FakeInventories();
        var service = new DiscoveryService(new FakeConnections(connection),
            new FakeSecrets(connection.SecretReference, null), [new FakeReader()], storage);
        var error = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            service.ScanAsync(connection.Id, CancellationToken.None));
        Assert.Equal("CredentialMissing", error.Code);
        Assert.Empty(await storage.ListAsync(CancellationToken.None));
    }

    private static MigrationConnection NewConnection(ConnectionDirection direction) =>
        new(Guid.NewGuid(), "Source", "azure-devops-services", direction, "example-org", "connection-secret",
            DateTimeOffset.UtcNow, null, "Verified", null, "organization_projects_read");

    private sealed class FakeReader : IDiscoveryReader
    {
        public string ProviderKey => "azure-devops-services";
        public string? ReceivedPat { get; private set; }
        public Task<DiscoveryInventory> DiscoverAsync(Guid id, string org, string token, CancellationToken ct)
        {
            ReceivedPat = token;
            var snap = new DiscoverySnapshot {
                SnapshotId = Guid.NewGuid(), SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = DateTimeOffset.UtcNow, Projects = [], Identities = [], AccessGrants = []
            };
            return Task.FromResult(new DiscoveryInventory {
                Id = snap.SnapshotId, ConnectionId = id, SourceProvider = ProviderKey, Organization = org,
                CollectedAtUtc = snap.CollectedAt, Snapshot = snap, Refs = [], Issues = [],
                Completeness = DiscoveryCompleteness.Partial, IdentitiesComplete = false,
                PermissionsComplete = false
            });
        }
    }

    private sealed class FakeConnections(MigrationConnection connection) : IConnectionRepository
    {
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<MigrationConnection?>(id == connection.Id ? connection : null);
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MigrationConnection>>([connection]);
        public Task SaveAsync(MigrationConnection value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSecrets(string reference, string? value) : IConnectionSecretStore
    {
        public Task<string?> ReadAsync(string key, CancellationToken ct) => Task.FromResult(key == reference ? value : null);
        public Task SaveAsync(string key, string pat, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeInventories : IDiscoveryRepository
    {
        private readonly List<DiscoveryInventory> _items = [];
        public Task SaveAsync(DiscoveryInventory inventory, CancellationToken ct) { _items.Add(inventory); return Task.CompletedTask; }
        public Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_items.FirstOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveryInventory>>(_items);
    }
}
