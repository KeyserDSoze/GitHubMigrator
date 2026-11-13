using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real JWT/roles and PostgreSQL host, but no permitted GitHub transport.
public sealed class ApiCheckpointTargetReadPreflightEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F097c4c0d_is_superadmin_only_and_off_by_default()
    {
        var url = Route() + Query(new string('a', 64), new string('b', 64));
        var before = harness.Calls.Count;
        var ordinary = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var reviewer = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        using (var response = await Get(harness.Client, url, null))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await Get(harness.Client, url, ordinary))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await Get(harness.Client, url, admin))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await Get(harness.Client, url, reviewer))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("CheckpointTargetReadPreflightDisabled",
                await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F097c4c0d_enabled_rejects_bad_digest_and_unverified_epoch_without_network()
    {
        var enabled = new ApiHarness { EnableCheckpointTargetReadPreflight = true };
        try
        {
            await enabled.InitializeAsync();
            var reviewer = await enabled.LoginAsync(
                enabled.SuperadminName, ApiHarness.SuperadminPassword);
            var before = enabled.Calls.Count;
            var route = Route();
            using (var invalid = await Get(enabled.Client,
                route + Query("not-a-sha256", new string('b', 64)), reviewer))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Contains("CheckpointTargetReadPreflightInvalidRequest",
                    await invalid.Content.ReadAsStringAsync());
            }
            using (var missing = await Get(enabled.Client,
                route + Query(new string('a', 64), new string('b', 64)), reviewer))
            {
                Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
                using var json = JsonDocument.Parse(
                    await missing.Content.ReadAsStringAsync());
                Assert.Equal("CheckpointTargetReadPreflightSourceUnverified",
                    json.RootElement.GetProperty("error").GetString());
                Assert.DoesNotContain(ApiHarness.SourceToken,
                    json.RootElement.GetRawText());
                Assert.DoesNotContain(ApiHarness.TargetToken,
                    json.RootElement.GetRawText());
            }
            Assert.Equal(before, enabled.Calls.Count);
        }
        finally { await enabled.DisposeAsync(); }
    }

    private static string Route() =>
        "/api/incremental-preparations/" + Guid.NewGuid() +
        "/operation-plans/" + Guid.NewGuid() +
        "/source-sync-checkpoints/" + Guid.NewGuid() +
        "/target-read-preflight";

    private static string Query(string candidateSha, string humanSha) =>
        "?sourceRepositoryId=repo-1" +
        "&expectedCandidateEvidenceSha256=" + candidateSha +
        "&expectedHumanReviewEvidenceSha256=" + humanSha;

    private static async Task<HttpResponseMessage> Get(
        HttpClient client, string url, string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", token);
        return await client.SendAsync(req);
    }
}
