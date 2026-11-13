using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// API TestServer: F-086 has no capture or push endpoint; status is
// superadmin-only, disabled by default and never returns raw typed refs.
public sealed class ApiRemoteTypedRecoveryBaselineEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F086_no_role_other_than_superadmin_can_inspect_and_default_is_disabled()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var url = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/github-app-scopes/" + Guid.NewGuid() +
            "/remote-sandbox-review-sessions/" + Guid.NewGuid() +
            "/dispatch-intents/" + Guid.NewGuid() +
            "/confirmations/" + Guid.NewGuid() +
            "/typed-recovery-baseline";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var providerCalls = harness.Calls.Count;
        using (var anonymous = await SendAsync(url, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var forbidden = await SendAsync(url, user))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var forbidden = await SendAsync(url, admin))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var disabled = await SendAsync(url, superadmin))
        {
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            using var json = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("TypedRecoveryBaselineDisabled",
                json.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain(ApiHarness.SourceToken,
                json.RootElement.GetRawText());
            Assert.DoesNotContain(ApiHarness.TargetToken,
                json.RootElement.GetRawText());
        }
        Assert.Equal(providerCalls, harness.Calls.Count);
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
