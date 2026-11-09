using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Auth, default-off boundaries and provider noninterference using the
// real API/JWT/roles/PostgreSQL host. This test never contacts GitHub.
public sealed class ApiCheckpointTargetHumanReviewEndpointTests(
    ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F097c4c0c_is_superadmin_only_off_by_default_and_never_uses_provider()
    {
        var root = "/api/incremental-preparations/" + Guid.NewGuid() +
            "/operation-plans/" + Guid.NewGuid() +
            "/source-sync-checkpoints/" + Guid.NewGuid() +
            "/target-human-review";
        var query = "?sourceRepositoryId=repo-1";
        var user = await harness.LoginAsync(
            harness.UserName, ApiHarness.UserPassword);
        var admin = await harness.LoginAsync(
            harness.AdminName, ApiHarness.AdminPassword);
        var superadmin = await harness.LoginAsync(
            harness.SuperadminName, ApiHarness.SuperadminPassword);
        var before = harness.Calls.Count;

        foreach (var (method, path) in new[] {
            (HttpMethod.Get, root + "/candidate" + query),
            (HttpMethod.Get, root + query),
            (HttpMethod.Post, root)
        })
        {
            using (var anonymous = await Send(method, path, null))
                Assert.Equal(HttpStatusCode.Unauthorized,
                    anonymous.StatusCode);
            using (var ordinary = await Send(method, path, user))
                Assert.Equal(HttpStatusCode.Forbidden,
                    ordinary.StatusCode);
            using (var nonReviewerAdmin = await Send(method, path, admin))
                Assert.Equal(HttpStatusCode.Forbidden,
                    nonReviewerAdmin.StatusCode);
            using var disabled = await Send(method, path, superadmin);
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            using var payload = JsonDocument.Parse(
                await disabled.Content.ReadAsStringAsync());
            Assert.Equal("CheckpointHumanReviewDisabled",
                payload.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain(ApiHarness.SourceToken, payload.RootElement.GetRawText());
            Assert.DoesNotContain(ApiHarness.TargetToken, payload.RootElement.GetRawText());
        }

        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task F097c4c0c_opt_in_is_read_only_and_fails_closed_without_verified_epoch()
    {
        // A separate explicitly enabled host proves wiring and request
        // validation; an unverified checkpoint must never reach a provider.
        var optIn = new ApiHarness {
            EnableCheckpointHumanReview = true
        };
        try
        {
            await optIn.InitializeAsync();
            var token = await optIn.LoginAsync(
                optIn.SuperadminName, ApiHarness.SuperadminPassword);
            var root = "/api/incremental-preparations/" + Guid.NewGuid() +
                "/operation-plans/" + Guid.NewGuid() +
                "/source-sync-checkpoints/" + Guid.NewGuid() +
                "/target-human-review";
            var before = optIn.Calls.Count;
            using (var candidate = await CallOptIn(HttpMethod.Get,
                root + "/candidate?sourceRepositoryId=repo-1", null))
            {
                Assert.Equal(HttpStatusCode.Conflict, candidate.StatusCode);
                Assert.Contains("CheckpointHumanReviewCandidateUnverified",
                    await candidate.Content.ReadAsStringAsync());
            }
            using (var absent = await CallOptIn(HttpMethod.Get,
                root + "?sourceRepositoryId=repo-1", null))
            {
                Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            }
            using (var invalid = await CallOptIn(HttpMethod.Post, root,
                new {
                    sourceRepositoryId = "repo-1",
                    decision = "ApprovedForObservation",
                    expectedCandidateEvidenceSha256 = "not-a-sha",
                    reviewedBy = Guid.NewGuid(),
                    requestedBy = Guid.NewGuid()
                }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Contains("CheckpointHumanReviewInvalidRequest",
                    await invalid.Content.ReadAsStringAsync());
            }
            using (var missing = await CallOptIn(HttpMethod.Post, root,
                new {
                    sourceRepositoryId = "repo-1",
                    decision = "Rejected",
                    expectedCandidateEvidenceSha256 = new string('a', 64),
                    reviewedBy = Guid.NewGuid(),
                    requestedBy = Guid.NewGuid()
                }))
            {
                Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
                Assert.Contains("CheckpointHumanReviewCandidateUnverified",
                    await missing.Content.ReadAsStringAsync());
            }
            Assert.Equal(before, optIn.Calls.Count);

            async Task<HttpResponseMessage> CallOptIn(
                HttpMethod method, string path, object? body)
            {
                using var req = new HttpRequestMessage(method, path);
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
                if (method == HttpMethod.Post)
                    req.Content = JsonContent.Create(body);
                return await optIn.Client.SendAsync(req);
            }
        }
        finally { await optIn.DisposeAsync(); }
    }

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post)
            request.Content = JsonContent.Create(new {
                sourceRepositoryId = "repo-1",
                decision = "ApprovedForObservation",
                expectedCandidateEvidenceSha256 = new string('a', 64),
                // Unknown spoofed identity is ignored; API uses claims.
                reviewedBy = Guid.NewGuid(),
                requestedBy = Guid.NewGuid()
            });
        return await harness.Client.SendAsync(request);
    }
}
