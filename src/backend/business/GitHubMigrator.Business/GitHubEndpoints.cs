namespace GitHubMigrator.Business;

// Where a deployment's GitHub lives: REST API root and web/git root.
// The default is github.com. Any other value comes only from operator configuration and
// must pass the same egress allow-list used for self-hosted source providers.
public sealed record GitHubEndpoints(string ApiRoot, string WebRoot)
{
    public static readonly GitHubEndpoints Cloud = new("https://api.github.com", "https://github.com");

    public bool IsCloud => ApiRoot == Cloud.ApiRoot && WebRoot == Cloud.WebRoot;

    public string Api(string path) => ApiRoot + "/" + path.TrimStart('/');

    public string GitUrl(string organization, string repository) =>
        $"{WebRoot}/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}.git";

    // GitHub Enterprise Server serves REST under /api/v3 and git under the web host root.
    public static GitHubEndpoints FromConfiguration(string? apiRoot, string? webRoot, ServerEndpointPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(apiRoot) && string.IsNullOrWhiteSpace(webRoot)) return Cloud;
        if (string.IsNullOrWhiteSpace(apiRoot) || string.IsNullOrWhiteSpace(webRoot))
            throw new InvalidOperationException("Providers:GitHub:ApiRoot and Providers:GitHub:WebRoot must be configured together.");
        try { return new GitHubEndpoints(policy.Normalize(apiRoot), policy.Normalize(webRoot)); }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("Providers:GitHub endpoint rejected by the egress policy: " + ex.Message, ex);
        }
    }
}
