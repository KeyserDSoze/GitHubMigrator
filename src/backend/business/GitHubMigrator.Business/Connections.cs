using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

public interface IConnectionValidator
{
    string ProviderKey { get; }
    ConnectionDirection Direction { get; }
    Task<ConnectionValidation> ValidateAsync(string organization, string personalAccessToken, CancellationToken ct);
}

// Self-hosted providers also need the (already policy-normalized) server root.
public interface IEndpointConnectionValidator : IConnectionValidator
{
    Task<ConnectionValidation> ValidateAsync(string organization, string? baseUrl,
        string personalAccessToken, CancellationToken ct);
}

public interface IConnectionRepository
{
    Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct);
    Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct);
    Task SaveAsync(MigrationConnection connection, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IConnectionSecretStore
{
    Task SaveAsync(string reference, string personalAccessToken, CancellationToken ct);
    Task<string?> ReadAsync(string reference, CancellationToken ct);
    Task DeleteAsync(string reference, CancellationToken ct);
}

public sealed record ConnectionDraft(string Name, string ProviderKey, string Organization,
    string PersonalAccessToken, string? BaseUrl = null);

public sealed class ConnectionValidationException(string code) : Exception("Connection validation rejected")
{
    public string Code { get; } = code;
}

public sealed class ConnectionService(
    IEnumerable<IConnectionValidator> validators,
    IConnectionRepository repository,
    IConnectionSecretStore secrets,
    TimeProvider timeProvider,
    ServerEndpointPolicy? endpointPolicy = null)
{
    private static readonly Regex OrganizationPattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,99}$", RegexOptions.Compiled);
    // Collection names of self-hosted servers may contain dots and spaces.
    private static readonly Regex CollectionPattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9 ._-]{0,99}$", RegexOptions.Compiled);
    private readonly IReadOnlyDictionary<string, IConnectionValidator> _validators =
        validators.ToDictionary(v => v.ProviderKey, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<object> SupportedProviders() =>
        _validators.Values.Select(v => (object)new { key = v.ProviderKey, direction = v.Direction.ToString().ToLowerInvariant(), status = "available" })
            .OrderBy(x => x.ToString()).ToArray();

    public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) =>
        repository.ListAsync(ct);

    public static void ValidateDraft(ConnectionDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 120)
            throw new ArgumentException("Connection name must contain 1-120 characters.");
        if (string.IsNullOrWhiteSpace(draft.ProviderKey))
            throw new ArgumentException("Provider is required.");
        if (string.IsNullOrWhiteSpace(draft.Organization) ||
            !(ProviderKeys.IsServer(draft.ProviderKey) ? CollectionPattern : OrganizationPattern).IsMatch(draft.Organization))
            throw new ArgumentException("Invalid organization identifier.");
        if (ProviderKeys.IsServer(draft.ProviderKey) != !string.IsNullOrWhiteSpace(draft.BaseUrl))
            throw new ArgumentException("Server URL is required for self-hosted providers only.");
        if (string.IsNullOrWhiteSpace(draft.PersonalAccessToken) ||
            draft.PersonalAccessToken.Length > 4096 ||
            draft.PersonalAccessToken.Any(char.IsControl))
            throw new ArgumentException("Invalid credential.");
    }

    public async Task<ConnectionValidation> ValidateDraftAsync(ConnectionDraft draft, CancellationToken ct)
    {
        ValidateDraft(draft);
        if (!_validators.TryGetValue(draft.ProviderKey, out var validator))
            throw new ArgumentException("Unsupported provider.");
        return await ValidateWithAsync(validator, draft.Organization, NormalizeBaseUrl(draft.ProviderKey, draft.BaseUrl),
            draft.PersonalAccessToken, ct);
    }

    private string? NormalizeBaseUrl(string providerKey, string? baseUrl) =>
        ProviderKeys.IsServer(providerKey)
            ? (endpointPolicy ?? throw new ArgumentException("Self-hosted providers are disabled.")).Normalize(baseUrl)
            : null;

    private static Task<ConnectionValidation> ValidateWithAsync(IConnectionValidator validator,
        string organization, string? baseUrl, string token, CancellationToken ct) =>
        validator is IEndpointConnectionValidator endpoint
            ? endpoint.ValidateAsync(organization, baseUrl, token, ct)
            : validator.ValidateAsync(organization, token, ct);

    public async Task<MigrationConnection> CreateAsync(ConnectionDraft draft, CancellationToken ct)
    {
        var result = await ValidateDraftAsync(draft, ct);
        if (!result.IsValid) throw new ConnectionValidationException(result.Code);
        var validator = _validators[draft.ProviderKey];
        var id = Guid.NewGuid();
        var reference = $"connection-{id:N}";
        var connection = new MigrationConnection(id, draft.Name.Trim(), validator.ProviderKey, validator.Direction,
            draft.Organization, reference, timeProvider.GetUtcNow(), timeProvider.GetUtcNow(),
            result.Code, result.AuthenticatedAs, result.PermissionAssessment,
            NormalizeBaseUrl(draft.ProviderKey, draft.BaseUrl));
        await secrets.SaveAsync(reference, draft.PersonalAccessToken, ct);
        try { await repository.SaveAsync(connection, ct); }
        catch
        {
            try { await secrets.DeleteAsync(reference, CancellationToken.None); }
            catch { /* Retain original error; failed cleanup is surfaced via operational logs. */ }
            throw;
        }
        return connection;
    }

    public async Task<MigrationConnection?> VerifyAsync(Guid id, CancellationToken ct)
    {
        var connection = await repository.GetAsync(id, ct);
        if (connection is null) return null;
        var secret = await secrets.ReadAsync(connection.SecretReference, ct);
        string? baseUrl = null;
        var hostAllowed = true;
        try { baseUrl = NormalizeBaseUrl(connection.ProviderKey, connection.BaseUrl); }
        catch (ArgumentException) { hostAllowed = false; }
        var result = !hostAllowed
            ? new ConnectionValidation(false, "ServerHostNotAllowed", null, "not_verified")
            : secret is null
            ? new ConnectionValidation(false, "CredentialMissing", null, "not_verified")
            : await ValidateWithAsync(_validators[connection.ProviderKey], connection.Organization,
                baseUrl, secret, ct);
        var updated = connection with
        {
            ValidatedAtUtc = timeProvider.GetUtcNow(),
            ValidationCode = result.Code,
            AuthenticatedAs = result.AuthenticatedAs,
            PermissionAssessment = result.PermissionAssessment
        };
        await repository.SaveAsync(updated, ct);
        return updated;
    }

    public async Task<MigrationConnection?> RotateAsync(Guid id, string newPersonalAccessToken, CancellationToken ct)
    {
        var existing = await repository.GetAsync(id, ct);
        if (existing is null) return null;
        var result = await ValidateDraftAsync(new ConnectionDraft(existing.Name, existing.ProviderKey,
            existing.Organization, newPersonalAccessToken, existing.BaseUrl), ct);
        if (!result.IsValid) throw new ConnectionValidationException(result.Code);
        await secrets.SaveAsync(existing.SecretReference, newPersonalAccessToken, ct);
        var updated = existing with
        {
            ValidatedAtUtc = timeProvider.GetUtcNow(),
            ValidationCode = result.Code,
            AuthenticatedAs = result.AuthenticatedAs,
            PermissionAssessment = result.PermissionAssessment
        };
        await repository.SaveAsync(updated, ct);
        return updated;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var connection = await repository.GetAsync(id, ct);
        if (connection is null) return false;
        await secrets.DeleteAsync(connection.SecretReference, ct);
        await repository.DeleteAsync(id, ct);
        return true;
    }
}
