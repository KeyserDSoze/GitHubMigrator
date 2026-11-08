using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-090 API is disabled by default, scoped to one already-reviewed plan,
// and must never call an external provider or GitHub sender.
public sealed class ApiSourceSyncCheckpointEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F090_read_and_write_require_superadmin_and_explicit_enablement()
    {
        using (var health = await harness.Client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var root = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/source-sync-checkpoints";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var before = harness.Calls.Count;
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            var path = method == HttpMethod.Get
                ? root : root + "/" + Guid.NewGuid();
            using (var unauthorized = await CallAsync(
                method, path, null))
                Assert.Equal(HttpStatusCode.Unauthorized,
                    unauthorized.StatusCode);
            using (var denied = await CallAsync(method, path, user))
                Assert.Equal(HttpStatusCode.Forbidden,
                    denied.StatusCode);
            using (var denied = await CallAsync(method, path, admin))
                Assert.Equal(HttpStatusCode.Forbidden,
                    denied.StatusCode);
            using var disabled = await CallAsync(
                method, path, superadmin);
            Assert.Equal(HttpStatusCode.Conflict,
                disabled.StatusCode);
            using var json = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("SourceSyncCheckpointDisabled",
                json.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain(ApiHarness.SourceToken,
                json.RootElement.GetRawText());
            Assert.DoesNotContain(ApiHarness.TargetToken,
                json.RootElement.GetRawText());
        }
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F092_ref_change_pages_are_superadmin_only_and_default_disabled()
    {
        var root = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/source-sync-checkpoints/" + Guid.NewGuid() + "/ref-changes";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var before = harness.Calls.Count;
        using (var denied = await CallAsync(HttpMethod.Get, root, null))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await CallAsync(HttpMethod.Get, root, user))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await CallAsync(HttpMethod.Get, root, admin))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var disabled = await CallAsync(
            HttpMethod.Get, root + "?take=2", superadmin))
        {
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            using var json = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("SourceSyncCheckpointDisabled",
                json.RootElement.GetProperty("error").GetString());
        }
        Assert.Equal(before, harness.Calls.Count);
    }

    private async Task<HttpResponseMessage> CallAsync(
        HttpMethod method, string path, string? bearer)
    {
        using var req = new HttpRequestMessage(method, path);
        if (bearer is not null)
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearer);
        if (method == HttpMethod.Post)
            req.Content = JsonContent.Create(new {
                expectedManifestSha256 = new string('a', 64),
                expectedPreviousCheckpointId = (Guid?)null
            });
        return await harness.Client.SendAsync(req);
    }
}
