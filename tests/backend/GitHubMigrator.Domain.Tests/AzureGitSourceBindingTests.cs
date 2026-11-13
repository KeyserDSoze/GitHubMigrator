using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureGitSourceBindingTests
{
    private static MigrationConnection Connection(string provider, string organization, string? baseUrl = null,
        ConnectionDirection direction = ConnectionDirection.Source, string validation = "Verified") =>
        new(Guid.NewGuid(), "c", provider, direction, organization, "secret", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            validation, "me", "organization_projects_read", baseUrl);

    private static AzureGitSourceLocation Location(MigrationConnection c, string provider, string? serverRoot = null) =>
        new(c.Id, c.Organization, "Project", "Repo", "repo-id", provider, serverRoot);

    [Fact]
    public void Services_keeps_the_default_location_and_never_carries_a_server_root()
    {
        var connection = Connection(ProviderKeys.AzureDevOpsServices, "my-org");
        var bound = AzureGitSourceBinding.Bind(Location(connection, ProviderKeys.AzureDevOpsServices, "http://injected"), connection);
        Assert.Null(bound.ServerRoot);
        Assert.Equal("my-org", bound.Organization);
    }

    [Fact]
    public void Server_takes_its_root_from_the_verified_connection_not_from_the_location()
    {
        var connection = Connection(ProviderKeys.AzureDevOpsServer, "DefaultCollection", "https://ado.corp.test/tfs");
        var bound = AzureGitSourceBinding.Bind(Location(connection, ProviderKeys.AzureDevOpsServer, "https://evil.test/tfs"), connection);
        Assert.Equal("https://ado.corp.test/tfs", bound.ServerRoot);
        Assert.Equal(ProviderKeys.AzureDevOpsServer, bound.SourceProvider);
    }

    [Theory]
    [InlineData("wrong-provider")]
    [InlineData("provider-mismatch")]
    [InlineData("destination")]
    [InlineData("not-verified")]
    [InlineData("other-organization")]
    [InlineData("other-connection")]
    [InlineData("server-without-url")]
    public void Anything_that_does_not_line_up_with_the_verified_connection_is_refused(string scenario)
    {
        var connection = scenario switch
        {
            "wrong-provider" => Connection(ProviderKeys.GitHubEnterpriseCloud, "my-org"),
            "provider-mismatch" => Connection(ProviderKeys.AzureDevOpsServer, "my-org", "https://ado.corp.test/tfs"),
            "destination" => Connection(ProviderKeys.AzureDevOpsServices, "my-org", direction: ConnectionDirection.Destination),
            "not-verified" => Connection(ProviderKeys.AzureDevOpsServices, "my-org", validation: "Unauthorized"),
            "server-without-url" => Connection(ProviderKeys.AzureDevOpsServer, "my-org"),
            _ => Connection(ProviderKeys.AzureDevOpsServices, "my-org")
        };
        var provider = scenario == "server-without-url" ? ProviderKeys.AzureDevOpsServer : ProviderKeys.AzureDevOpsServices;
        var location = Location(connection, provider);
        if (scenario == "other-organization") location = location with { Organization = "someone-else" };
        if (scenario == "other-connection") location = location with { ConnectionId = Guid.NewGuid() };
        var error = Assert.Throws<GitSourceBundleException>(() => AzureGitSourceBinding.Bind(location, connection));
        Assert.Equal("AzureGitSourceNotVerified", error.Code);
    }
}
