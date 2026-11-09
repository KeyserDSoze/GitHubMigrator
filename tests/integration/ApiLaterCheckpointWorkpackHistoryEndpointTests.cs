using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// #111: the historical v2 workpack endpoint is GET-only, superadmin-only,
// and OFF by default. No remote provider GET, sender or bundle worker.
public sealed class ApiLaterCheckpointWorkpackHistoryEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F097c4c2_default_off_and_JWT_gate_never_contact_provider()
    {
        var reviewer = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var url = Root();
        var calls = harness.Calls.Count;

        foreach (var (token, expected) in new[] {
            ((string?)null, HttpStatusCode.Unauthorized),
            (user, HttpStatusCode.Forbidden),
            (admin, HttpStatusCode.Forbidden),
            (reviewer, HttpStatusCode.Conflict)
        })
        {
            using var reply = await Send(harness.Client, HttpMethod.Get,
                url, token);
            Assert.Equal(expected, reply.StatusCode);
            if (token == reviewer)
            {
                using var data = JsonDocument.Parse(
                    await reply.Content.ReadAsStringAsync());
                Assert.Equal("LaterCheckpointHistoryDisabled",
                    data.RootElement.GetProperty("error").GetString());
            }
        }
        using (var post = await Send(harness.Client, HttpMethod.Post,
            url, reviewer))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(calls, harness.Calls.Count);
    }

    [Fact]
    public async Task F097c4c2_enabled_read_refuses_invalid_scope_and_missing_history()
    {
        var enabled = new ApiHarness { EnableLaterCheckpointHistory = true };
        try
        {
            await enabled.InitializeAsync();
            var token = await enabled.LoginAsync(
                enabled.SuperadminName, ApiHarness.SuperadminPassword);
            var calls = enabled.Calls.Count;
            using (var invalid = await Send(enabled.Client, HttpMethod.Get,
                Root().Replace(new string('a', 64), "bad"), token))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Contains("LaterCheckpointHistoryInvalidRequest",
                    await invalid.Content.ReadAsStringAsync());
            }
            using (var missing = await Send(enabled.Client, HttpMethod.Get,
                Root(), token))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                Assert.Contains("LaterCheckpointHistoryNotFound",
                    await missing.Content.ReadAsStringAsync());
            }
            Assert.Equal(calls, enabled.Calls.Count);
        }
        finally { await enabled.DisposeAsync(); }
    }

    private static string Root() =>
        "/api/incremental-preparations/" + Guid.NewGuid() +
        "/operation-plans/" + Guid.NewGuid() +
        "/source-sync-checkpoints/" + Guid.NewGuid() +
        "/later-workpacks/" + Guid.NewGuid() +
        "/history?sourceRepositoryId=repo-1&targetObservationId=" +
        Guid.NewGuid() + "&expectedTargetReceiptSha256=" + new string('a', 64);

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string url, string? token)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
