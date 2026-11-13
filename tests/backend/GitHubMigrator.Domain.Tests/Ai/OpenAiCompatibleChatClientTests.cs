using System.Net;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.Ai;

namespace GitHubMigrator.Domain.Tests.Ai;

internal sealed record SentRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body);

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();
    public List<SentRequest> Sent { get; } = [];

    public StubHandler Then(Func<HttpResponseMessage> response) { _script.Enqueue((_, _) => Task.FromResult(response())); return this; }
    public StubHandler Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) { _script.Enqueue(response); return this; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Sent.Add(new(request.Method, request.RequestUri!, headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        if (_script.Count == 0) throw new InvalidOperationException("No scripted response left.");
        return await _script.Dequeue()(request, ct);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public static HttpResponseMessage Completion(string content, string finish = "stop", string model = "gpt-sim") => Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new
        {
            id = "chatcmpl-1", @object = "chat.completion", model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content, refusal = (string?)null }, finish_reason = finish } },
            usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 }
        }));
}

internal sealed class FakeTokenSource(string? token = "mi-token") : IManagedIdentityTokenSource
{
    public Task<string> GetTokenAsync(CancellationToken ct) =>
        token is null ? throw new InvalidOperationException("no identity") : Task.FromResult(token);
}

public sealed class OpenAiCompatibleChatClientTests
{
    private const string Key = "sk-test-0123456789";
    private static readonly ChatRequest Hello = new([ChatMessage.System("be brief"), ChatMessage.User("hello")]);

    private static ChatEndpoint OpenAi(Func<ChatEndpoint, ChatEndpoint>? tweak = null) =>
        (tweak ?? (e => e))(new(AiProvider.OpenAICompatible, "https://api.openai.com/v1", "gpt-sim", AiAuthMode.ApiKey));

    private static ChatEndpoint Azure(string? apiVersion = null, AiAuthMode auth = AiAuthMode.ApiKey) =>
        new(AiProvider.AzureOpenAI, "https://res.openai.azure.com", "my-deploy", auth, apiVersion);

    private static (OpenAiCompatibleChatClient Client, StubHandler Handler, List<TimeSpan> Delays) Build(IManagedIdentityTokenSource? tokens = null, TimeProvider? clock = null)
    {
        var handler = new StubHandler();
        var delays = new List<TimeSpan>();
        var client = new OpenAiCompatibleChatClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, tokens,
            (span, _) => { delays.Add(span); return Task.CompletedTask; }, clock);
        return (client, handler, delays);
    }

    private static async Task<ChatModelException> FailureOf(Task<ChatResult> call) => await Assert.ThrowsAsync<ChatModelException>(() => call);

    // ---- request shape ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://llm.corp.example/api/v1/chat/completions", "https://llm.corp.example/api/v1/chat/completions")]
    [InlineData("https://llm.corp.example", "https://llm.corp.example/chat/completions")]
    public async Task The_compatible_route_is_the_base_url_plus_chat_completions(string baseUrl, string expected)
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Completion("hi"));
        await client.CompleteAsync(OpenAi(e => e with { BaseUrl = baseUrl }), Key, Hello, default);
        Assert.Equal(expected, handler.Sent.Single().Uri.ToString());
        Assert.Equal(HttpMethod.Post, handler.Sent.Single().Method);
    }

    [Fact]
    public async Task Azure_uses_the_v1_route_without_an_api_version_and_the_deployment_route_with_one()
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Completion("a")).Then(() => StubHandler.Completion("b"));
        await client.CompleteAsync(Azure(), Key, Hello, default);
        await client.CompleteAsync(Azure("2024-10-21") with { Model = "my deploy/x" }, Key, Hello, default);
        Assert.Equal("https://res.openai.azure.com/openai/v1/chat/completions", handler.Sent[0].Uri.ToString());
        Assert.Equal("https://res.openai.azure.com/openai/deployments/my%20deploy%2Fx/chat/completions?api-version=2024-10-21", handler.Sent[1].Uri.OriginalString);
    }

    [Fact]
    public async Task Credentials_are_sent_the_way_each_provider_expects_and_nowhere_else()
    {
        var (client, handler, _) = Build(new FakeTokenSource());
        for (var i = 0; i < 4; i++) handler.Then(() => StubHandler.Completion("ok"));
        await client.CompleteAsync(OpenAi(), Key, Hello, default);
        await client.CompleteAsync(Azure(), Key, Hello, default);
        await client.CompleteAsync(OpenAi(e => e with { Auth = AiAuthMode.None }), null, Hello, default);
        await client.CompleteAsync(Azure(auth: AiAuthMode.ManagedIdentity), null, Hello, default);

        Assert.Equal("Bearer " + Key, handler.Sent[0].Headers["Authorization"]);
        Assert.False(handler.Sent[0].Headers.ContainsKey("api-key"));
        Assert.Equal(Key, handler.Sent[1].Headers["api-key"]);
        Assert.False(handler.Sent[1].Headers.ContainsKey("Authorization"));
        Assert.False(handler.Sent[2].Headers.ContainsKey("Authorization") || handler.Sent[2].Headers.ContainsKey("api-key"));
        Assert.Equal("Bearer mi-token", handler.Sent[3].Headers["Authorization"]);
        Assert.All(handler.Sent, s => Assert.DoesNotContain(Key, s.Body + s.Uri));
    }

    [Fact]
    public async Task Misconfiguration_is_reported_before_any_request_is_made()
    {
        var (client, handler, _) = Build();
        Assert.Equal(ChatFailure.Misconfigured, (await FailureOf(client.CompleteAsync(OpenAi(), null, Hello, default))).Failure);
        Assert.Equal(ChatFailure.Misconfigured, (await FailureOf(client.CompleteAsync(OpenAi(e => e with { Auth = AiAuthMode.ManagedIdentity }), null, Hello, default))).Failure);
        Assert.Equal(ChatFailure.Misconfigured, (await FailureOf(client.CompleteAsync(OpenAi(e => e with { BaseUrl = "not a url" }), Key, Hello, default))).Failure);
        Assert.Equal(ChatFailure.Misconfigured, (await FailureOf(client.CompleteAsync(OpenAi(e => e with { BaseUrl = "https://user:pw@host/v1" }), Key, Hello, default))).Failure);
        Assert.Equal(ChatFailure.Misconfigured, (await FailureOf(client.CompleteAsync(OpenAi(e => e with { BaseUrl = "ftp://host/v1" }), Key, Hello, default))).Failure);
        Assert.Equal(ChatFailure.Misconfigured, (await FailureOf(client.CompleteAsync(OpenAi(e => e with { Model = " " }), Key, Hello, default))).Failure);
        Assert.Equal(ChatFailure.Misconfigured, (await FailureOf(client.CompleteAsync(Azure(auth: AiAuthMode.ManagedIdentity), null, Hello, default))).Failure); // no token source
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task A_managed_identity_that_cannot_get_a_token_is_an_authorization_failure()
    {
        var (client, handler, _) = Build(new FakeTokenSource(null));
        var failure = await FailureOf(client.CompleteAsync(Azure(auth: AiAuthMode.ManagedIdentity), null, Hello, default));
        Assert.Equal(ChatFailure.Unauthorized, failure.Failure);
        Assert.DoesNotContain("no identity", failure.Message);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task The_body_follows_the_endpoint_settings()
    {
        var (client, handler, _) = Build();
        for (var i = 0; i < 3; i++) handler.Then(() => StubHandler.Completion("ok"));
        await client.CompleteAsync(OpenAi(), Key, new([ChatMessage.User("x")], MaxOutputTokens: 100, Seed: 7, JsonObject: true), default);
        await client.CompleteAsync(OpenAi(e => e with { TokenParameter = TokenLimitParameter.MaxCompletionTokens, Temperature = null, MaxOutputTokens = 50 }), Key,
            new([ChatMessage.User("x")], MaxOutputTokens: 5000), default);
        await client.CompleteAsync(OpenAi(), Key, Hello, default);

        using var first = JsonDocument.Parse(handler.Sent[0].Body);
        var a = first.RootElement;
        Assert.Equal("gpt-sim", a.GetProperty("model").GetString());
        Assert.Equal(100, a.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, a.GetProperty("temperature").GetDouble());
        Assert.Equal(7, a.GetProperty("seed").GetInt32());
        Assert.Equal("json_object", a.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(a.GetProperty("stream").GetBoolean());
        Assert.Equal("user", a.GetProperty("messages")[0].GetProperty("role").GetString());

        using var second = JsonDocument.Parse(handler.Sent[1].Body);
        var b = second.RootElement;
        Assert.Equal(50, b.GetProperty("max_completion_tokens").GetInt32()); // capped to the connection's limit
        Assert.False(b.TryGetProperty("max_tokens", out _));
        Assert.False(b.TryGetProperty("temperature", out _));
        Assert.False(b.TryGetProperty("seed", out _));
        Assert.False(b.TryGetProperty("response_format", out _));

        using var third = JsonDocument.Parse(handler.Sent[2].Body);
        Assert.Equal(2, third.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal(4096, third.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Oversized_or_invalid_input_never_leaves_the_process()
    {
        var (client, handler, _) = Build();
        var big = new ChatRequest([ChatMessage.User(new string('x', 1000))]);
        Assert.Equal(ChatFailure.InputTooLarge, (await FailureOf(client.CompleteAsync(OpenAi(e => e with { MaxInputBytes = 999 }), Key, big, default))).Failure);
        Assert.Equal(ChatFailure.BadRequest, (await FailureOf(client.CompleteAsync(OpenAi(), Key, new([]), default))).Failure);
        Assert.Equal(ChatFailure.BadRequest, (await FailureOf(client.CompleteAsync(OpenAi(), Key, new([new("tool", "x")]), default))).Failure);
        Assert.Empty(handler.Sent);
    }

    // ---- successful answers ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_completion_returns_content_finish_reason_usage_and_model()
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Completion("hello there", model: "gpt-sim-2"));
        var result = await client.CompleteAsync(OpenAi(), Key, Hello, default);
        Assert.Equal("hello there", result.Content);
        Assert.Equal("stop", result.FinishReason);
        Assert.False(result.Truncated);
        Assert.Equal(new ChatUsage(11, 7), result.Usage);
        Assert.Equal("gpt-sim-2", result.Model);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task A_truncated_answer_is_returned_and_flagged_not_thrown()
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Completion("name: x\non:", "length"));
        var result = await client.CompleteAsync(OpenAi(), Key, Hello, default);
        Assert.True(result.Truncated);
        Assert.Equal("name: x\non:", result.Content);
    }

    // ---- provider errors ------------------------------------------------------------------------------------------

    public static IEnumerable<object?[]> Errors() =>
    [
        [401, """{"error":{"message":"Incorrect API key provided: sk-test-0123456789","type":"invalid_request_error","code":"invalid_api_key"}}""", ChatFailure.Unauthorized, "invalid_api_key"],
        [403, """{"error":{"message":"forbidden"}}""", ChatFailure.Unauthorized, null],
        [401, """{"error":{"code":"401","message":"Access denied due to invalid subscription key"}}""", ChatFailure.Unauthorized, "401"],
        [404, """{"error":{"message":"The model `x` does not exist","type":"invalid_request_error","code":"model_not_found"}}""", ChatFailure.ModelNotFound, "model_not_found"],
        [404, """{"error":{"code":"DeploymentNotFound","message":"The API deployment for this resource does not exist."}}""", ChatFailure.ModelNotFound, "DeploymentNotFound"],
        [400, """{"error":{"message":"too long","code":"context_length_exceeded"}}""", ChatFailure.ContextLengthExceeded, "context_length_exceeded"],
        [400, """{"error":{"message":"This model's maximum context length is 8192 tokens."}}""", ChatFailure.ContextLengthExceeded, null],
        [413, "", ChatFailure.ContextLengthExceeded, null],
        [400, """{"error":{"code":"content_filter","message":"filtered","innererror":{"code":"ResponsibleAIPolicyViolation"}}}""", ChatFailure.ContentFiltered, "content_filter"],
        [400, """{"error":{"code":"BadRequest","message":"x","innererror":{"code":"ResponsibleAIPolicyViolation"}}}""", ChatFailure.ContentFiltered, "BadRequest"],
        [400, """{"error":{"message":"'messages' must contain the word 'json'","type":"invalid_request_error"}}""", ChatFailure.BadRequest, "invalid_request_error"],
        [422, "{}", ChatFailure.BadRequest, null],
        [408, "", ChatFailure.Timeout, null],
        [500, """{"error":{"message":"The server had an error"}}""", ChatFailure.Unavailable, null],
        [501, "", ChatFailure.Unavailable, null],
    ];

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Provider_errors_map_to_stable_failures_without_retrying_non_transient_ones(int status, string body, ChatFailure expected, string? providerCode)
    {
        var (client, handler, delays) = Build();
        handler.Then(() => StubHandler.Json((HttpStatusCode)status, body));
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(expected, failure.Failure);
        Assert.Equal(status, failure.HttpStatus);
        Assert.Equal(providerCode, failure.ProviderCode);
        Assert.Single(handler.Sent);
        Assert.Empty(delays);
        Assert.DoesNotContain(Key, failure.Message);
    }

    [Fact]
    public async Task The_provider_body_is_never_copied_into_the_exception_and_odd_codes_are_dropped()
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"your prompt said: TOP-SECRET-PROMPT-TEXT","type":"x y\nz","code":"<script>alert(1)</script>"}}"""));
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.DoesNotContain("TOP-SECRET", failure.Message);
        Assert.Null(failure.ProviderCode);
    }

    [Fact]
    public async Task A_redirect_is_an_error_and_never_followed()
    {
        var (client, handler, _) = Build();
        handler.Then(() => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://evil.example/steal"); return r; });
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.Unavailable, failure.Failure);
        Assert.Equal(302, failure.HttpStatus);
        Assert.Single(handler.Sent);
    }

    // ---- retries --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rate_limits_are_retried_honouring_Retry_After_and_then_succeed()
    {
        var (client, handler, delays) = Build();
        handler.Then(() => StubHandler.Json((HttpStatusCode)429, """{"error":{"code":"rate_limit_exceeded"}}""", ("Retry-After", "2")))
            .Then(() => StubHandler.Json((HttpStatusCode)429, "{}", ("Retry-After", "5")))
            .Then(() => StubHandler.Completion("finally"));
        var result = await client.CompleteAsync(OpenAi(), Key, Hello, default);
        Assert.Equal("finally", result.Content);
        Assert.Equal(3, result.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public async Task Without_Retry_After_the_wait_doubles_and_retries_are_bounded()
    {
        var (client, handler, delays) = Build();
        for (var i = 0; i < 3; i++) handler.Then(() => StubHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.Unavailable, failure.Failure);
        Assert.Equal(3, failure.Attempts);
        Assert.Equal(3, handler.Sent.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);

        var (noRetry, noRetryHandler, _) = Build();
        noRetryHandler.Then(() => StubHandler.Json((HttpStatusCode)429, "{}"));
        var once = await FailureOf(noRetry.CompleteAsync(OpenAi(e => e with { MaxRetries = 0 }), Key, Hello, default));
        Assert.Equal(ChatFailure.RateLimited, once.Failure);
        Assert.Equal(1, once.Attempts);
    }

    [Fact]
    public async Task A_provider_that_asks_for_a_long_wait_is_reported_instead_of_waited_for()
    {
        var (client, handler, delays) = Build();
        handler.Then(() => StubHandler.Json((HttpStatusCode)429, "{}", ("Retry-After", "120")));
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.RateLimited, failure.Failure);
        Assert.Equal(TimeSpan.FromSeconds(120), failure.RetryAfter);
        Assert.Single(handler.Sent);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Retry_After_may_be_an_http_date_and_exhausted_quota_is_not_retried()
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var (client, handler, delays) = Build(clock: new FixedClock(now));
        handler.Then(() => StubHandler.Json((HttpStatusCode)429, "{}", ("Retry-After", now.AddSeconds(7).ToString("R"))))
            .Then(() => StubHandler.Completion("ok"));
        await client.CompleteAsync(OpenAi(), Key, Hello, default);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);

        var (quota, quotaHandler, quotaDelays) = Build();
        quotaHandler.Then(() => StubHandler.Json((HttpStatusCode)429, """{"error":{"code":"insufficient_quota"}}"""));
        var failure = await FailureOf(quota.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.RateLimited, failure.Failure);
        Assert.Equal("insufficient_quota", failure.ProviderCode);
        Assert.Empty(quotaDelays);
    }

    [Fact]
    public async Task Connection_errors_are_retried_but_timeouts_and_server_errors_are_not()
    {
        var (client, handler, delays) = Build();
        handler.Then((_, _) => throw new HttpRequestException("connection refused 10.0.0.5")).Then(() => StubHandler.Completion("ok"));
        Assert.Equal(2, (await client.CompleteAsync(OpenAi(), Key, Hello, default)).Attempts);
        Assert.Single(delays);

        var (serverError, serverHandler, _) = Build();
        serverHandler.Then(() => StubHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        Assert.Equal(1, (await FailureOf(serverError.CompleteAsync(OpenAi(), Key, Hello, default))).Attempts);

        var (unreachable, unreachableHandler, _) = Build();
        for (var i = 0; i < 3; i++) unreachableHandler.Then((_, _) => throw new HttpRequestException("secret-host.internal refused"));
        var failure = await FailureOf(unreachable.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.Unavailable, failure.Failure);
        Assert.DoesNotContain("secret-host", failure.Message);
    }

    // ---- time ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_attempt_that_exceeds_the_timeout_fails_with_Timeout_and_is_not_retried()
    {
        var (client, handler, delays) = Build();
        handler.Then(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return StubHandler.Completion("never"); });
        var failure = await FailureOf(client.CompleteAsync(OpenAi(e => e with { Timeout = TimeSpan.FromMilliseconds(80) }), Key, Hello, default));
        Assert.Equal(ChatFailure.Timeout, failure.Failure);
        Assert.Single(handler.Sent);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation_not_as_a_model_failure()
    {
        var (client, handler, _) = Build();
        handler.Then(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return StubHandler.Completion("never"); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(OpenAi(), Key, Hello, cts.Token));
    }

    // ---- bad 200s ---------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> BadSuccesses() =>
    [
        ["empty choices", """{"choices":[]}""", ChatFailure.MalformedResponse],
        ["no choices", """{"id":"x"}""", ChatFailure.MalformedResponse],
        ["array root", "[]", ChatFailure.MalformedResponse],
        ["not json", "plain text", ChatFailure.MalformedResponse],
        ["cut mid-way", """{"choices":[{"message":{"content":"na""", ChatFailure.MalformedResponse],
        ["no message", """{"choices":[{"finish_reason":"stop"}]}""", ChatFailure.MalformedResponse],
        ["null content", """{"choices":[{"message":{"content":null},"finish_reason":"stop"}]}""", ChatFailure.MalformedResponse],
        ["empty content", """{"choices":[{"message":{"content":""},"finish_reason":"stop"}]}""", ChatFailure.MalformedResponse],
        ["refusal", """{"choices":[{"message":{"content":null,"refusal":"I can't help with that"},"finish_reason":"stop"}]}""", ChatFailure.Refused],
        ["content filter", """{"choices":[{"message":{"content":"partial"},"finish_reason":"content_filter"}]}""", ChatFailure.ContentFiltered],
    ];

    [Theory]
    [MemberData(nameof(BadSuccesses))]
    public async Task A_200_that_is_not_a_usable_answer_is_a_named_failure(string name, string body, ChatFailure expected)
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Json(HttpStatusCode.OK, body));
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(expected, failure.Failure);
        Assert.False(failure.Retryable);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task A_proxy_error_page_served_with_200_is_not_parsed_as_an_answer()
    {
        var (client, handler, _) = Build();
        handler.Then(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in to continue</html>", Encoding.UTF8, "text/html") });
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.MalformedResponse, failure.Failure);
        Assert.Contains("not JSON", failure.Message);
    }

    [Fact]
    public async Task A_response_larger_than_the_cap_is_refused_without_buffering_it_all()
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"" + new string('a', OpenAiCompatibleChatClient.MaxResponseBytes + 10) + "\"}}]}"));
        var failure = await FailureOf(client.CompleteAsync(OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.MalformedResponse, failure.Failure);
        Assert.Contains("larger than allowed", failure.Message);
    }

    [Fact]
    public async Task A_missing_usage_block_is_tolerated_because_some_servers_omit_it()
    {
        var (client, handler, _) = Build();
        handler.Then(() => StubHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}]}"""));
        var result = await client.CompleteAsync(OpenAi(), Key, Hello, default);
        Assert.Equal("hi", result.Content);
        Assert.Null(result.Usage);
        Assert.Null(result.Model);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
