using System.Net;
using System.Net.Http;
using System.Text;
using GitHubMigrator.Infrastructure.AzureDevOps;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class ProviderValidationTests
{
    [Fact]
    public async Task AzureDevOps_probes_fixed_host_and_project_read_permission()
    {
        var handler = new StubHandler(req =>
        {
            Assert.Equal("dev.azure.com", req.RequestUri!.Host);
            Assert.Equal("/example-org/_apis/projects", req.RequestUri.AbsolutePath);
            Assert.Contains("api-version=7.1", req.RequestUri.Query);
            Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(":secret-123")),
                req.Headers.Authorization.Parameter);
            Assert.DoesNotContain("secret-123", req.RequestUri.ToString());
            return Json("""{"count":0,"value":[]}""");
        });
        var result = await new AzureDevOpsConnectionValidator(new HttpClient(handler))
            .ValidateAsync("example-org", "secret-123", CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal("organization_projects_read", result.PermissionAssessment);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AzureDevOps_denies_pat_without_project_access()
    {
        var result = await new AzureDevOpsConnectionValidator(new HttpClient(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))))
            .ValidateAsync("org", "denied", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Equal("InsufficientScope", result.Code);
    }

    [Fact]
    public async Task Github_validates_identity_and_org_but_does_not_claim_write_access()
    {
        var handler = new StubHandler(req =>
        {
            Assert.Equal("api.github.com", req.RequestUri!.Host);
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            Assert.Equal("github-secret", req.Headers.Authorization.Parameter);
            return req.RequestUri.AbsolutePath switch
            {
                "/user" => Json("""{"login":"octocat"}"""),
                "/orgs/example-org" => Json("""{"login":"example-org"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var result = await new GitHubConnectionValidator(new HttpClient(handler))
            .ValidateAsync("example-org", "github-secret", CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal("octocat", result.AuthenticatedAs);
        Assert.Equal("organization_write_unverified", result.PermissionAssessment);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Github_does_not_accept_unknown_organization()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath == "/user"
            ? Json("""{"login":"user"}""") : new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await new GitHubConnectionValidator(new HttpClient(handler))
            .ValidateAsync("missing-org", "github-secret", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Equal("OrganizationNotFound", result.Code);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }
}
