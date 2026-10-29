using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Infrastructure.GitHub;

public sealed class GitHubConnectionValidator(HttpClient client) : IConnectionValidator
{
    public string ProviderKey => "github-enterprise-cloud";
    public ConnectionDirection Direction => ConnectionDirection.Destination;

    public async Task<ConnectionValidation> ValidateAsync(string organization, string personalAccessToken, CancellationToken ct)
    {
        try
        {
            using var identity = await SendGetAsync("https://api.github.com/user", personalAccessToken, ct);
            if (identity.StatusCode == HttpStatusCode.Unauthorized)
                return new(false, "Unauthorized", null, "not_verified");
            if (identity.StatusCode == HttpStatusCode.Forbidden)
                return new(false, "InsufficientScope", null, "not_verified");
            if (!identity.IsSuccessStatusCode)
                return new(false, "ProviderUnavailable", null, "not_verified");

            await using var body = await identity.Content.ReadAsStreamAsync(ct);
            using var user = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            if (!user.RootElement.TryGetProperty("login", out var login) ||
                login.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(login.GetString()))
                return new(false, "UnexpectedResponse", null, "not_verified");

            using var org = await SendGetAsync($"https://api.github.com/orgs/{Uri.EscapeDataString(organization)}", personalAccessToken, ct);
            if (org.StatusCode == HttpStatusCode.NotFound)
                return new(false, "OrganizationNotFound", null, "not_verified");
            if (org.StatusCode == HttpStatusCode.Forbidden)
                return new(false, "InsufficientScope", null, "not_verified");
            if (!org.IsSuccessStatusCode)
                return new(false, "ProviderUnavailable", null, "not_verified");

            // The public organization endpoint DOES NOT demonstrate permission to create repositories.
            return new(true, "Verified", login.GetString(), "organization_write_unverified");
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

    private async Task<HttpResponseMessage> SendGetAsync(string address, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
        {
            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        }
        return response;
    }
}
