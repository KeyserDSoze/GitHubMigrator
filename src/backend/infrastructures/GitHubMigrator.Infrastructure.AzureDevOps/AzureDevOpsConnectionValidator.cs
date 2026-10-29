using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

public sealed class AzureDevOpsConnectionValidator(HttpClient client) : IConnectionValidator
{
    public string ProviderKey => "azure-devops-services";
    public ConnectionDirection Direction => ConnectionDirection.Source;

    public async Task<ConnectionValidation> ValidateAsync(string organization, string personalAccessToken, CancellationToken ct)
    {
        var endpoint = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/_apis/projects?$top=1&api-version=7.1");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + personalAccessToken)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(false, "Unauthorized", null, "not_verified");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new(false, "InsufficientScope", null, "not_verified");
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                return new(false, "UnexpectedRedirect", null, "not_verified");
            if (!response.IsSuccessStatusCode)
                return new(false, "ProviderUnavailable", null, "not_verified");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            if (!json.RootElement.TryGetProperty("value", out var projects) || projects.ValueKind != JsonValueKind.Array)
                return new(false, "UnexpectedResponse", null, "not_verified");
            return new(true, "Verified", null, "organization_projects_read");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "ProviderTimeout", null, "not_verified");
        }
        catch (HttpRequestException)
        {
            return new(false, "ProviderUnavailable", null, "not_verified");
        }
        catch (JsonException)
        {
            return new(false, "UnexpectedResponse", null, "not_verified");
        }
    }
}
