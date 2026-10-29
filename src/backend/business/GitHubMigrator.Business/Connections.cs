using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

public interface IConnectionValidator
{
    string ProviderKey { get; }
    ConnectionDirection Direction { get; }
    Task<ConnectionValidation> ValidateAsync(string organization, string personalAccessToken, CancellationToken ct);
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

public sealed record ConnectionDraft(string Name, string ProviderKey, string Organization, string PersonalAccessToken);

public sealed class ConnectionValidationException(string code) : Exception("Connection validation rejected")
{
    public string Code { get; } = code;
}

public sealed class ConnectionService(
    IEnumerable<IConnectionValidator> validators,
    IConnectionRepository repository,
    IConnectionSecretStore secrets,
    TimeProvider timeProvider)
{
    private static readonly Regex OrganizationPattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,99}$", RegexOptions.Compiled);
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
            !OrganizationPattern.IsMatch(draft.Organization))
            throw new ArgumentException("Invalid organization identifier.");
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
        return await validator.ValidateAsync(draft.Organization, draft.PersonalAccessToken, ct);
    }

    public async Task<MigrationConnection> CreateAsync(ConnectionDraft draft, CancellationToken ct)
    {
        var result = await ValidateDraftAsync(draft, ct);
        if (!result.IsValid) throw new ConnectionValidationException(result.Code);
        var validator = _validators[draft.ProviderKey];
        var id = Guid.NewGuid();
        var reference = $"connection-{id:N}";
        var connection = new MigrationConnection(id, draft.Name.Trim(), validator.ProviderKey, validator.Direction,
            draft.Organization, reference, timeProvider.GetUtcNow(), timeProvider.GetUtcNow(),
            result.Code, result.AuthenticatedAs, result.PermissionAssessment);
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
        var result = secret is null
            ? new ConnectionValidation(false, "CredentialMissing", null, "not_verified")
            : await _validators[connection.ProviderKey].ValidateAsync(connection.Organization, secret, ct);
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
            existing.Organization, newPersonalAccessToken), ct);
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
