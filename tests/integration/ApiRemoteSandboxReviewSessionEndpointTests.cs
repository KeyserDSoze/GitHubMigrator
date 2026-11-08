using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Full ASP.NET TestServer with real PostgreSQL schema bootstrap and
// real JWT roles. Credentials and remote calls are never used when the
// test-only sandbox allowlist is disabled (the default configuration).
public sealed class ApiRemoteSandboxReviewSessionEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F079_readonly_session_routes_require_superadmin_and_fail_closed()
    {
        // This host must initialize F-055 before F-078 on a clean DB:
        // F-078 has a foreign key referencing F-055's hazard table.
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var prepId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var hazardId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var root = $"/api/incremental-preparations/{prepId}" +
            $"/operation-plans/{planId}/github-app-scopes/{scopeId}" +
            "/remote-sandbox-review-sessions";
        var payload = new { hazardId };
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var initialCalls = harness.Calls.Count;

        using (var anonymous = await SendAsync(
            HttpMethod.Post, root, null, payload))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var forbidden = await SendAsync(
            HttpMethod.Post, root, user, payload))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var forbidden = await SendAsync(
            HttpMethod.Post, root, admin, payload))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var disabled = await SendAsync(
            HttpMethod.Post, root, superadmin, payload))
            await AssertDisabledAsync(disabled);

        using (var anonymous = await SendAsync(
            HttpMethod.Get, root + "/" + sessionId, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var forbidden = await SendAsync(
            HttpMethod.Get, root + "/" + sessionId, user))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var disabled = await SendAsync(
            HttpMethod.Get, root + "/" + sessionId, superadmin))
            await AssertDisabledAsync(disabled);

        // In particular the superadmin POST never minted a credential,
        // retrieved remote refs or attempted a network transport.
        Assert.Equal(initialCalls, harness.Calls.Count);
    }

    private static async Task AssertDisabledAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal("SandboxReviewSessionDisabled",
            json.RootElement.GetProperty("error").GetString());
        var content = json.RootElement.GetRawText();
        Assert.DoesNotContain(ApiHarness.TargetToken, content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(ApiHarness.SourceToken, content,
            StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? bearer,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await harness.Client.SendAsync(request);
    }
}
