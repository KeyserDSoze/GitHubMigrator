using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using GitHubMigrator.Infrastructure.Ai;
using LlmSim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using ChatMessage = GitHubMigrator.Business.ChatMessage;
using ChatRequest = GitHubMigrator.Business.ChatRequest;

namespace GitHubMigrator.Domain.Tests.Ai;

// The real client against the real simulator, in-process on a random loopback port. These tests prove what the
// product does on the wire; they say nothing about how a real provider behaves (confirm that online).
internal sealed class SimRig : IAsyncDisposable
{
    public const string Key = "llm-sim-key";
    private readonly WebApplication _app;
    private readonly HttpClient _control = new();
    public string Origin { get; }
    public List<TimeSpan> Delays { get; } = [];

    private SimRig(WebApplication app, string origin)
    {
        _app = app; Origin = origin;
        _control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
    }

    public static async Task<SimRig> StartAsync(bool requireKey = true)
    {
        var app = await LlmSimApp.StartAsync(new LlmSimOptions("http://127.0.0.1:0", RequireKey: requireKey));
        return new(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    public OpenAiCompatibleChatClient Client() => new(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan }, null,
        (span, _) => { Delays.Add(span); return Task.CompletedTask; });

    public ChatEndpoint OpenAi(string model = "gpt-sim", AiAuthMode auth = AiAuthMode.ApiKey) =>
        new(AiProvider.OpenAICompatible, Origin + "/v1", model, auth);

    public ChatEndpoint Azure(string? apiVersion = null, string model = "gpt-sim") =>
        new(AiProvider.AzureOpenAI, Origin, model, AiAuthMode.ApiKey, apiVersion, TokenParameter: TokenLimitParameter.MaxCompletionTokens);

    public async Task ScriptAsync(params object[] replies)
    {
        var response = await _control.PostAsync(Origin + "/_sim/script",
            new StringContent(JsonSerializer.Serialize(new { responses = replies }), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement[]> LogAsync()
    {
        using var document = JsonDocument.Parse(await _control.GetStringAsync(Origin + "/_sim/requests"));
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _control.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class LlmSimWireTests
{
    private static readonly ChatRequest Hello = new([ChatMessage.System("be brief"), ChatMessage.User("hello")]);
    private const string Key = SimRig.Key;

    private static async Task<ChatModelException> FailureOf(Task<ChatResult> call) => await Assert.ThrowsAsync<ChatModelException>(() => call);

    // ---- routes and credentials -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_client_talks_to_the_openai_route_the_azure_v1_route_and_the_azure_deployment_route()
    {
        await using var sim = await SimRig.StartAsync();
        var client = sim.Client();
        foreach (var (name, endpoint) in new[]
        {
            ("openai", sim.OpenAi()), ("azure v1", sim.Azure()), ("azure classic", sim.Azure("2024-10-21"))
        })
        {
            var result = await client.CompleteAsync(endpoint, Key, Hello, default);
            Assert.Contains("simulated answer", result.Content);
            Assert.Equal("stop", result.FinishReason);
            Assert.Equal("gpt-sim", result.Model);
            Assert.NotNull(result.Usage);
            Assert.True(result.Usage!.PromptTokens > 0, name);
        }
        var paths = (await sim.LogAsync()).Select(e => e.GetProperty("path").GetString()).ToArray();
        Assert.Equal(["/v1/chat/completions", "/openai/v1/chat/completions", "/openai/deployments/gpt-sim/chat/completions"], paths);
    }

    [Fact]
    public async Task A_wrong_key_an_unknown_model_and_an_unknown_deployment_are_named_failures()
    {
        await using var sim = await SimRig.StartAsync();
        var client = sim.Client();
        Assert.Equal(ChatFailure.Unauthorized, (await FailureOf(client.CompleteAsync(sim.OpenAi(), "wrong-key-0123456", Hello, default))).Failure);
        Assert.Equal(ChatFailure.Unauthorized, (await FailureOf(client.CompleteAsync(sim.Azure(), "wrong-key-0123456", Hello, default))).Failure);
        Assert.Equal(ChatFailure.ModelNotFound, (await FailureOf(client.CompleteAsync(sim.OpenAi("nope"), Key, Hello, default))).Failure);
        Assert.Equal(ChatFailure.ModelNotFound, (await FailureOf(client.CompleteAsync(sim.Azure("2024-10-21", "nope"), Key, Hello, default))).Failure);
        Assert.All(sim.Delays, _ => Assert.Fail("non-transient errors must not be retried"));
    }

    [Fact]
    public async Task A_keyless_local_server_is_reached_without_any_credential()
    {
        await using var sim = await SimRig.StartAsync(requireKey: false);
        var result = await sim.Client().CompleteAsync(sim.OpenAi(auth: AiAuthMode.None), null, Hello, default);
        Assert.Contains("simulated answer", result.Content);
        var entry = Assert.Single(await sim.LogAsync());
        Assert.False(entry.GetProperty("headers").EnumerateObject().Any(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)));
    }

    // ---- faults ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rate_limited_call_is_retried_after_the_wait_the_server_asked_for()
    {
        await using var sim = await SimRig.StartAsync();
        await sim.ScriptAsync(new { status = 429, retryAfterSeconds = 1, repeat = 2 });
        var result = await sim.Client().CompleteAsync(sim.OpenAi(), Key, Hello, default);
        Assert.Equal(3, result.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], sim.Delays);
        Assert.Equal(3, (await sim.LogAsync()).Length);
    }

    [Fact]
    public async Task Rate_limits_that_never_clear_or_ask_for_too_long_are_reported()
    {
        await using var sim = await SimRig.StartAsync();
        await sim.ScriptAsync(new { status = 429, retryAfterSeconds = 1, repeat = 5 });
        var exhausted = await FailureOf(sim.Client().CompleteAsync(sim.OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.RateLimited, exhausted.Failure);
        Assert.Equal(3, exhausted.Attempts);

        await using var other = await SimRig.StartAsync();
        await other.ScriptAsync(new { status = 429, retryAfterSeconds = 120 });
        var tooLong = await FailureOf(other.Client().CompleteAsync(other.OpenAi(), Key, Hello, default));
        Assert.Equal(TimeSpan.FromSeconds(120), tooLong.RetryAfter);
        Assert.Single(await other.LogAsync());
    }

    [Fact]
    public async Task Transient_server_errors_are_retried_and_a_plain_500_is_not()
    {
        await using var sim = await SimRig.StartAsync();
        await sim.ScriptAsync(new { status = 503 }, new { status = 502 });
        Assert.Equal(3, (await sim.Client().CompleteAsync(sim.OpenAi(), Key, Hello, default)).Attempts);

        await using var broken = await SimRig.StartAsync();
        await broken.ScriptAsync(new { status = 500 });
        var failure = await FailureOf(broken.Client().CompleteAsync(broken.OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.Unavailable, failure.Failure);
        Assert.Equal(1, failure.Attempts);
    }

    [Fact]
    public async Task Context_length_and_content_filter_rejections_are_named()
    {
        await using var sim = await SimRig.StartAsync();
        await sim.ScriptAsync(new { status = 400, contextLengthExceeded = true }, new { contentFilter = true });
        var client = sim.Client();
        Assert.Equal(ChatFailure.ContextLengthExceeded, (await FailureOf(client.CompleteAsync(sim.OpenAi(), Key, Hello, default))).Failure);
        Assert.Equal(ChatFailure.ContentFiltered, (await FailureOf(client.CompleteAsync(sim.Azure("2024-10-21"), Key, Hello, default))).Failure);
    }

    [Fact]
    public async Task A_model_that_stalls_hits_the_attempt_timeout_and_a_slow_one_inside_it_still_answers()
    {
        await using var sim = await SimRig.StartAsync();
        await sim.ScriptAsync(new { hangMs = 10_000 });
        var started = DateTime.UtcNow;
        var failure = await FailureOf(sim.Client().CompleteAsync(sim.OpenAi() with { Timeout = TimeSpan.FromMilliseconds(400) }, Key, Hello, default));
        Assert.Equal(ChatFailure.Timeout, failure.Failure);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8));

        await sim.ScriptAsync(new { delayMs = 150 });
        var slow = await sim.Client().CompleteAsync(sim.OpenAi() with { Timeout = TimeSpan.FromSeconds(10) }, Key, Hello, default);
        Assert.Contains("simulated answer", slow.Content);
    }

    [Fact]
    public async Task Answers_that_are_not_a_usable_completion_are_malformed_responses()
    {
        await using var sim = await SimRig.StartAsync();
        await sim.ScriptAsync(new { malformedJson = true }, new { emptyChoices = true },
            new { contentType = "text/html", content = "<html>Sign in to continue</html>" });
        var client = sim.Client();
        for (var i = 0; i < 3; i++)
            Assert.Equal(ChatFailure.MalformedResponse, (await FailureOf(client.CompleteAsync(sim.OpenAi(), Key, Hello, default))).Failure);

        await using var proxy = await SimRig.StartAsync();
        await proxy.ScriptAsync(new { status = 502, contentType = "text/html", content = "<html>Bad gateway</html>", repeat = 3 });
        var failure = await FailureOf(proxy.Client().CompleteAsync(proxy.OpenAi(), Key, Hello, default));
        Assert.Equal(ChatFailure.Unavailable, failure.Failure);
        Assert.Equal(3, failure.Attempts);
    }

    [Fact]
    public async Task An_answer_cut_off_by_the_token_limit_is_returned_flagged_as_truncated()
    {
        await using var sim = await SimRig.StartAsync();
        var result = await sim.Client().CompleteAsync(sim.OpenAi(), Key,
            new([ChatMessage.User("[[llmsim:good:minimal]]")], MaxOutputTokens: 16), default);
        Assert.True(result.Truncated);
        Assert.True(result.Content.Length <= 16 * 4 + 4);
        var extracted = ModelOutputExtractor.Extract(result.Content);
        var report = await new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate()])
            .RunAsync(extracted.Yaml ?? result.Content, Plan, default);
        Assert.False(report.Approvable);
    }

    // ---- what the model is sent -----------------------------------------------------------------------------------

    [Fact]
    public async Task After_redaction_no_credential_reaches_the_model_and_the_api_key_is_only_in_the_masked_header()
    {
        const string source = """
            variables:
              DB_PASSWORD: Sup3rS3cretValue!
            steps:
              - script: deploy.sh
                env:
                  AWS_ACCESS_KEY_ID: AKIAIOSFODNN7EXAMPLE
                  GH_TOKEN: ghp_0123456789abcdefghijklmnopqrstuvwxyzAB
                  FEED: https://build:S3cr3tPass@nuget.corp.example/v3/index.json
                  OK: $(SafeReference)
            """;
        var redacted = SourceRedactor.Redact(source);
        Assert.True(redacted.SafeToSend);
        await using var sim = await SimRig.StartAsync();
        await sim.Client().CompleteAsync(sim.OpenAi(), Key, new([ChatMessage.System("convert"), ChatMessage.User(redacted.Text)]), default);

        var entry = Assert.Single(await sim.LogAsync());
        var body = entry.GetProperty("body").GetString()!;
        foreach (var secret in new[] { "Sup3rS3cretValue!", "AKIAIOSFODNN7EXAMPLE", "ghp_0123456789", "S3cr3tPass" })
            Assert.DoesNotContain(secret, body);
        // the raw JSON escapes '<' as \u003C; what the model reads is the decoded message
        var prompt = entry.GetProperty("json").GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("<REDACTED:SENSITIVE_LITERAL:1>", prompt);
        Assert.Contains("<REDACTED:AWS_ACCESS_KEY_ID:1>", prompt);
        Assert.Contains("<REDACTED:GITHUB_TOKEN:1>", prompt);
        Assert.Contains("$(SafeReference)", prompt);
        foreach (var secret in new[] { "Sup3rS3cretValue!", "AKIAIOSFODNN7EXAMPLE", "ghp_0123456789", "S3cr3tPass" })
            Assert.DoesNotContain(secret, prompt);
        Assert.DoesNotContain(Key, body);
        var headers = entry.GetProperty("headers");
        Assert.Equal("***", headers.EnumerateObject().Single(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value.GetString());
        Assert.DoesNotContain(Key, headers.GetRawText());
        Assert.False(entry.GetProperty("streamed").GetBoolean());
    }

    [Fact]
    public async Task The_request_carries_the_connection_settings_and_asks_for_one_non_streamed_answer()
    {
        await using var sim = await SimRig.StartAsync();
        await sim.Client().CompleteAsync(sim.OpenAi() with { MaxOutputTokens = 321, Temperature = 0 }, Key, Hello, default);
        var json = Assert.Single(await sim.LogAsync()).GetProperty("json");
        Assert.Equal("gpt-sim", json.GetProperty("model").GetString());
        Assert.Equal(321, json.GetProperty("max_tokens").GetInt32());
        Assert.False(json.GetProperty("stream").GetBoolean());
        Assert.Equal(2, json.GetProperty("messages").GetArrayLength());
    }

    // ---- hostile answers through the gates ----------------------------------------------------------------------------

    private static readonly WorkflowPlanContext Plan = WorkflowPlanContext.Of(["NUGET_API_KEY"], []);

    private static async Task<(ExtractedWorkflow Extracted, WorkflowGateReport? Report)> ConvertAsync(string marker, WorkflowGatePipeline? pipeline = null)
    {
        await using var sim = await SimRig.StartAsync();
        var result = await sim.Client().CompleteAsync(sim.OpenAi() with { MaxOutputTokens = 100_000 }, Key,
            new([ChatMessage.System("convert"), ChatMessage.User("source pipeline\n" + marker)], MaxOutputTokens: 100_000), default);
        var extracted = ModelOutputExtractor.Extract(result.Content);
        if (!extracted.Ok) return (extracted, null);
        pipeline ??= new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate()]);
        return (extracted, await pipeline.RunAsync(extracted.Yaml!, Plan, default));
    }

    public static IEnumerable<object[]> Hostile() =>
    [
        ["pipe-to-shell", "REMOTE_SCRIPT_EXECUTION"],
        ["pull-request-target", "DANGEROUS_TRIGGER"],
        ["unpinned-main", "UNPINNED_ACTION"],
        ["literal-secret", "LITERAL_SECRET"],
        ["secret-exfil", "SECRET_TO_UNLISTED_HOST"],
        ["secret-exfil", "SECRET_IN_RUN_SCRIPT"],
        ["secret-exfil", "SECRET_NOT_IN_PLAN"],
        ["script-injection", "UNTRUSTED_CONTEXT_IN_SCRIPT"],
        ["broken-yaml", "YAML_PARSE_ERROR"],
        ["wrong-schema", "NOT_A_WORKFLOW"],
        ["huge", "TOO_MANY_STEPS"],
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task Every_hostile_answer_of_the_library_is_stopped_by_the_gate_that_is_meant_to_catch_it(string name, string code)
    {
        var (extracted, report) = await ConvertAsync($"[[llmsim:hostile:{name}]]");
        Assert.True(extracted.Ok, name);
        Assert.NotNull(report);
        Assert.True(report!.Has(code), $"{name}: expected {code}, got [{string.Join(", ", report.Findings.Select(f => f.Code).Distinct())}]");
        Assert.False(report.Approvable);
    }

    [Fact]
    public async Task A_chatty_markdown_answer_is_unwrapped_and_its_prose_kept_as_notes_never_as_workflow()
    {
        var (extracted, report) = await ConvertAsync("[[llmsim:hostile:markdown-wrapped]]");
        Assert.True(extracted.Ok);
        Assert.StartsWith("name: CI", extracted.Yaml);
        Assert.DoesNotContain("```", extracted.Yaml);
        Assert.Contains("Happy to help", extracted.Notes);
        Assert.DoesNotContain("Happy to help", extracted.Yaml);
        Assert.Equal(["TRIGGERS_NOT_DISABLED"], report!.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code).Distinct());
    }

    [Fact]
    public async Task Injected_instructions_in_the_answer_change_nothing_about_what_the_gates_decide()
    {
        var (injected, injectedReport) = await ConvertAsync("[[llmsim:hostile:prompt-injection-echo]]");
        var (plain, plainReport) = await ConvertAsync("[[llmsim:hostile:markdown-wrapped]]");
        Assert.Contains("Ignore previous instructions", injected.Yaml);
        Assert.Equal(
            plainReport!.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code).Distinct().Order(),
            injectedReport!.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code).Distinct().Order());
        Assert.False(injectedReport.Approvable);
        Assert.NotNull(plain.Notes);
    }

    [Fact]
    public async Task The_correct_answer_is_held_back_only_by_its_original_triggers_until_a_separate_approval_enables_them()
    {
        var (_, report) = await ConvertAsync("[[llmsim:good:minimal]]");
        Assert.Equal(["TRIGGERS_NOT_DISABLED"], report!.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code).Distinct());

        var relaxed = new WorkflowGatePolicy { RequireDisabledTriggers = false };
        var (_, enabled) = await ConvertAsync("[[llmsim:good:minimal]]", new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate()], relaxed));
        Assert.False(enabled!.HasBlockers);
    }

    [RequiresToolsFact]
    public async Task With_the_real_analysers_the_correct_answer_is_approvable_once_triggers_are_allowed_and_no_hostile_one_ever_is()
    {
        var relaxed = new WorkflowGatePolicy { RequireDisabledTriggers = false };
        var (_, good) = await ConvertAsync("[[llmsim:good:minimal]]", WorkflowTools.RealPipeline(relaxed));
        Assert.True(good!.Approvable, string.Join("; ", good.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code + ":" + f.Message)));

        foreach (var name in new[] { "pipe-to-shell", "pull-request-target", "unpinned-main", "literal-secret", "secret-exfil", "script-injection", "markdown-wrapped", "prompt-injection-echo" })
        {
            var (_, report) = await ConvertAsync($"[[llmsim:hostile:{name}]]", WorkflowTools.RealPipeline());
            Assert.False(report!.Approvable, name);
        }
    }
}
