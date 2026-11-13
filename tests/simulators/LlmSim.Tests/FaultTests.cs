using System.Diagnostics;
using System.Text.Json;
using Xunit;
using static LlmSim.Tests.SimHost;

namespace LlmSim.Tests;

public sealed class FaultTests(LlmSimFixture fixture) : SimTests(fixture), IClassFixture<LlmSimFixture>
{
    // status, error.type, error.code (null = JSON null), message prefix
    public static TheoryData<int, string, string?, string> StatusBodies => new()
    {
        { 400, "invalid_request_error", null, "We could not process your request" },
        { 401, "invalid_request_error", "invalid_api_key", "Incorrect API key provided: llm" },
        { 403, "invalid_request_error", "model_not_found", "Project `proj_llm_sim` does not have access to model `gpt-sim`" },
        { 404, "invalid_request_error", "model_not_found", "The model `gpt-sim` does not exist or you do not have access to it." },
        { 408, "server_error", "request_timeout", "Request timed out." },
        { 413, "invalid_request_error", "request_too_large", "Request body too large." },
        { 429, "requests", "rate_limit_exceeded", "Rate limit reached for gpt-sim" },
        { 500, "server_error", null, "The server had an error while processing your request. Sorry about that!" },
        { 502, "server_error", null, "Bad gateway." },
        { 503, "server_error", null, "That model is currently overloaded with other requests." },
    };

    [Theory]
    [MemberData(nameof(StatusBodies))]
    public async Task A_scripted_status_returns_a_realistic_openai_error(int status, string type, string? code, string message)
    {
        await Sim.ScriptAsync(new { status });
        var response = await Sim.Chat(Req("hi"));
        Assert.Equal(status, response.Status);
        Assert.StartsWith("application/json", response.ContentType);
        Assert.Equal(["message", "type", "param", "code"], response.Error.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.StartsWith(message, response.Error.GetProperty("message").GetString());
        Assert.Equal(type, response.Error.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, response.Error.GetProperty("param").ValueKind);
        if (code is null) Assert.Equal(JsonValueKind.Null, response.Error.GetProperty("code").ValueKind);
        else Assert.Equal(code, response.Error.GetProperty("code").GetString());
        Assert.DoesNotContain(Key, response.Text);
    }

    [Fact]
    public async Task A_429_carries_retry_after_in_seconds_and_milliseconds()
    {
        await Sim.ScriptAsync(new { status = 429, retryAfterSeconds = 7 }, new { status = 429 });
        var limited = await Sim.Chat(Req("hi"));
        Assert.Equal(429, limited.Status);
        Assert.Equal("7", limited.Header("Retry-After"));
        Assert.Equal("7000", limited.Header("retry-after-ms"));
        Assert.Equal("rate_limit_exceeded", limited.Error.GetProperty("code").GetString());
        Assert.Contains("Please try again in 7s.", limited.Error.GetProperty("message").GetString());
        Assert.Equal("1", (await Sim.Chat(Req("hi"))).Header("Retry-After"));
    }

    [Fact]
    public async Task Retry_after_can_accompany_other_statuses_and_is_absent_otherwise()
    {
        await Sim.ScriptAsync(new { status = 503, retryAfterSeconds = 30 }, new { status = 500 });
        Assert.Equal("30", (await Sim.Chat(Req("hi"))).Header("Retry-After"));
        Assert.Null((await Sim.Chat(Req("hi"))).Header("Retry-After"));
    }

    [Fact]
    public async Task A_context_length_error_has_its_own_code_and_param()
    {
        await Sim.ScriptAsync(new { status = 400, contextLengthExceeded = true });
        var response = await Sim.Chat(Req(new string('x', 40_000)));
        Assert.Equal(400, response.Status);
        Assert.Equal("context_length_exceeded", response.Error.GetProperty("code").GetString());
        Assert.Equal("messages", response.Error.GetProperty("param").GetString());
        Assert.Equal("invalid_request_error", response.Error.GetProperty("type").GetString());
        Assert.StartsWith("This model's maximum context length is 8192 tokens. However, your messages resulted in 10000 tokens.", response.Error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_scripted_401_uses_the_azure_body_on_azure_routes()
    {
        await Sim.ScriptAsync(new { status = 401 }, new { status = 401 });
        var openai = await Sim.Chat(Req("hi"));
        Assert.Equal("invalid_api_key", openai.Error.GetProperty("code").GetString());
        var azure = await Sim.Chat(Req("hi"), "/openai/deployments/gpt-sim/chat/completions?api-version=1");
        Assert.Equal(401, azure.Status);
        Assert.Equal(["code", "message"], azure.Error.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("401", azure.Error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Extra_response_headers_are_added_to_completions_and_errors()
    {
        await Sim.ScriptAsync(new { content = "ok", headers = new Dictionary<string, string> { ["X-Ratelimit-Remaining-Requests"] = "0", ["X-Trace"] = "abc" } },
            new { status = 500, headers = new Dictionary<string, string> { ["X-Trace"] = "err" } });
        var ok = await Sim.Chat(Req("hi"));
        Assert.Equal("0", ok.Header("X-Ratelimit-Remaining-Requests"));
        Assert.Equal("abc", ok.Header("X-Trace"));
        Assert.Equal("err", (await Sim.Chat(Req("hi"))).Header("X-Trace"));
    }

    [Fact]
    public async Task DelayMs_postpones_the_answer()
    {
        await Sim.ScriptAsync(new { content = "slow", delayMs = 400 });
        var watch = Stopwatch.StartNew();
        var response = await Sim.Chat(Req("hi"));
        Assert.InRange(watch.ElapsedMilliseconds, 350, 5000);
        Assert.Equal("slow", response.Content);
        var latency = (await Sim.LogAsync()).Single().GetProperty("latencyMs").GetInt64();
        Assert.InRange(latency, 350, 5000);
    }

    [Fact]
    public async Task A_delayed_request_is_released_when_the_client_gives_up_and_is_logged_as_cancelled()
    {
        await Sim.ScriptAsync(new { content = "never delivered", delayMs = 60_000 });
        using var cts = new CancellationTokenSource(300);
        using var request = Sim.Build(HttpMethod.Post, "/v1/chat/completions", Req("hi"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sim.Http.SendAsync(request, cts.Token));

        var entries = await WaitForLogAsync(1);
        Assert.Equal(499, entries[0].GetProperty("status").GetInt32());
        Assert.Equal("queue", entries[0].GetProperty("matched").GetString());
        Assert.InRange(entries[0].GetProperty("latencyMs").GetInt64(), 200, 10_000);
    }

    private async Task<List<JsonElement>> WaitForLogAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var log = await Sim.LogAsync();
            if (log.Count >= count || DateTime.UtcNow > deadline) return log;
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task HangMs_sends_headers_then_stalls_mid_body_so_client_timeouts_fire()
    {
        await Sim.ScriptAsync(new { content = new string('h', 400), hangMs = 30_000 }, new { content = new string('h', 400), hangMs = 30_000 });

        // 1. a client timeout covering the whole exchange
        using (var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) })
        using (var request = Sim.Build(HttpMethod.Post, "/v1/chat/completions", Req("hi")))
        {
            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request));
            Assert.InRange(watch.ElapsedMilliseconds, 400, 10_000);
        }

        // 2. the status line and headers do arrive; only the body stalls
        using var patient = new HttpClient();
        using var second = Sim.Build(HttpMethod.Post, "/v1/chat/completions", Req("hi"));
        using var response = await patient.SendAsync(second, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString());
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.Content.ReadAsStringAsync(cts.Token));
    }

    [Fact]
    public async Task A_short_hang_still_ends_with_the_complete_valid_answer()
    {
        await Sim.ScriptAsync(new { content = "worth the wait", hangMs = 400 });
        var watch = Stopwatch.StartNew();
        var response = await Sim.Chat(Req("hi"));
        Assert.InRange(watch.ElapsedMilliseconds, 350, 10_000);
        Assert.Equal("worth the wait", response.Content);
    }

    [Fact]
    public async Task A_stream_can_hang_after_the_first_chunk()
    {
        await Sim.ScriptAsync(new { content = new string('s', 100), hangMs = 30_000 });
        using var request = Sim.Build(HttpMethod.Post, "/v1/chat/completions", Req("hi", "gpt-sim", ("stream", true)));
        using var response = await Sim.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.StartsWith("data: ", (await reader.ReadLineAsync())!);
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await reader.ReadLineAsync(cts.Token) is not null) { }
        });
    }

    [Fact]
    public async Task MalformedJson_truncates_a_200_body_so_the_client_cannot_parse_it()
    {
        await Sim.ScriptAsync(new { content = "this answer is cut off", malformedJson = true });
        var response = await Sim.Chat(Req("hi"));
        Assert.Equal(200, response.Status);
        Assert.StartsWith("application/json", response.ContentType);
        Assert.StartsWith("{\"id\":\"chatcmpl-", response.Text);
        Assert.Equal(JsonValueKind.Undefined, response.Json.ValueKind);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(response.Text));
        Assert.DoesNotContain("\"usage\"", response.Text);
    }

    [Fact]
    public async Task A_malformed_stream_has_an_unparseable_data_line_followed_by_done()
    {
        await Sim.ScriptAsync(new { malformedJson = true });
        var stream = await Sim.StreamAsync(Req("hi", "gpt-sim", ("stream", true)));
        Assert.Equal(2, stream.Data.Count);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(stream.Data[0]));
        Assert.Equal("[DONE]", stream.Data[1]);
    }

    [Fact]
    public async Task EmptyChoices_returns_a_completion_without_choices()
    {
        await Sim.ScriptAsync(new { emptyChoices = true });
        var response = await Sim.Chat(Req("hi"));
        Assert.Equal(200, response.Status);
        Assert.Equal(JsonValueKind.Array, response.Json.GetProperty("choices").ValueKind);
        Assert.Equal(0, response.Json.GetProperty("choices").GetArrayLength());
        Assert.Equal(0, response.Json.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal("chat.completion", response.Json.GetProperty("object").GetString());

        await Sim.ScriptAsync(new { emptyChoices = true });
        var stream = await Sim.StreamAsync(Req("hi", "gpt-sim", ("stream", true)));
        Assert.Equal(0, stream.Chunks[0].GetProperty("choices").GetArrayLength());
        Assert.Equal("[DONE]", stream.Data[^1]);
    }

    [Fact]
    public async Task A_proxy_error_page_can_stand_in_for_the_provider()
    {
        await Sim.ScriptAsync(new { status = 502, contentType = "text/html", content = "<html><head><title>502 Bad Gateway</title></head><body><h1>Bad Gateway</h1><p>nginx</p></body></html>" });
        var response = await Sim.Chat(Req("hi"));
        Assert.Equal(502, response.Status);
        Assert.StartsWith("text/html", response.ContentType);
        Assert.StartsWith("<html>", response.Text);
        Assert.Equal(JsonValueKind.Undefined, response.Json.ValueKind);
    }

    [Fact]
    public async Task A_content_type_override_without_status_serves_the_raw_body_with_200()
    {
        await Sim.ScriptAsync(new { contentType = "text/plain", content = "plain words" }, new { contentType = "application/json", content = "{\"not\":\"a completion\"}" },
            new { contentType = "text/yaml", contentFrom = "good/minimal.yml" });
        var plain = await Sim.Chat(Req("hi"));
        Assert.Equal(200, plain.Status);
        Assert.StartsWith("text/plain", plain.ContentType);
        Assert.Equal("plain words", plain.Text);
        var json = await Sim.Chat(Req("hi"));
        Assert.Equal("{\"not\":\"a completion\"}", json.Text);
        Assert.False(json.Json.TryGetProperty("choices", out _));
        Assert.StartsWith("name: CI", (await Sim.Chat(Req("hi"))).Text);
    }

    [Fact]
    public async Task ContentFilter_returns_the_azure_400_for_non_streaming_requests()
    {
        await Sim.ScriptAsync(new { contentFilter = true });
        var response = await Sim.Chat(Req("hi"), "/openai/deployments/gpt-sim/chat/completions?api-version=2024-10-21");
        Assert.Equal(400, response.Status);
        var error = response.Error;
        Assert.Equal(["message", "type", "param", "code", "status", "innererror"], error.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.StartsWith("The response was filtered due to the prompt triggering Azure OpenAI's content management policy.", error.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("type").ValueKind);
        Assert.Equal("prompt", error.GetProperty("param").GetString());
        Assert.Equal("content_filter", error.GetProperty("code").GetString());
        Assert.Equal(400, error.GetProperty("status").GetInt32());
        var inner = error.GetProperty("innererror");
        Assert.Equal("ResponsibleAIPolicyViolation", inner.GetProperty("code").GetString());
        var result = inner.GetProperty("content_filter_result");
        Assert.Equal(["hate", "self_harm", "sexual", "violence"], result.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(result.GetProperty("hate").GetProperty("filtered").GetBoolean());
        Assert.Equal("high", result.GetProperty("hate").GetProperty("severity").GetString());
        Assert.False(result.GetProperty("violence").GetProperty("filtered").GetBoolean());
        Assert.Equal("safe", result.GetProperty("violence").GetProperty("severity").GetString());
    }

    [Fact]
    public async Task ContentFilter_ends_a_stream_with_the_content_filter_finish_reason()
    {
        await Sim.ScriptAsync(new { content = new string('c', 100), contentFilter = true });
        var stream = await Sim.StreamAsync(Req("hi", "gpt-sim", ("stream", true)));
        Assert.Equal(200, stream.Status);
        Assert.Equal("content_filter", stream.Chunks[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(50, stream.Text.Length);
        Assert.Equal("[DONE]", stream.Data[^1]);
    }

    [Fact]
    public async Task StreamAbort_without_streaming_cuts_the_body_and_closes_the_connection()
    {
        await Sim.ScriptAsync(new { content = new string('a', 500), streamAbort = true });
        await Assert.ThrowsAnyAsync<Exception>(() => Sim.Chat(Req("hi")));
        Assert.Equal(200, (await Sim.Chat(Req("hi"))).Status);
    }

    [Fact]
    public async Task A_global_fault_answers_matching_paths_until_it_is_used_up()
    {
        var added = await Sim.Post("/_sim/faults", new { pathRegex = "/models$", status = 503, remaining = 2, message = "down for maintenance" });
        Assert.Equal(201, added.Status);
        var first = await Sim.Get("/v1/models");
        Assert.Equal(503, first.Status);
        Assert.Equal("down for maintenance", first.Error.GetProperty("message").GetString());
        Assert.Equal("server_error", first.Error.GetProperty("type").GetString());
        Assert.Equal(503, (await Sim.Get("/v1/models")).Status);
        Assert.Equal(200, (await Sim.Get("/v1/models")).Status);
        // other paths were never affected
        Assert.Equal(200, (await Sim.Chat(Req("hi"))).Status);
    }

    [Fact]
    public async Task A_global_fault_without_a_path_hits_every_model_request_but_not_the_control_plane_and_can_be_cleared()
    {
        await Sim.ScriptAsync(new { content = "still queued" });
        await Sim.Post("/_sim/faults", new { status = 429, retryAfterSeconds = 2 });
        var limited = await Sim.Chat(Req("hi"));
        Assert.Equal(429, limited.Status);
        Assert.Equal("2", limited.Header("Retry-After"));
        Assert.Equal(429, (await Sim.Get("/v1/models")).Status);
        Assert.Equal(401, (await Sim.Chat(Req("hi"), key: "bad")).Status);
        Assert.Equal(200, (await Sim.Get("/_sim/stats")).Status);
        Assert.Equal(1, (await Sim.Get("/_sim/script")).Json.GetProperty("remaining").GetInt32());

        Assert.Equal(204, (await Sim.Delete("/_sim/faults")).Status);
        Assert.Equal("still queued", (await Sim.Chat(Req("hi"))).Content);
    }

    [Fact]
    public async Task Invalid_faults_are_rejected()
    {
        Assert.Equal(400, (await Sim.Post("/_sim/faults", new { status = 418 })).Status);
        Assert.Equal(400, (await Sim.Post("/_sim/faults", new { status = 500, pathRegex = "(" })).Status);
        Assert.Equal(400, (await Sim.Post("/_sim/faults", "{\"statuss\":500}")).Status);
        Assert.Equal(200, (await Sim.Get("/v1/models")).Status);
    }

    [Fact]
    public async Task Fault_responses_are_logged_like_any_other_rejected_request()
    {
        await Sim.Post("/_sim/faults", new { pathRegex = "/v1/chat", status = 500, remaining = 1 });
        await Sim.Chat(Req("hi"));
        var entry = (await Sim.LogAsync()).Single();
        Assert.Equal(500, entry.GetProperty("status").GetInt32());
        Assert.Equal("none", entry.GetProperty("matched").GetString());
    }
}
