namespace GitHubMigrator.Domain;

public enum ConnectionDirection { Source, Destination }

// Transport-safe metadata: the PAT is never a member of this domain object.
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
    string PermissionAssessment
);

public sealed record ConnectionValidation(
    bool IsValid,
    string Code,
    string? AuthenticatedAs,
    string PermissionAssessment
);
