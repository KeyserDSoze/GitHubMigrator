using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// #109 real HTTP/JWT boundaries; no target credential/provider GET can
// occur without an exact approved, current F-014/F-091/F-097b epoch.
public sealed class ApiCheckpointTargetObservationEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F097c4c0d_disabled_transport_is_401_403_superadmin_409_and_no_provider()
    {
        var url = Root();
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var reviewer = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var before = harness.Calls.Count;
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            foreach (var (token, expected) in new[] {
                ((string?)null, HttpStatusCode.Unauthorized),
                (user, HttpStatusCode.Forbidden),
                (admin, HttpStatusCode.Forbidden),
                (reviewer, HttpStatusCode.Conflict)
            })
            {
                using var response = await Send(harness.Client,
                    method, url, token);
                Assert.Equal(expected, response.StatusCode);
                if (token == reviewer)
                {
                    Assert.Contains("CheckpointTargetObservationDisabled",
                        await response.Content.ReadAsStringAsync());
                }
            }
        }
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F097c4c0d_enabled_never_reads_provider_for_malformed_or_unverified_source()
    {
        var enabled = new ApiHarness { EnableCheckpointTargetObservation = true };
        try
        {
            await enabled.InitializeAsync();
            var reviewer = await enabled.LoginAsync(
                enabled.SuperadminName, ApiHarness.SuperadminPassword);
            var url = Root();
            var before = enabled.Calls.Count;
            using (var invalid = await Send(enabled.Client, HttpMethod.Post,
                url, reviewer, "not-a-sha"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Contains("CheckpointTargetObservationInvalidRequest",
                    await invalid.Content.ReadAsStringAsync());
            }
            using (var missing = await Send(enabled.Client, HttpMethod.Post,
                url, reviewer))
            {
                Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
                using var json = JsonDocument.Parse(
                    await missing.Content.ReadAsStringAsync());
                Assert.Equal("CheckpointTargetObservationPreflightUnverified",
                    json.RootElement.GetProperty("error").GetString());
                Assert.DoesNotContain(ApiHarness.TargetToken,
                    json.RootElement.GetRawText());
            }
            using (var absent = await Send(enabled.Client, HttpMethod.Get,
                url + "?sourceRepositoryId=repo-1", reviewer))
            {
                Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            }
            Assert.Equal(before, enabled.Calls.Count);
        }
        finally { await enabled.DisposeAsync(); }
    }

    private static string Root() =>
        "/api/incremental-preparations/" + Guid.NewGuid() +
        "/operation-plans/" + Guid.NewGuid() +
        "/source-sync-checkpoints/" + Guid.NewGuid() +
        "/target-ref-observations";

    private static async Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string url, string? token,
        string? candidate = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post)
            request.Content = JsonContent.Create(new {
                sourceRepositoryId = "repo-1",
                expectedCandidateEvidenceSha256 =
                    candidate ?? new string('a', 64),
                expectedHumanReviewEvidenceSha256 = new string('b', 64),
                reviewedBy = Guid.NewGuid(),
                targetUrl = "https://other.invalid/do-not-call",
                token = ApiHarness.TargetToken
            });
        return await client.SendAsync(request);
    }
}
