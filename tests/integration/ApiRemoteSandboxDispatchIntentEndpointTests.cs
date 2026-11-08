using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Production API, actual JWT and PostgreSQL bootstrap; the new route
// is not a sender and must fail closed with all sandbox flags disabled.
public sealed class ApiRemoteSandboxDispatchIntentEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F080_both_methods_are_superadmin_only_default_off_and_offline()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var root = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/github-app-scopes/" + Guid.NewGuid() +
            "/remote-sandbox-review-sessions/" + Guid.NewGuid() +
            "/dispatch-intents";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var calls = harness.Calls.Count;
        foreach (var path in new[] { root, root + "/" + Guid.NewGuid() })
        {
            var method = path == root ? HttpMethod.Post : HttpMethod.Get;
            using (var anonymous = await SendAsync(method, path, null))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var forbidden = await SendAsync(method, path, user))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var forbidden = await SendAsync(method, path, admin))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var disabled = await SendAsync(method, path, superadmin))
            {
                Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
                using var json = JsonDocument.Parse(
                    await disabled.Content.ReadAsStringAsync());
                Assert.Equal("SandboxDispatchIntentDisabled",
                    json.RootElement.GetProperty("error").GetString());
                var body = json.RootElement.GetRawText();
                Assert.DoesNotContain(ApiHarness.SourceToken, body,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(ApiHarness.TargetToken, body,
                    StringComparison.Ordinal);
            }
        }
        Assert.Equal(calls, harness.Calls.Count);
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
