using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real API and JWT role matrix. Default configuration must refuse
// F-081 without any GitHub App network call or write-side capability.
public sealed class ApiRemoteSandboxDispatchConfirmationEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F081_anonymous_users_and_admins_cannot_confirm_and_superadmin_is_disabled_by_default()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var root = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/github-app-scopes/" + Guid.NewGuid() +
            "/remote-sandbox-review-sessions/" + Guid.NewGuid() +
            "/dispatch-intents/" + Guid.NewGuid() + "/confirmations";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var providerCalls = harness.Calls.Count;
        foreach (var url in new[] { root, root + "/" + Guid.NewGuid() })
        {
            var method = url == root ? HttpMethod.Post : HttpMethod.Get;
            using (var anonymous = await SendAsync(method, url, null))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var forbidden = await SendAsync(method, url, user))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var forbidden = await SendAsync(method, url, admin))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var disabled = await SendAsync(method, url, superadmin))
            {
                Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
                using var json = JsonDocument.Parse(
                    await disabled.Content.ReadAsStringAsync());
                Assert.Equal("SandboxDispatchConfirmationDisabled",
                    json.RootElement.GetProperty("error").GetString());
                var body = json.RootElement.GetRawText();
                Assert.DoesNotContain(ApiHarness.SourceToken,
                    body, StringComparison.Ordinal);
                Assert.DoesNotContain(ApiHarness.TargetToken,
                    body, StringComparison.Ordinal);
            }
        }
        Assert.Equal(providerCalls, harness.Calls.Count);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? bearer)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearer);
        return await harness.Client.SendAsync(request);
    }
}
