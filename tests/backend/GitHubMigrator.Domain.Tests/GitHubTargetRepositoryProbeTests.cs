using System.Net;
using System.Net.Http;
using System.Text;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubTargetRepositoryProbeTests
{
    [Theory]
    [InlineData(200, "Exists")]
    [InlineData(404, "NotVisibleOrMissing")]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(429, "RateLimited")]
    [InlineData(500, "Unavailable")]
    [InlineData(302, "Unavailable")]
    public async Task Fixed_GitHub_GET_probe_classifies_provider_response_without_claiming_404_availability(
        int httpStatus, string expected)
    {
        var handler = new FakeHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            Assert.Equal("/repos/enterprise-org/target-repo", req.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", req.Headers.Authorization?.Parameter);
            return new HttpResponseMessage((HttpStatusCode)httpStatus)
            {
                Content = new StringContent("""{"full_name":"enterprise-org/target-repo"}""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        var probe = new GitHubTargetRepositoryProbe(client);
        Assert.Equal(expected, await probe.ProbeAsync("enterprise-org", "target-repo",
            "test-token", CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_repository_or_invalid_JSON_must_not_prove_a_collision()
    {
        using var client = new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"full_name":"another-org/private"}""")
            }));
        var result = await new GitHubTargetRepositoryProbe(client)
            .ProbeAsync("enterprise-org", "target-repo", "pat", default);
        Assert.Equal("InvalidResponse", result);
    }

    [Fact]
    public async Task Response_body_over_limit_is_rejected_without_parsing()
    {
        using var client = new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('A', 150_000))
            }));
        Assert.Equal("InvalidResponse", await new GitHubTargetRepositoryProbe(client)
            .ProbeAsync("enterprise-org", "target-repo", "pat", default));
    }

    [Fact]
    public async Task GitHub_403_when_rate_limit_exhausted_is_not_reported_as_generic_forbidden()
    {
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        }));
        Assert.Equal("RateLimited", await new GitHubTargetRepositoryProbe(client)
            .ProbeAsync("enterprise-org", "target-repo", "pat", default));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(reply(req));
    }
}
