using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// Which remotes the read-only Azure DevOps fetch may ever contact.
// Default: https://dev.azure.com on its default port, nothing else. Operator configuration can add an
// allow-listed Azure DevOps Server and (Development only) a Services stand-in for the lab simulator.
public sealed class AzureGitRemotePolicy(
    AzureDevOpsServicesEndpoints? services = null, ServerEndpointPolicy? server = null,
    bool skipTlsVerification = false)
{
    public static AzureGitRemotePolicy Cloud { get; } = new();

    public AzureDevOpsServicesEndpoints Services { get; } = services ?? AzureDevOpsServicesEndpoints.Cloud;
    public ServerEndpointPolicy? Server { get; } = server;
    public bool SkipTlsVerification { get; } = skipTlsVerification;

    public bool Allows(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return false;
        if (url.Scheme == Uri.UriSchemeHttps && url.Host == "dev.azure.com" && url.IsDefaultPort) return true;
        if (!Services.IsCloud && SameOrigin(Services.GitRoot, url)) return true;
        if (Server is not { Enabled: true }) return false;
        try { Server.Normalize(url.GetLeftPart(UriPartial.Authority)); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool SameOrigin(string root, Uri url) =>
        Uri.TryCreate(root, UriKind.Absolute, out var expected) && expected.Scheme == url.Scheme &&
        string.Equals(expected.Host, url.Host, StringComparison.OrdinalIgnoreCase) && expected.Port == url.Port;
}
