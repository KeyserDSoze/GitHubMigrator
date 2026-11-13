using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed class FakeChatClient : IChatModelClient
{
    private readonly Queue<Func<ChatEndpoint, string?, ChatRequest, ChatResult>> _script = new();
    public List<(ChatEndpoint Endpoint, string? Secret, ChatRequest Request)> Calls { get; } = [];

    public FakeChatClient Then(Func<ChatResult> result) { _script.Enqueue((_, _, _) => result()); return this; }
    public FakeChatClient Fails(ChatFailure failure, string? providerCode = null) =>
        Then(() => throw new ChatModelException(failure, "x", providerCode: providerCode));

    public Task<ChatResult> CompleteAsync(ChatEndpoint endpoint, string? secret, ChatRequest request, CancellationToken ct)
    {
        Calls.Add((endpoint, secret, request));
        return Task.FromResult(_script.Count == 0 ? Ok() : _script.Dequeue()(endpoint, secret, request));
    }

    public static ChatResult Ok(string model = "gpt-sim") => new("OK", "stop", new(5, 1), model, 1);
}

internal sealed class MemoryAiRepository : IAiConnectionRepository
{
    public Dictionary<Guid, AiConnection> Rows { get; } = [];
    public bool FailSave { get; set; }
    public Task<IReadOnlyList<AiConnection>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AiConnection>>(Rows.Values.Select(c => c with { SecretReference = "" }).ToArray());
    public Task<AiConnection?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(id));
    public Task SaveAsync(AiConnection c, CancellationToken ct)
    {
        if (FailSave) throw new InvalidOperationException("db down");
        Rows[c.Id] = c;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(Guid id, CancellationToken ct) { Rows.Remove(id); return Task.CompletedTask; }
}

internal sealed class MemorySecrets : IConnectionSecretStore
{
    public Dictionary<string, string> Values { get; } = [];
    public Task SaveAsync(string reference, string personalAccessToken, CancellationToken ct) { Values[reference] = personalAccessToken; return Task.CompletedTask; }
    public Task<string?> ReadAsync(string reference, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault(reference));
    public Task DeleteAsync(string reference, CancellationToken ct) { Values.Remove(reference); return Task.CompletedTask; }
}

public sealed class AiConnectionServiceTests
{
    private const string Key = "sk-live-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public MemoryAiRepository Repository { get; } = new();
        public MemorySecrets Secrets { get; } = new();
        public FakeChatClient Client { get; } = new();
        public AiConnectionService Service { get; }

        public Rig(string hosts = "*.openai.azure.com,api.openai.com,llm.corp.example", bool allowHttp = false, bool managedIdentity = false) =>
            Service = new(Repository, Secrets, Client, AiEndpointPolicy.FromConfiguration(hosts, allowHttp), new FixedTime(Now), managedIdentity);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AiConnectionDraft OpenAi(Func<AiConnectionDraft, AiConnectionDraft>? tweak = null) =>
        (tweak ?? (d => d))(new("Corp OpenAI", AiProvider.OpenAICompatible, "https://api.openai.com/v1/", " gpt-4.1 ", AiAuthMode.ApiKey, Key));

    // ---- creation -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_connection_is_created_with_the_key_in_the_secret_store_only()
    {
        var rig = new Rig();
        rig.Client.Then(() => FakeChatClient.Ok("gpt-4.1-2025"));
        var created = await rig.Service.CreateAsync(OpenAi(), default);

        Assert.True(created.IsVerified);
        Assert.Equal("https://api.openai.com/v1", created.Endpoint);
        Assert.Equal("gpt-4.1", created.Model);
        Assert.Equal("gpt-4.1-2025", created.VerifiedModel);
        Assert.Equal(Now, created.VerifiedAtUtc);
        Assert.False(created.SendSourceContent);
        Assert.Null(created.ConsentAtUtc);
        Assert.Equal($"ai-connection-{created.Id:N}", created.SecretReference);
        Assert.Equal(Key, rig.Secrets.Values[created.SecretReference]);
        Assert.Equal(0, created.Temperature);
        Assert.Equal(TokenLimitParameter.MaxTokens, created.TokenParameter);
        Assert.Equal(60, created.TimeoutSeconds);
        Assert.DoesNotContain(Key, System.Text.Json.JsonSerializer.Serialize(rig.Repository.Rows.Values.Select(c => c with { SecretReference = "" })));
    }

    [Fact]
    public async Task The_verification_request_is_a_fixed_ping_that_carries_nothing_from_any_pipeline()
    {
        var rig = new Rig();
        await rig.Service.CreateAsync(OpenAi(), default);
        var call = Assert.Single(rig.Client.Calls);
        Assert.Equal(Key, call.Secret);
        Assert.Equal(["system", "user"], call.Request.Messages.Select(m => m.Role));
        Assert.Equal("ping", call.Request.Messages[1].Content);
        Assert.True(call.Request.MaxOutputTokens <= 512);
    }

    [Fact]
    public async Task Azure_defaults_to_max_completion_tokens_and_managed_identity_stores_no_secret()
    {
        var rig = new Rig(managedIdentity: true);
        var created = await rig.Service.CreateAsync(new("Foundry", AiProvider.AzureOpenAI, "https://res.openai.azure.com", "gpt-5", AiAuthMode.ManagedIdentity,
            ApiVersion: "2025-04-01-preview", SendTemperature: false), default);
        Assert.Equal("", created.SecretReference);
        Assert.Empty(rig.Secrets.Values);
        Assert.Equal(TokenLimitParameter.MaxCompletionTokens, created.TokenParameter);
        Assert.Null(created.Temperature);
        Assert.Equal("2025-04-01-preview", created.ApiVersion);
        Assert.Null(Assert.Single(rig.Client.Calls).Secret);
    }

    [Fact]
    public async Task A_keyless_local_server_needs_no_credential_and_a_listed_http_host_in_development()
    {
        var rig = new Rig(hosts: "localhost,llm-sim", allowHttp: true);
        var created = await rig.Service.CreateAsync(new("Ollama", AiProvider.OpenAICompatible, "http://localhost:11434/v1", "llama3.1:8b", AiAuthMode.None), default);
        Assert.Equal("http://localhost:11434/v1", created.Endpoint);
        Assert.Equal("", created.SecretReference);
        Assert.Equal("llama3.1:8b", created.Model);
    }

    private static object[] Case(string name, Func<AiConnectionDraft, AiConnectionDraft> tweak) => [name, tweak];

    public static IEnumerable<object[]> Rejected() =>
    [
        ["empty name", (Func<AiConnectionDraft, AiConnectionDraft>)(d => d with { Name = " " })],
        Case("long name", d => d with { Name = new string('n', 121) }),
        Case("bad model", d => d with { Model = "gpt 4; rm -rf" }),
        Case("empty model", d => d with { Model = "" }),
        Case("unknown provider", d => d with { Provider = (AiProvider)42 }),
        Case("unknown auth", d => d with { Auth = (AiAuthMode)42 }),
        Case("short key", d => d with { ApiKey = "short" }),
        Case("no key", d => d with { ApiKey = null }),
        Case("key with newline", d => d with { ApiKey = "sk-abcdefgh\nX-Injected: 1" }),
        Case("key with edge whitespace", d => d with { ApiKey = " sk-abcdefgh " }),
        Case("huge key", d => d with { ApiKey = new string('k', 4097) }),
        Case("key on keyless mode", d => d with { Auth = AiAuthMode.None }),
        Case("managed identity not available", d => d with { Provider = AiProvider.AzureOpenAI, Endpoint = "https://r.openai.azure.com", Auth = AiAuthMode.ManagedIdentity, ApiKey = null }),
        Case("api version on openai", d => d with { ApiVersion = "2024-10-21" }),
        Case("bad api version", d => d with { Provider = AiProvider.AzureOpenAI, Endpoint = "https://r.openai.azure.com", ApiVersion = "latest" }),
        Case("tokens too low", d => d with { MaxOutputTokens = 1 }),
        Case("tokens too high", d => d with { MaxOutputTokens = 1_000_000 }),
        Case("temperature", d => d with { Temperature = 3 }),
        Case("timeout", d => d with { TimeoutSeconds = 1 }),
        Case("input limit", d => d with { MaxInputBytes = 10 }),
        Case("host not allowed", d => d with { Endpoint = "https://evil.example/v1" }),
        Case("http not allowed", d => d with { Endpoint = "http://api.openai.com/v1" }),
        Case("credentials in url", d => d with { Endpoint = "https://user:pw@api.openai.com/v1" }),
        Case("query in url", d => d with { Endpoint = "https://api.openai.com/v1?x=1" }),
        Case("metadata address", d => d with { Endpoint = "https://169.254.169.254/latest" }),
        Case("not a url", d => d with { Endpoint = "api.openai.com" }),
    ];

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Invalid_input_is_refused_before_any_network_call_or_write(string name, Func<AiConnectionDraft, AiConnectionDraft> tweak)
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.CreateAsync(OpenAi(tweak), default));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.ValidateDraftAsync(OpenAi(tweak), default));
        Assert.Empty(rig.Client.Calls);
        Assert.Empty(rig.Secrets.Values);
        Assert.Empty(rig.Repository.Rows);
        _ = name;
    }

    [Theory]
    [InlineData(ChatFailure.Unauthorized, null, "Unauthorized")]
    [InlineData(ChatFailure.ModelNotFound, null, "ModelNotFound")]
    [InlineData(ChatFailure.RateLimited, null, "RateLimited")]
    [InlineData(ChatFailure.Timeout, null, "Timeout")]
    [InlineData(ChatFailure.Unavailable, null, "Unavailable")]
    [InlineData(ChatFailure.MalformedResponse, null, "MalformedResponse")]
    [InlineData(ChatFailure.BadRequest, "unsupported_parameter", "UnsupportedParameter")]
    [InlineData(ChatFailure.BadRequest, "unsupported_value", "UnsupportedParameter")]
    [InlineData(ChatFailure.BadRequest, "other", "BadRequest")]
    public async Task A_model_that_does_not_answer_the_ping_creates_nothing_and_names_the_reason(ChatFailure failure, string? providerCode, string expected)
    {
        var rig = new Rig();
        rig.Client.Fails(failure, providerCode);
        var thrown = await Assert.ThrowsAsync<AiConnectionException>(() => rig.Service.CreateAsync(OpenAi(), default));
        Assert.Equal(expected, thrown.Code);
        Assert.Empty(rig.Secrets.Values);
        Assert.Empty(rig.Repository.Rows);
        var validation = await rig.Service.ValidateDraftAsync(OpenAi(), default);
        Assert.True(validation.IsValid); // the next scripted answer is the default OK
    }

    [Fact]
    public async Task If_saving_fails_the_stored_key_is_removed_again()
    {
        var rig = new Rig();
        rig.Repository.FailSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.CreateAsync(OpenAi(), default));
        Assert.Empty(rig.Secrets.Values);
    }

    // ---- verify / rotate ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Verifying_records_the_outcome_and_a_failure_revokes_the_verified_status_but_keeps_the_consent_flag()
    {
        var rig = new Rig();
        var created = await rig.Service.CreateAsync(OpenAi(), default);
        await rig.Service.SetSourceConsentAsync(created.Id, true, "root", default);

        rig.Client.Fails(ChatFailure.RateLimited);
        var failed = (await rig.Service.VerifyAsync(created.Id, default))!;
        Assert.Equal("RateLimited", failed.VerificationCode);
        Assert.False(failed.IsVerified);
        Assert.Null(failed.VerifiedModel);
        Assert.True(failed.SendSourceContent);

        var recovered = (await rig.Service.VerifyAsync(created.Id, default))!;
        Assert.True(recovered.IsVerified);
        Assert.Null(await rig.Service.VerifyAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Verify_reports_a_host_that_is_no_longer_allowed_and_a_missing_credential_without_calling_the_model()
    {
        var rig = new Rig();
        var created = await rig.Service.CreateAsync(OpenAi(), default);
        var calls = rig.Client.Calls.Count;

        var stricter = new AiConnectionService(rig.Repository, rig.Secrets, rig.Client, AiEndpointPolicy.FromConfiguration("other.example", false), new FixedTime(Now));
        Assert.Equal("EndpointNotAllowed", (await stricter.VerifyAsync(created.Id, default))!.VerificationCode);

        rig.Secrets.Values.Clear();
        Assert.Equal("CredentialMissing", (await rig.Service.VerifyAsync(created.Id, default))!.VerificationCode);
        Assert.Equal(calls, rig.Client.Calls.Count);
    }

    [Fact]
    public async Task Rotating_tests_the_new_key_first_and_only_then_replaces_the_old_one()
    {
        var rig = new Rig();
        var created = await rig.Service.CreateAsync(OpenAi(), default);
        await rig.Service.SetSourceConsentAsync(created.Id, true, "root", default);

        rig.Client.Fails(ChatFailure.Unauthorized);
        var thrown = await Assert.ThrowsAsync<AiConnectionException>(() => rig.Service.RotateAsync(created.Id, "sk-rotated-0123456789", default));
        Assert.Equal("Unauthorized", thrown.Code);
        Assert.Equal(Key, rig.Secrets.Values[created.SecretReference]);

        var rotated = (await rig.Service.RotateAsync(created.Id, "sk-rotated-0123456789", default))!;
        Assert.Equal("sk-rotated-0123456789", rig.Secrets.Values[created.SecretReference]);
        Assert.Equal("sk-rotated-0123456789", rig.Client.Calls[^1].Secret);
        Assert.True(rotated.IsVerified);
        Assert.True(rotated.SendSourceContent);

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.RotateAsync(created.Id, "short", default));
        Assert.Null(await rig.Service.RotateAsync(Guid.NewGuid(), "sk-rotated-0123456789", default));
    }

    [Fact]
    public async Task A_connection_without_a_key_cannot_be_rotated()
    {
        var rig = new Rig(hosts: "localhost", allowHttp: true);
        var created = await rig.Service.CreateAsync(new("Local", AiProvider.OpenAICompatible, "http://localhost:11434/v1", "llama3", AiAuthMode.None), default);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.RotateAsync(created.Id, "sk-rotated-0123456789", default));
    }

    // ---- consent ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Source_consent_needs_a_verified_connection_and_records_who_and_when()
    {
        var rig = new Rig();
        var created = await rig.Service.CreateAsync(OpenAi(), default);

        var allowed = (await rig.Service.SetSourceConsentAsync(created.Id, true, "alice\u0007\n", default))!;
        Assert.True(allowed.SendSourceContent);
        Assert.Equal("alice", allowed.ConsentBy);
        Assert.Equal(Now, allowed.ConsentAtUtc);

        rig.Client.Fails(ChatFailure.Unauthorized);
        await rig.Service.VerifyAsync(created.Id, default);
        var thrown = await Assert.ThrowsAsync<AiConnectionException>(() => rig.Service.SetSourceConsentAsync(created.Id, true, "bob", default));
        Assert.Equal("NotVerified", thrown.Code);
        var revoked = (await rig.Service.SetSourceConsentAsync(created.Id, false, "bob", default))!;
        Assert.False(revoked.SendSourceContent);
        Assert.Equal("bob", revoked.ConsentBy);

        Assert.Equal("unknown", (await rig.Service.SetSourceConsentAsync(created.Id, false, "", default))!.ConsentBy);
        Assert.Equal(160, (await rig.Service.SetSourceConsentAsync(created.Id, false, new string('a', 500), default))!.ConsentBy!.Length);
        Assert.Null(await rig.Service.SetSourceConsentAsync(Guid.NewGuid(), true, "x", default));
    }

    // ---- use by a feature -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolving_for_use_rechecks_everything_that_can_change_after_creation()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<AiConnectionException>(() => rig.Service.ResolveAsync(Guid.NewGuid(), false, default));
        var created = await rig.Service.CreateAsync(OpenAi(), default);

        Assert.Equal("SourceConsentRequired", (await Assert.ThrowsAsync<AiConnectionException>(() => rig.Service.ResolveAsync(created.Id, true, default))).Code);
        var advisory = await rig.Service.ResolveAsync(created.Id, false, default);
        Assert.Equal(Key, advisory.Secret);
        Assert.Equal("gpt-4.1", advisory.Endpoint.Model);

        await rig.Service.SetSourceConsentAsync(created.Id, true, "root", default);
        Assert.True((await rig.Service.ResolveAsync(created.Id, true, default)).Connection.SendSourceContent);

        var stricter = new AiConnectionService(rig.Repository, rig.Secrets, rig.Client, AiEndpointPolicy.FromConfiguration("other.example", false), new FixedTime(Now));
        Assert.Equal("EndpointNotAllowed", (await Assert.ThrowsAsync<AiConnectionException>(() => stricter.ResolveAsync(created.Id, false, default))).Code);

        rig.Secrets.Values.Clear();
        Assert.Equal("CredentialMissing", (await Assert.ThrowsAsync<AiConnectionException>(() => rig.Service.ResolveAsync(created.Id, false, default))).Code);

        rig.Client.Fails(ChatFailure.Unauthorized);
        await rig.Service.VerifyAsync(created.Id, default);
        Assert.Equal("NotVerified", (await Assert.ThrowsAsync<AiConnectionException>(() => rig.Service.ResolveAsync(created.Id, false, default))).Code);
    }

    [Fact]
    public async Task Deleting_removes_the_key_and_the_record_and_listing_never_exposes_the_secret_reference()
    {
        var rig = new Rig();
        var created = await rig.Service.CreateAsync(OpenAi(), default);
        Assert.All(await rig.Service.ListAsync(default), c => Assert.Equal("", c.SecretReference));
        Assert.True(await rig.Service.DeleteAsync(created.Id, default));
        Assert.Empty(rig.Secrets.Values);
        Assert.Empty(rig.Repository.Rows);
        Assert.False(await rig.Service.DeleteAsync(created.Id, default));
    }

    // ---- policy ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Without_configuration_only_the_azure_hosts_are_allowed_and_http_needs_an_explicit_switch()
    {
        var policy = AiEndpointPolicy.FromConfiguration(null, false);
        Assert.Equal(AiHostDefaults.AzureHosts, policy.Hosts);
        Assert.Equal("https://r.openai.azure.com/openai/v1", policy.Normalize("https://r.openai.azure.com/openai/v1/"));
        Assert.Throws<ArgumentException>(() => policy.Normalize("https://api.openai.com/v1"));
        Assert.Throws<ArgumentException>(() => policy.Normalize("http://r.openai.azure.com"));
        Assert.Equal("http://r.openai.azure.com", AiEndpointPolicy.FromConfiguration(null, true).Normalize("http://r.openai.azure.com"));
        Assert.Equal(["a.example", "b.example"], AiEndpointPolicy.FromConfiguration("A.example; b.example", false).Hosts);
    }
}
