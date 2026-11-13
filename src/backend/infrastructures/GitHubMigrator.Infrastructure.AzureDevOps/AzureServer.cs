using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

// Azure DevOps Server / TFS: PAT over Basic auth against {serverRoot}/{collection}.
// Every request re-validates the stored URL against the egress allow-list.
public sealed class AzureDevOpsServerConnectionValidator(HttpClient client, ServerEndpointPolicy policy)
    : IEndpointConnectionValidator
{
    public string ProviderKey => ProviderKeys.AzureDevOpsServer;
    public ConnectionDirection Direction => ConnectionDirection.Source;

    public Task<ConnectionValidation> ValidateAsync(string organization, string personalAccessToken, CancellationToken ct) =>
        Task.FromResult(new ConnectionValidation(false, "ServerUrlRequired", null, "not_verified"));

    public async Task<ConnectionValidation> ValidateAsync(string organization, string? baseUrl,
        string personalAccessToken, CancellationToken ct)
    {
        string serverRoot;
        try { serverRoot = policy.Normalize(baseUrl); }
        catch (ArgumentException) { return new(false, "ServerHostNotAllowed", null, "not_verified"); }

        var collection = AzureDevOpsEndpoints.Server(serverRoot, organization, "7.0").Root;
        try
        {
            await AzureDevOpsEndpoints.DetectServerApiVersionAsync(client, collection, personalAccessToken, ct);
            return new(true, "Verified", await TryReadAuthenticatedUserAsync(collection, personalAccessToken, ct),
                "organization_projects_read");
        }
        catch (DiscoveryProviderException ex)
        {
            return new(false, ex.Code, null, "not_verified");
        }
    }

    private async Task<string?> TryReadAuthenticatedUserAsync(string collection, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{collection}/_apis/connectionData");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return json.RootElement.TryGetProperty("authenticatedUser", out var user) &&
                user.TryGetProperty("providerDisplayName", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null; // cosmetic only
        }
    }
}

public sealed class AzureDevOpsServerDiscoveryReader(
    HttpClient client, TimeProvider clock, ServerEndpointPolicy policy,
    IDiscoveryPageCheckpointStore? checkpoints = null)
    : AzureDevOpsDiscoveryReader(client, clock, checkpoints)
{
    public override string ProviderKey => ProviderKeys.AzureDevOpsServer;
    protected override SourceSystemKind SourceSystem => SourceSystemKind.AzureDevOpsServer;

    protected override async Task<AzureDevOpsEndpoints> ResolveEndpointsAsync(
        string organization, string? baseUrl, string accessToken, CancellationToken ct)
    {
        string serverRoot;
        try { serverRoot = policy.Normalize(baseUrl); }
        catch (ArgumentException) { throw new DiscoveryProviderException("ServerHostNotAllowed"); }
        var collection = AzureDevOpsEndpoints.Server(serverRoot, organization, "7.0");
        var api = await AzureDevOpsEndpoints.DetectServerApiVersionAsync(Http, collection.Root, accessToken, ct);
        return AzureDevOpsEndpoints.Server(serverRoot, organization, api);
    }
}
