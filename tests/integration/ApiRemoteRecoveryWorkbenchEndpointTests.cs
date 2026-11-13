using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-088: monitoring is strictly superadmin-only and permanently GET-only.
// The operational page can read historical SHA evidence but not write GitHub.
public sealed class ApiRemoteRecoveryWorkbenchEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F088_unauthorized_roles_rejected_and_superadmin_disabled_by_default()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var path = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/remote-recovery-workbench?take=12";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var providerCalls = harness.Calls.Count;
        using (var anonymous = await SendAsync(path, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var forbidden = await SendAsync(path, user))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var forbidden = await SendAsync(path, admin))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var disabled = await SendAsync(path, superadmin))
        {
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            using var json = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("RecoveryWorkbenchDisabled",
                json.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain(ApiHarness.SourceToken,
                json.RootElement.GetRawText());
            Assert.DoesNotContain(ApiHarness.TargetToken,
                json.RootElement.GetRawText());
        }
        Assert.Equal(providerCalls, harness.Calls.Count);
    }

    [Fact]
    public async Task F088_no_write_route_exists_for_readonly_workbench()
    {
        var token = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var url = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/remote-recovery-workbench";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearer);
        return await harness.Client.SendAsync(request);
    }
}
