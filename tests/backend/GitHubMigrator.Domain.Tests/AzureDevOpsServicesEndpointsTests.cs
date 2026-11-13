using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureDevOpsServicesEndpointsTests
{
    [Fact]
    public void The_default_is_microsofts_cloud_with_the_same_urls_as_before()
    {
        var cloud = AzureDevOpsServicesEndpoints.Cloud;
        Assert.True(cloud.IsCloud);
        Assert.Equal("https://dev.azure.com/my-org", cloud.Core("my-org"));
        Assert.Equal("https://vssps.dev.azure.com/my-org", cloud.Graph("my-org"));
        Assert.Equal("https://dev.azure.com/my-org/Project%20A/_git/Repo%20B", cloud.GitUrl("my-org", "Project A", "Repo B"));
        Assert.Same(cloud, AzureDevOpsServicesEndpoints.FromConfiguration(null, " ", null, new ServerEndpointPolicy([])));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9", "http://127.0.0.1:9/vssps", "http://127.0.0.1:9", true, true)]
    [InlineData("http://127.0.0.1:9", "http://127.0.0.1:9/vssps", "http://127.0.0.1:9", false, false)]
    [InlineData("https://sim.test", "https://sim.test/vssps", "https://sim.test", false, true)]
    [InlineData("https://evil.test", "https://sim.test/vssps", "https://sim.test", false, false)]
    [InlineData("https://sim.test", null, "https://sim.test", false, false)]
    [InlineData("https://sim.test", "https://sim.test/vssps", null, false, false)]
    public void An_override_needs_all_three_roots_and_allow_listed_hosts(
        string? core, string? graph, string? git, bool allowHttp, bool accepted)
    {
        var policy = new ServerEndpointPolicy(["127.0.0.1", "sim.test"], allowHttp);
        if (accepted)
        {
            var endpoints = AzureDevOpsServicesEndpoints.FromConfiguration(core, graph, git, policy);
            Assert.False(endpoints.IsCloud);
            Assert.Equal(core + "/org", endpoints.Core("org"));
            Assert.Equal(graph + "/org", endpoints.Graph("org"));
        }
        else Assert.Throws<InvalidOperationException>(() => AzureDevOpsServicesEndpoints.FromConfiguration(core, graph, git, policy));
    }

    [Fact]
    public void An_override_without_any_allowed_host_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => AzureDevOpsServicesEndpoints.FromConfiguration(
            "https://sim.test", "https://sim.test/vssps", "https://sim.test", new ServerEndpointPolicy([])));
    }
}
