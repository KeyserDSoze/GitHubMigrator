namespace GitHubMigrator.Domain;

public enum ConnectionDirection { Source, Destination }

public static class ProviderKeys
{
    public const string AzureDevOpsServices = "azure-devops-services";
    public const string AzureDevOpsServer = "azure-devops-server";
    public const string GitHubEnterpriseCloud = "github-enterprise-cloud";

    public static bool IsAzureDevOpsSource(string? key) =>
        string.Equals(key, AzureDevOpsServices, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, AzureDevOpsServer, StringComparison.OrdinalIgnoreCase);

    public static bool IsServer(string? key) =>
        string.Equals(key, AzureDevOpsServer, StringComparison.OrdinalIgnoreCase);

    public static bool IsGitHubDestination(string? key) =>
        string.Equals(key, GitHubEnterpriseCloud, StringComparison.OrdinalIgnoreCase);
}

// Transport-safe metadata: the PAT is never a member of this domain object.
// BaseUrl is only set for self-hosted providers (server root, e.g. https://ado.corp/tfs);
// Organization then holds the collection name.
public sealed record MigrationConnection(
    Guid Id,
    string Name,
    string ProviderKey,
    ConnectionDirection Direction,
    string Organization,
    string SecretReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ValidatedAtUtc,
    string ValidationCode,
    string? AuthenticatedAs,
    string PermissionAssessment,
    string? BaseUrl = null
);

public sealed record ConnectionValidation(
    bool IsValid,
    string Code,
    string? AuthenticatedAs,
    string PermissionAssessment
);
