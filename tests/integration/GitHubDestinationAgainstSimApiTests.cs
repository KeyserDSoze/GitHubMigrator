using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The real API host (PostgreSQL, JWT, AES-GCM secrets, production DI) configured only through
// Providers:GitHub:* so that every GitHub reader talks real HTTP to the GitHub simulator.
public sealed class GitHubDestinationAgainstSimApiTests : IAsyncLifetime
{
    private readonly GhSimFixture _sim = new();
    private ApiHarness _api = null!;

    public async Task InitializeAsync()
    {
        await _sim.InitializeAsync();
        _api = new ApiHarness { GitHubSimOrigin = _sim.Origin };
        await _api.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _sim.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await _api.LoginAsync(_api.AdminName, ApiHarness.AdminPassword));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _api.Client.SendAsync(request);
    }

    [Fact]
    public async Task A_destination_connection_is_created_verified_and_checked_against_the_simulated_github()
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/connections", new
        {
            name = "Sim GitHub", providerKey = "github-enterprise-cloud", organization = "orion-org",
            personalAccessToken = GhSimFixture.Pat
        });
        var text = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(GhSimFixture.Pat, text);
        using var connection = JsonDocument.Parse(text);
        Assert.Equal("sim-admin", connection.RootElement.GetProperty("authenticatedAs").GetString());
        Assert.Equal("Verified", connection.RootElement.GetProperty("validationCode").GetString());

        using var rejected = await SendAsync(HttpMethod.Post, "/api/connections", new
        {
            name = "Bad token", providerKey = "github-enterprise-cloud", organization = "orion-org",
            personalAccessToken = "wrong-token"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using var unknownOrg = await SendAsync(HttpMethod.Post, "/api/connections", new
        {
            name = "No org", providerKey = "github-enterprise-cloud", organization = "missing-org",
            personalAccessToken = GhSimFixture.Pat
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownOrg.StatusCode);

        using var access = await SendAsync(HttpMethod.Get,
            $"/api/migration-plans/destinations/{_api.DestinationConnectionId}/access");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        using var evidence = JsonDocument.Parse(await access.Content.ReadAsStringAsync());
        Assert.Equal("orion-org", evidence.RootElement.GetProperty("targetOrganization").GetString());
        Assert.True(evidence.RootElement.GetProperty("membershipVerified").GetBoolean());
        Assert.Equal("Owner", evidence.RootElement.GetProperty("membershipRole").GetString());
        Assert.Equal("AllowedForMembers", evidence.RootElement.GetProperty("privateRepositoryPolicy").GetString());
    }

    [Fact]
    public async Task Verification_and_access_checks_only_ever_read_from_the_destination()
    {
        await _sim.SendAsync("DELETE", "/_sim/requests");
        using var created = await SendAsync(HttpMethod.Post, "/api/connections", new
        {
            name = "Sim GitHub 2", providerKey = "github-enterprise-cloud", organization = "orion-org",
            personalAccessToken = GhSimFixture.Pat
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var access = await SendAsync(HttpMethod.Get,
            $"/api/migration-plans/destinations/{_api.DestinationConnectionId}/access");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);

        var log = (await _sim.SendAsync("GET", "/_sim/requests?limit=500")).EnumerateArray()
            .Where(r => !r.GetProperty("Path").GetString()!.StartsWith("/_sim")).ToArray();
        Assert.NotEmpty(log);
        Assert.All(log, r =>
        {
            Assert.Equal("GET", r.GetProperty("Method").GetString());
            Assert.Equal("sim-admin", r.GetProperty("Principal").GetString());
        });
    }
}
