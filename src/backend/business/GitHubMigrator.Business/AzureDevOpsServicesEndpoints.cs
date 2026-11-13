namespace GitHubMigrator.Business;

// Where Azure DevOps Services lives: core REST, Graph (vssps) and git. The default is Microsoft's
// cloud. An override exists ONLY so the lab/test simulator can stand in for Services; the API honours
// it in the Development environment alone and still requires every host to be allow-listed.
public sealed record AzureDevOpsServicesEndpoints(string CoreRoot, string GraphRoot, string GitRoot)
{
    public static readonly AzureDevOpsServicesEndpoints Cloud =
        new("https://dev.azure.com", "https://vssps.dev.azure.com", "https://dev.azure.com");

    public bool IsCloud => this == Cloud;

    public string Core(string organization) => $"{CoreRoot}/{Uri.EscapeDataString(organization)}";
    public string Graph(string organization) => $"{GraphRoot}/{Uri.EscapeDataString(organization)}";

    public string GitUrl(string organization, string project, string repository) =>
        $"{GitRoot}/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repository)}";

    public static AzureDevOpsServicesEndpoints FromConfiguration(
        string? coreRoot, string? graphRoot, string? gitRoot, ServerEndpointPolicy policy)
    {
        var values = new[] { coreRoot, graphRoot, gitRoot };
        if (values.All(string.IsNullOrWhiteSpace)) return Cloud;
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "Providers:AzureDevOpsServices CoreRoot, GraphRoot and GitRoot must be configured together.");
        try { return new(policy.Normalize(coreRoot), policy.Normalize(graphRoot), policy.Normalize(gitRoot)); }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "Providers:AzureDevOpsServices endpoint rejected by the egress policy: " + ex.Message, ex);
        }
    }
}
