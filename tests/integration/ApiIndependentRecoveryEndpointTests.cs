using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// No HTTP push/dispatcher route. These are explicitly invoked read-only
// recovery endpoints, disabled by default and admin-only.
public sealed class ApiIndependentRecoveryEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F087_both_recovery_routes_require_superadmin_and_are_disabled()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var root = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/github-app-scopes/" + Guid.NewGuid() +
            "/remote-sandbox-review-sessions/" + Guid.NewGuid() +
            "/dispatch-intents/" + Guid.NewGuid() +
            "/confirmations/" + Guid.NewGuid() +
            "/independent-recovery/" + Guid.NewGuid();
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var providerCalls = harness.Calls.Count;
        foreach (var suffix in new[] { "/observe-tips", "/verify-full-objects" })
        {
            using (var anonymous = await SendAsync(root + suffix, null))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var forbidden = await SendAsync(root + suffix, user))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var forbidden = await SendAsync(root + suffix, admin))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var disabled = await SendAsync(root + suffix, superadmin);
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            using var json = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("IndependentRecoveryDisabled",
                json.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain(ApiHarness.SourceToken,
                json.RootElement.GetRawText());
            Assert.DoesNotContain(ApiHarness.TargetToken,
                json.RootElement.GetRawText());
        }
        Assert.Equal(providerCalls, harness.Calls.Count);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        return await harness.Client.SendAsync(request);
    }
}
