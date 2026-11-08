using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-089 source pulse is a read-only, superadmin-only projection. Neither
// it nor its optional sandbox F-088 view can run GitHub transports.
public sealed class ApiMultiRepositorySyncPulseEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F089_source_pulse_is_role_gated_disabled_and_no_provider_is_called()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var path = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/repository-sync-pulse";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var providerBefore = harness.Calls.Count;
        using (var anon = await GetAsync(path, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        using (var forbidden = await GetAsync(path, user))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var forbidden = await GetAsync(path, admin))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var disabled = await GetAsync(path, superadmin))
        {
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            using var json = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("MultiRepositoryPulseDisabled",
                json.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain(ApiHarness.SourceToken,
                json.RootElement.GetRawText());
            Assert.DoesNotContain(ApiHarness.TargetToken,
                json.RootElement.GetRawText());
        }
        Assert.Equal(providerBefore, harness.Calls.Count);
    }

    [Fact]
    public async Task F089_no_GitHub_write_or_job_creation_is_exposed_by_pulse_endpoint()
    {
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var path = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/repository-sync-pulse";
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", superadmin);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetAsync(
        string path, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearer);
        return await harness.Client.SendAsync(request);
    }
}
