using System.Net;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class GitHubEndpointsTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void Default_endpoints_are_github_com()
    {
        Assert.True(GitHubEndpoints.Cloud.IsCloud);
        Assert.Equal("https://api.github.com/repos/o/r", GitHubEndpoints.Cloud.Api("repos/o/r"));
        Assert.Equal("https://github.com/o/r.git", GitHubEndpoints.Cloud.GitUrl("o", "r"));
        var none = GitHubEndpoints.FromConfiguration(null, " ", new ServerEndpointPolicy([]));
        Assert.Same(GitHubEndpoints.Cloud, none);
    }

    [Fact]
    public async Task Every_rest_reader_still_targets_api_github_com_without_configuration()
    {
        var handler = new Recorder();
        HttpClient Client() => new(handler);
        const string token = "token-123456";
        await new GitHubTargetRepositoryProbe(Client()).ProbeAsync("org", "repo", token, default);
        await new GitHubDestinationAccessReader(Client()).ReadAsync("org", "me", token, default);
        await new GitHubConnectionValidator(Client()).ValidateAsync("org", token, default);
        await new GitHubImportRepositoryReader(Client(), TimeProvider.System).ObserveAsync("org", "repo", token, default);
        await new GitHubImportPolicyReader(Client(), TimeProvider.System).InspectAsync("org", "repo", token, default);
        await Assert.ThrowsAnyAsync<Exception>(() => new GitHubRefObservationReader(Client(), TimeProvider.System).ReadAsync("org", "repo", token, default));
        await Assert.ThrowsAnyAsync<Exception>(() => new GitHubConfigurationReader(Client()).GetNamesAsync("org", "repo", token, default));
        await new GitHubTraceabilityReader(Client()).IssueExistsAsync("org", "repo", 1, token, default);

        Assert.NotEmpty(handler.Uris);
        Assert.All(handler.Uris, u => Assert.Equal(("https", "api.github.com"), (u.Scheme, u.Host)));
    }

    [Fact]
    public async Task A_configured_endpoint_replaces_the_host_and_keeps_its_path_prefix()
    {
        var handler = new Recorder();
        var endpoints = new GitHubEndpoints("https://ghe.corp/api/v3", "https://ghe.corp");
        await new GitHubTargetRepositoryProbe(new HttpClient(handler), endpoints).ProbeAsync("org", "repo", "token-123456", default);
        await new GitHubConnectionValidator(new HttpClient(handler), endpoints).ValidateAsync("org", "token-123456", default);
        Assert.Equal(["https://ghe.corp/api/v3/repos/org/repo", "https://ghe.corp/api/v3/user"],
            handler.Uris.Select(u => u.ToString()).ToArray());
        Assert.Equal("https://ghe.corp/o/r.git", endpoints.GitUrl("o", "r"));
        Assert.False(endpoints.IsCloud);
    }

    [Theory]
    [InlineData("https://ghe.corp/api/v3", "https://ghe.corp", false, true)]
    [InlineData("http://ghe.corp/api/v3", "http://ghe.corp", false, false)]
    [InlineData("http://ghe.corp/api/v3", "http://ghe.corp", true, true)]
    [InlineData("https://evil.test/api/v3", "https://evil.test", false, false)]
    [InlineData("https://ghe.corp/api/v3", null, false, false)]
    [InlineData(null, "https://ghe.corp", false, false)]
    [InlineData("https://ghe.corp/api/v3?x=1", "https://ghe.corp", false, false)]
    [InlineData("https://user:pw@ghe.corp/api/v3", "https://ghe.corp", false, false)]
    [InlineData("https://169.254.169.254/api/v3", "https://169.254.169.254", false, false)]
    public void Overrides_must_be_allow_listed_complete_and_clean(string? api, string? web, bool allowHttp, bool accepted)
    {
        var policy = new ServerEndpointPolicy(["ghe.corp", "169.254.169.254"], allowHttp);
        if (accepted) Assert.False(GitHubEndpoints.FromConfiguration(api, web, policy).IsCloud);
        else Assert.Throws<InvalidOperationException>(() => GitHubEndpoints.FromConfiguration(api, web, policy));
    }

    [Fact]
    public void An_override_without_any_allowed_host_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GitHubEndpoints.FromConfiguration("https://ghe.corp/api/v3", "https://ghe.corp", new ServerEndpointPolicy([])));
    }
}
