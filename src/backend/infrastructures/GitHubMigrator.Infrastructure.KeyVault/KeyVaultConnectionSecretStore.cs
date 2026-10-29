using Azure;
using Azure.Security.KeyVault.Secrets;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.KeyVault;

public sealed class KeyVaultConnectionSecretStore(SecretClient client) : IConnectionSecretStore
{
    public async Task SaveAsync(string reference, string personalAccessToken, CancellationToken ct) =>
        await client.SetSecretAsync(new KeyVaultSecret(reference, personalAccessToken), ct);

    public async Task<string?> ReadAsync(string reference, CancellationToken ct)
    {
        try
        {
            var response = await client.GetSecretAsync(reference, cancellationToken: ct);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string reference, CancellationToken ct)
    {
        try { await client.StartDeleteSecretAsync(reference, ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { /* idempotent */ }
    }
}
