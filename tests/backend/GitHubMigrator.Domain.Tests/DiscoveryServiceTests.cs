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

    [Fact]
    public async Task Stable_job_snapshot_id_reuses_committed_scan_without_provider_calls()
    {
        var conn = NewConnection(ConnectionDirection.Source);
        var reader = new FakeReader();
        var secrets = new FakeSecrets(conn.SecretReference, "private-token");
        var store = new FakeInventories();
        var service = new DiscoveryService(new FakeConnections(conn), secrets, [reader], store);
        var id = Guid.NewGuid();
        var first = await service.ScanAsync(conn.Id, CancellationToken.None, id);
        var replay = await service.ScanAsync(conn.Id, CancellationToken.None, id);
        Assert.Equal(id, first.Id);
        Assert.Equal(id, first.Snapshot.SnapshotId);
        Assert.Equal(id, replay.Id);
        Assert.Equal(1, reader.Calls);
        Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.DoesNotContain("private-token", System.Text.Json.JsonSerializer.Serialize(replay));
    }

    [Fact]
    public async Task Stable_snapshot_owned_by_a_different_connection_never_gets_reused()
    {
        var connection = NewConnection(ConnectionDirection.Source);
        var reader = new FakeReader();
        var stored = new FakeInventories();
        var first = new DiscoveryService(new FakeConnections(connection),
            new FakeSecrets(connection.SecretReference, "secret"), [reader], stored);
        var id = Guid.NewGuid();
        await first.ScanAsync(connection.Id, CancellationToken.None, id);

        var wrong = new MigrationConnection(Guid.NewGuid(), connection.Name,
            connection.ProviderKey, ConnectionDirection.Source, connection.Organization,
            connection.SecretReference, connection.CreatedAtUtc, connection.ValidatedAtUtc,
            connection.ValidationCode, connection.AuthenticatedAs, connection.PermissionAssessment);
        var other = new DiscoveryService(new FakeConnections(wrong),
            new FakeSecrets(wrong.SecretReference, "secret"), [reader], stored);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            other.ScanAsync(wrong.Id, CancellationToken.None, id));
        Assert.Equal(1, reader.Calls);
    }

    private static MigrationConnection NewConnection(ConnectionDirection direction) =>
        new(Guid.NewGuid(), "Source", "azure-devops-services", direction, "example-org", "connection-secret",
            DateTimeOffset.UtcNow, null, "Verified", null, "organization_projects_read");

    private sealed class FakeReader : IDiscoveryReader
    {
        public string ProviderKey => "azure-devops-services";
        public string? ReceivedPat { get; private set; }
        public int Calls { get; private set; }
        public Task<DiscoveryInventory> DiscoverAsync(Guid id, string org, string token, CancellationToken ct)
        {
            Calls++;
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
