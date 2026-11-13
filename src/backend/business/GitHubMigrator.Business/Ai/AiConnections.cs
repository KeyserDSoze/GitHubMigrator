using System.Text.RegularExpressions;

namespace GitHubMigrator.Business;

// A UI-managed connection to a language model. It is deliberately NOT a MigrationConnection: those carry a
// source/destination direction that the transfer chain and the restricted runtime grants key on.
public sealed record AiConnection(
    Guid Id,
    string Name,
    AiProvider Provider,
    string Endpoint,
    string Model,
    AiAuthMode Auth,
    string? ApiVersion,
    TokenLimitParameter TokenParameter,
    int MaxOutputTokens,
    double? Temperature,
    int TimeoutSeconds,
    int MaxInputBytes,
    string SecretReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? VerifiedAtUtc,
    string VerificationCode,
    string? VerifiedModel,
    bool SendSourceContent,
    DateTimeOffset? ConsentAtUtc,
    string? ConsentBy)
{
    public const string VerifiedCode = "Verified";
    public bool IsVerified => VerificationCode == VerifiedCode;

    public ChatEndpoint ToEndpoint() => new(Provider, Endpoint, Model, Auth, ApiVersion, TimeSpan.FromSeconds(TimeoutSeconds),
        MaxOutputTokens, Temperature, MaxInputBytes, 2, TokenParameter);
}

public sealed record AiConnectionDraft(
    string Name,
    AiProvider Provider,
    string Endpoint,
    string Model,
    AiAuthMode Auth,
    string? ApiKey = null,
    string? ApiVersion = null,
    TokenLimitParameter? TokenParameter = null,
    int? MaxOutputTokens = null,
    bool SendTemperature = true,
    double? Temperature = null,
    int? TimeoutSeconds = null,
    int? MaxInputBytes = null);

public sealed record AiVerification(bool IsValid, string Code, string? Model = null, string? Detail = null);

public sealed class AiConnectionException(string code) : Exception("AI connection rejected")
{
    public string Code { get; } = code;
}

public interface IAiConnectionRepository
{
    Task<IReadOnlyList<AiConnection>> ListAsync(CancellationToken ct);
    Task<AiConnection?> GetAsync(Guid id, CancellationToken ct);
    Task SaveAsync(AiConnection connection, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

// Where a model may be contacted. Own type (not ServerEndpointPolicy) so DI can tell it from the Azure DevOps Server
// policy; the host list is operator configuration, never user input.
public sealed class AiEndpointPolicy
{
    private readonly ServerEndpointPolicy _inner;

    public AiEndpointPolicy(IEnumerable<string> allowedHosts, bool allowHttp)
    {
        Hosts = allowedHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToArray();
        AllowHttp = allowHttp;
        _inner = new ServerEndpointPolicy(Hosts, allowHttp);
    }

    public IReadOnlyList<string> Hosts { get; }
    public bool AllowHttp { get; }

    public static AiEndpointPolicy FromConfiguration(string? hosts, bool allowHttp) =>
        new(string.IsNullOrWhiteSpace(hosts)
            ? AiHostDefaults.AzureHosts
            : hosts.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries), allowHttp);

    public string Normalize(string? endpoint) => _inner.Normalize(endpoint);
}

public sealed record AiConnectionUse(AiConnection Connection, ChatEndpoint Endpoint, string? Secret);

public sealed partial class AiConnectionService(
    IAiConnectionRepository repository,
    IConnectionSecretStore secrets,
    IChatModelClient client,
    AiEndpointPolicy endpointPolicy,
    TimeProvider clock,
    bool managedIdentityAvailable = false)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,99}$")]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(-preview)?$")]
    private static partial Regex ApiVersionPattern();

    public AiEndpointPolicy Policy => endpointPolicy;
    public bool ManagedIdentityAvailable => managedIdentityAvailable;

    public Task<IReadOnlyList<AiConnection>> ListAsync(CancellationToken ct) => repository.ListAsync(ct);

    // Shape and policy only; no network.
    public (AiConnectionDraft Draft, string Endpoint) Normalize(AiConnectionDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 120)
            throw new ArgumentException("Connection name must contain 1-120 characters.");
        if (!Enum.IsDefined(draft.Provider) || !Enum.IsDefined(draft.Auth))
            throw new ArgumentException("Unknown provider or authentication mode.");
        if (string.IsNullOrWhiteSpace(draft.Model) || !ModelPattern().IsMatch(draft.Model.Trim()))
            throw new ArgumentException("Invalid model name.");
        if (draft.Auth == AiAuthMode.ManagedIdentity && (draft.Provider != AiProvider.AzureOpenAI || !managedIdentityAvailable))
            throw new ArgumentException("Managed identity is not available for this provider or deployment.");
        if (draft.Auth == AiAuthMode.ApiKey)
        {
            if (string.IsNullOrEmpty(draft.ApiKey) || draft.ApiKey.Length is < 8 or > 4096 || draft.ApiKey.Any(char.IsControl) || draft.ApiKey != draft.ApiKey.Trim())
                throw new ArgumentException("Invalid API key.");
        }
        else if (!string.IsNullOrEmpty(draft.ApiKey))
            throw new ArgumentException("This authentication mode takes no API key.");
        if (!string.IsNullOrWhiteSpace(draft.ApiVersion) &&
            (draft.Provider != AiProvider.AzureOpenAI || !ApiVersionPattern().IsMatch(draft.ApiVersion.Trim())))
            throw new ArgumentException("Invalid API version.");
        if (draft.MaxOutputTokens is { } tokens and (< 16 or > 128_000)) throw new ArgumentException("Invalid output token limit.");
        if (draft.Temperature is { } t and (< 0 or > 2)) throw new ArgumentException("Invalid temperature.");
        if (draft.TimeoutSeconds is { } timeout and (< 5 or > 600)) throw new ArgumentException("Invalid timeout.");
        if (draft.MaxInputBytes is { } input and (< 1_000 or > 1_000_000)) throw new ArgumentException("Invalid input limit.");
        return (draft, endpointPolicy.Normalize(draft.Endpoint));
    }

    private static ChatEndpoint ToEndpoint(AiConnectionDraft draft, string endpoint) => new(
        draft.Provider, endpoint, draft.Model.Trim(), draft.Auth,
        string.IsNullOrWhiteSpace(draft.ApiVersion) ? null : draft.ApiVersion.Trim(),
        TimeSpan.FromSeconds(draft.TimeoutSeconds ?? 60), draft.MaxOutputTokens ?? 4096,
        draft.SendTemperature ? draft.Temperature ?? 0 : null, draft.MaxInputBytes ?? 200_000, 2,
        draft.TokenParameter ?? (draft.Provider == AiProvider.AzureOpenAI ? TokenLimitParameter.MaxCompletionTokens : TokenLimitParameter.MaxTokens));

    // A real, tiny completion: it proves reachability, credential, model name and the parameter dialect in one go.
    // Nothing from any pipeline is ever part of this request.
    public async Task<AiVerification> TestAsync(ChatEndpoint endpoint, string? secret, CancellationToken ct)
    {
        try
        {
            var result = await client.CompleteAsync(endpoint, secret,
                new([ChatMessage.System("Reply with the single word OK."), ChatMessage.User("ping")], MaxOutputTokens: 512), ct);
            return new(true, AiConnection.VerifiedCode, result.Model);
        }
        catch (ChatModelException ex)
        {
            var code = ex.Failure == ChatFailure.BadRequest && ex.ProviderCode is "unsupported_parameter" or "unsupported_value"
                ? "UnsupportedParameter" : ex.Failure.ToString();
            return new(false, code, null, ex.ProviderCode);
        }
    }

    public async Task<AiVerification> ValidateDraftAsync(AiConnectionDraft draft, CancellationToken ct)
    {
        var (_, endpoint) = Normalize(draft);
        return await TestAsync(ToEndpoint(draft, endpoint), draft.ApiKey, ct);
    }

    public async Task<AiConnection> CreateAsync(AiConnectionDraft draft, CancellationToken ct)
    {
        var (_, endpoint) = Normalize(draft);
        var chat = ToEndpoint(draft, endpoint);
        var result = await TestAsync(chat, draft.ApiKey, ct);
        if (!result.IsValid) throw new AiConnectionException(result.Code);

        var id = Guid.NewGuid();
        var reference = draft.Auth == AiAuthMode.ApiKey ? $"ai-connection-{id:N}" : "";
        var now = clock.GetUtcNow();
        var connection = new AiConnection(id, draft.Name.Trim(), draft.Provider, endpoint, chat.Model, draft.Auth, chat.ApiVersion,
            chat.TokenParameter, chat.MaxOutputTokens, chat.Temperature, (int)chat.Timeout!.Value.TotalSeconds, chat.MaxInputBytes,
            reference, now, now, AiConnection.VerifiedCode, result.Model, false, null, null);
        if (reference.Length > 0) await secrets.SaveAsync(reference, draft.ApiKey!, ct);
        try { await repository.SaveAsync(connection, ct); }
        catch
        {
            if (reference.Length > 0)
            {
                try { await secrets.DeleteAsync(reference, CancellationToken.None); }
                catch { /* Retain original error; failed cleanup is surfaced via operational logs. */ }
            }
            throw;
        }
        return connection;
    }

    public async Task<AiConnection?> VerifyAsync(Guid id, CancellationToken ct)
    {
        var connection = await repository.GetAsync(id, ct);
        if (connection is null) return null;
        var result = await TestStoredAsync(connection, ct);
        var updated = connection with
        {
            VerifiedAtUtc = clock.GetUtcNow(),
            VerificationCode = result.Code,
            VerifiedModel = result.IsValid ? result.Model : null
        };
        await repository.SaveAsync(updated, ct);
        return updated;
    }

    private async Task<AiVerification> TestStoredAsync(AiConnection connection, CancellationToken ct)
    {
        try { endpointPolicy.Normalize(connection.Endpoint); }
        catch (ArgumentException) { return new(false, "EndpointNotAllowed"); }
        string? secret = null;
        if (connection.Auth == AiAuthMode.ApiKey)
        {
            secret = await secrets.ReadAsync(connection.SecretReference, ct);
            if (secret is null) return new(false, "CredentialMissing");
        }
        return await TestAsync(connection.ToEndpoint(), secret, ct);
    }

    public async Task<AiConnection?> RotateAsync(Guid id, string newApiKey, CancellationToken ct)
    {
        var existing = await repository.GetAsync(id, ct);
        if (existing is null) return null;
        if (existing.Auth != AiAuthMode.ApiKey) throw new ArgumentException("This connection has no API key.");
        if (string.IsNullOrEmpty(newApiKey) || newApiKey.Length is < 8 or > 4096 || newApiKey.Any(char.IsControl) || newApiKey != newApiKey.Trim())
            throw new ArgumentException("Invalid API key.");
        try { endpointPolicy.Normalize(existing.Endpoint); }
        catch (ArgumentException) { throw new AiConnectionException("EndpointNotAllowed"); }
        var result = await TestAsync(existing.ToEndpoint(), newApiKey, ct);
        if (!result.IsValid) throw new AiConnectionException(result.Code);
        await secrets.SaveAsync(existing.SecretReference, newApiKey, ct);
        var updated = existing with { VerifiedAtUtc = clock.GetUtcNow(), VerificationCode = AiConnection.VerifiedCode, VerifiedModel = result.Model };
        await repository.SaveAsync(updated, ct);
        return updated;
    }

    // Letting pipeline source leave for a model is a data-governance decision: it is recorded with who and when, and
    // it is only possible on a connection that has just proven it works.
    public async Task<AiConnection?> SetSourceConsentAsync(Guid id, bool allow, string actor, CancellationToken ct)
    {
        var existing = await repository.GetAsync(id, ct);
        if (existing is null) return null;
        if (allow && !existing.IsVerified) throw new AiConnectionException("NotVerified");
        var by = new string((actor ?? "").Where(c => !char.IsControl(c)).ToArray());
        var updated = existing with
        {
            SendSourceContent = allow,
            ConsentAtUtc = clock.GetUtcNow(),
            ConsentBy = by.Length == 0 ? "unknown" : by.Length > 160 ? by[..160] : by
        };
        await repository.SaveAsync(updated, ct);
        return updated;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var connection = await repository.GetAsync(id, ct);
        if (connection is null) return false;
        if (connection.SecretReference.Length > 0) await secrets.DeleteAsync(connection.SecretReference, ct);
        await repository.DeleteAsync(id, ct);
        return true;
    }

    // What a feature that talks to the model gets. It re-checks everything that may have changed since creation.
    public async Task<AiConnectionUse> ResolveAsync(Guid id, bool requireSourceConsent, CancellationToken ct)
    {
        var connection = await repository.GetAsync(id, ct) ?? throw new AiConnectionException("NotFound");
        if (!connection.IsVerified) throw new AiConnectionException("NotVerified");
        try { endpointPolicy.Normalize(connection.Endpoint); }
        catch (ArgumentException) { throw new AiConnectionException("EndpointNotAllowed"); }
        if (requireSourceConsent && !connection.SendSourceContent) throw new AiConnectionException("SourceConsentRequired");
        string? secret = null;
        if (connection.Auth == AiAuthMode.ApiKey)
            secret = await secrets.ReadAsync(connection.SecretReference, ct) ?? throw new AiConnectionException("CredentialMissing");
        return new(connection, connection.ToEndpoint(), secret);
    }
}
