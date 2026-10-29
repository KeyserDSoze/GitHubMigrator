using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

public sealed class DiscoveryProviderException(string code) : Exception("Source inventory discovery failed")
{
    public string Code { get; } = code;
}

public interface IDiscoveryReader
{
    string ProviderKey { get; }
    Task<DiscoveryInventory> DiscoverAsync(Guid connectionId, string organization, string accessToken, CancellationToken ct);
}

public interface IDiscoveryRepository
{
    Task SaveAsync(DiscoveryInventory inventory, CancellationToken ct);
    Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct);
}

public sealed class DiscoveryService(
    IConnectionRepository connections,
    IConnectionSecretStore credentials,
    IEnumerable<IDiscoveryReader> readers,
    IDiscoveryRepository repository)
{
    private readonly IReadOnlyDictionary<string, IDiscoveryReader> _readers =
        readers.ToDictionary(r => r.ProviderKey, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<DiscoveryInventory>> ListAsync(CancellationToken ct) => repository.ListAsync(ct);
    public Task<DiscoveryInventory?> GetAsync(Guid id, CancellationToken ct) => repository.GetAsync(id, ct);

    public async Task<DiscoveryInventory> ScanAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await connections.GetAsync(connectionId, ct)
            ?? throw new KeyNotFoundException("Connection not found");
        if (connection.Direction != ConnectionDirection.Source || !_readers.TryGetValue(connection.ProviderKey, out var reader))
            throw new ArgumentException("Connection is not a supported source.");
        var token = await credentials.ReadAsync(connection.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token)) throw new DiscoveryProviderException("CredentialMissing");
        // Reader can only access this transient token during the operation, never through the persisted inventory.
        var inventory = await reader.DiscoverAsync(connection.Id, connection.Organization, token, ct);
        if (inventory.ConnectionId != connection.Id || inventory.SourceProvider != connection.ProviderKey ||
            inventory.Organization != connection.Organization)
            throw new InvalidOperationException("Source adapter returned an inventory for another connection.");
        await repository.SaveAsync(inventory, ct);
        return inventory;
    }
}
