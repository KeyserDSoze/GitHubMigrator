using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-085 exposes only role-gated, read-only recovery and body-free
// recording of an existing independent F-058 proof, never GitHub write.
public sealed class ApiRemotePostSendRecoveryEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F085_anonymous_user_admin_are_forbidden_and_superadmin_is_disabled()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var root = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/github-app-scopes/" + Guid.NewGuid() +
            "/remote-sandbox-review-sessions/" + Guid.NewGuid() +
            "/dispatch-intents/" + Guid.NewGuid() +
            "/confirmations/" + Guid.NewGuid() +
            "/post-send-recovery/" + Guid.NewGuid();
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var callsBefore = harness.Calls.Count;

        foreach (var (url, method) in new[] {
            (root, HttpMethod.Get),
            (root + "/closure-checkpoints", HttpMethod.Post)
        })
        {
            using (var anonymous = await SendAsync(method, url, null))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var forbidden = await SendAsync(method, url, user))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using (var forbidden = await SendAsync(method, url, admin))
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var disabled = await SendAsync(method, url, superadmin);
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            using var json = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("PostSendRecoveryDisabled",
                json.RootElement.GetProperty("error").GetString());
            var body = json.RootElement.GetRawText();
            Assert.DoesNotContain(ApiHarness.SourceToken, body);
            Assert.DoesNotContain(ApiHarness.TargetToken, body);
        }
        Assert.Equal(callsBefore, harness.Calls.Count);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? bearer)
    {
        using var request = new HttpRequestMessage(method, url);
        if (bearer is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearer);
        return await harness.Client.SendAsync(request);
    }
}
