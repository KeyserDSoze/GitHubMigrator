using System.Net;
using System.Net.Http;
using System.Text;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubDestinationAccessReaderTests
{
    [Theory]
    [InlineData(true, "AllowedForMembers")]
    [InlineData(false, "RestrictedForMembers")]
    public async Task Active_membership_matching_saved_identity_reports_policy_without_claiming_write_rights(
        bool allowed, string expectedPolicy)
    {
        var calls = new List<string>();
        using var client = new HttpClient(new FakeHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("TEST-ONLY", req.Headers.Authorization?.Parameter);
            calls.Add(req.RequestUri.AbsolutePath);
            if (req.RequestUri.AbsolutePath == "/user/memberships/orgs/target-org")
                return Json("""{"state":"active","role":"member","user":{"login":"gh-operator"},"organization":{"login":"target-org"}}""");
            return Json("{\"login\":\"target-org\",\"members_can_create_private_repositories\":" +
                allowed.ToString().ToLowerInvariant() + "}");
        }));
        var evidence = await new GitHubDestinationAccessReader(client)
            .ReadAsync("target-org", "gh-operator", "TEST-ONLY", default);
        Assert.Equal("Active", evidence.MembershipStatus);
        Assert.Equal("Member", evidence.MembershipRole);
        Assert.Equal(expectedPolicy, evidence.PrivateRepositoryPolicy);
        Assert.Equal(["/user/memberships/orgs/target-org", "/orgs/target-org"], calls);
    }

    [Fact]
    public async Task Confirmed_owner_membership_still_cannot_prove_write_scope()
    {
        using var client = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsolutePath.StartsWith("/user/", StringComparison.Ordinal)
                ? Json("""{"state":"active","role":"admin","user":{"login":"admin-login"},"organization":{"login":"target-org"}}""")
                : Json("""{"login":"target-org"}""")));
        var e = await new GitHubDestinationAccessReader(client)
            .ReadAsync("target-org", "admin-login", "token", default);
        Assert.Equal("Active", e.MembershipStatus);
        Assert.Equal("Owner", e.MembershipRole);
        Assert.Equal("Unverified", e.PrivateRepositoryPolicy);
    }

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "NotConfirmed")]
    [InlineData(429, "RateLimited")]
    [InlineData(500, "Unavailable")]
    public async Task Unconfirmed_or_unavailable_membership_never_queries_org_policy(int status, string expected)
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage((HttpStatusCode)status);
        }));
        var e = await new GitHubDestinationAccessReader(client)
            .ReadAsync("target-org", "operator", "token", default);
        Assert.Equal(expected, e.MembershipStatus);
        Assert.Equal("Unknown", e.MembershipRole);
        Assert.Equal("Unverified", e.PrivateRepositoryPolicy);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("other-account", "target-org")]
    [InlineData("operator", "wrong-org")]
    public async Task Mismatched_login_or_organization_is_not_accepted(string login, string org)
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            requests++;
            return Json("{\"state\":\"active\",\"role\":\"admin\",\"user\":{\"login\":\"" +
                login + "\"},\"organization\":{\"login\":\"" + org + "\"}}");
        }));
        var e = await new GitHubDestinationAccessReader(client)
            .ReadAsync("target-org", "operator", "token", default);
        Assert.Equal("IdentityMismatch", e.MembershipStatus);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Pending_invitation_does_not_verify_active_membership()
    {
        using var client = new HttpClient(new FakeHandler(_ =>
            Json("""{"state":"pending","role":"member","user":{"login":"operator"},"organization":{"login":"target-org"}}""")));
        var e = await new GitHubDestinationAccessReader(client)
            .ReadAsync("target-org", "operator", "token", default);
        Assert.Equal("Pending", e.MembershipStatus);
        Assert.Equal("Unknown", e.MembershipRole);
    }

    [Fact]
    public async Task Unbounded_response_is_rejected_instead_of_trusted()
    {
        using var client = new HttpClient(new FakeHandler(_ =>
            Json(new string('a', 150_000))));
        var e = await new GitHubDestinationAccessReader(client)
            .ReadAsync("target-org", "operator", "token", default);
        Assert.Equal("InvalidResponse", e.MembershipStatus);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(reply(req));
    }
}
