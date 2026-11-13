using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class AzureGitRemotePolicyTests
{
    private const string Token = "pat-must-never-reach-argv";
    private static readonly string BareDir = Path.Combine(Path.GetTempPath(), "fixed-git-dir");

    [Theory]
    [InlineData("https://dev.azure.com/org/p/_git/r", true)]
    [InlineData("http://dev.azure.com/org/p/_git/r", false)]
    [InlineData("https://dev.azure.com:8443/org/p/_git/r", false)]
    [InlineData("https://evil.dev.azure.com/org/p/_git/r", false)]
    [InlineData("https://dev.azure.com/org/p/_git/r?x=1", false)]
    [InlineData("https://user:pw@dev.azure.com/org/p/_git/r", false)]
    [InlineData("https://ado.corp.test/tfs/C/p/_git/r", false)]
    public void The_default_policy_only_ever_allows_microsofts_cloud(string url, bool allowed) =>
        Assert.Equal(allowed, AzureGitRemotePolicy.Cloud.Allows(new Uri(url)));

    [Theory]
    [InlineData("https://ado.corp.test/tfs/C/p/_git/r", false, true)]
    [InlineData("http://ado.corp.test/tfs/C/p/_git/r", false, false)]
    [InlineData("http://ado.corp.test/tfs/C/p/_git/r", true, true)]
    [InlineData("https://other.corp.test/tfs/C/p/_git/r", false, false)]
    [InlineData("https://ado.corp.test/tfs/C/p/_git/r?x=1", false, false)]
    [InlineData("https://169.254.169.254/tfs/C/p/_git/r", false, false)]
    public void An_allow_listed_server_is_the_only_extra_remote(string url, bool allowHttp, bool allowed)
    {
        var policy = new AzureGitRemotePolicy(null, new ServerEndpointPolicy(["ado.corp.test", "169.254.169.254"], allowHttp));
        Assert.Equal(allowed, policy.Allows(new Uri(url)));
        // the cloud stays allowed whatever else is configured
        Assert.True(policy.Allows(new Uri("https://dev.azure.com/org/p/_git/r")));
    }

    [Fact]
    public void A_services_stand_in_is_allowed_by_origin_only()
    {
        var services = new AzureDevOpsServicesEndpoints("http://127.0.0.1:9000/core", "http://127.0.0.1:9000/graph", "http://127.0.0.1:9000");
        var policy = new AzureGitRemotePolicy(services, null);
        Assert.True(policy.Allows(new Uri("http://127.0.0.1:9000/org/p/_git/r")));
        Assert.False(policy.Allows(new Uri("http://127.0.0.1:9001/org/p/_git/r")));
        Assert.False(policy.Allows(new Uri("https://127.0.0.1:9000/org/p/_git/r")));
        Assert.False(new AzureGitRemotePolicy().Allows(new Uri("http://127.0.0.1:9000/org/p/_git/r")));
    }

    [Fact]
    public void The_start_info_follows_the_scheme_the_policy_allowed_and_keeps_the_token_out_of_argv()
    {
        var policy = new AzureGitRemotePolicy(null, new ServerEndpointPolicy(["ado.corp.test"], true), skipTlsVerification: true);

        var plain = GitCliAzureReadOnlyTransport.CreateFetchStartInfo(BareDir, new Uri("http://ado.corp.test/tfs/C/p/_git/r"), Token, policy);
        Assert.Equal("http", plain.Environment["GIT_ALLOW_PROTOCOL"]);
        Assert.Equal("protocol.http.allow", plain.Environment["GIT_CONFIG_KEY_4"]);
        Assert.Equal("always", plain.Environment["GIT_CONFIG_VALUE_4"]);
        Assert.Equal("true", plain.Environment["GIT_CONFIG_VALUE_2"]);

        var tls = GitCliAzureReadOnlyTransport.CreateFetchStartInfo(BareDir, new Uri("https://ado.corp.test/tfs/C/p/_git/r"), Token, policy);
        Assert.Equal("https", tls.Environment["GIT_ALLOW_PROTOCOL"]);
        Assert.Equal("protocol.https.allow", tls.Environment["GIT_CONFIG_KEY_4"]);
        Assert.Equal("false", tls.Environment["GIT_CONFIG_VALUE_2"]);

        foreach (var info in new[] { plain, tls })
        {
            Assert.DoesNotContain(Token, string.Join(" ", info.ArgumentList));
            Assert.Contains("AUTHORIZATION: Basic ", info.Environment["GIT_CONFIG_VALUE_5"]);
            Assert.DoesNotContain("push", info.ArgumentList);
        }

        // the same host is refused when it is not allow-listed, and TLS verification is never skipped for the cloud
        var refused = Assert.Throws<GitSourceBundleException>(() => GitCliAzureReadOnlyTransport.CreateFetchStartInfo(
            BareDir, new Uri("https://other.test/tfs/C/p/_git/r"), Token, policy));
        Assert.Equal("AzureGitTransportRejected", refused.Code);
        var cloud = GitCliAzureReadOnlyTransport.CreateFetchStartInfo(BareDir, new Uri("https://dev.azure.com/o/p/_git/r"), Token, policy);
        Assert.Equal("true", cloud.Environment["GIT_CONFIG_VALUE_2"]);
        var strictCloud = GitCliAzureReadOnlyTransport.CreateFetchStartInfo(BareDir, new Uri("https://dev.azure.com/o/p/_git/r"), Token);
        Assert.Equal("true", strictCloud.Environment["GIT_CONFIG_VALUE_2"]);
    }
}
