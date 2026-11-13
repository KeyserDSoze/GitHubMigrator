using System.Text.Json;
using Xunit;
using static LlmSim.Tests.SimHost;

namespace LlmSim.Tests;

public sealed class AuthTests(LlmSimFixture fixture) : SimTests(fixture), IClassFixture<LlmSimFixture>
{
    [Fact]
    public async Task Bearer_and_api_key_headers_are_accepted()
    {
        Assert.Equal(200, (await Sim.Chat(Req("hi"))).Status);
        Assert.Equal(200, (await Sim.Chat(Req("hi"), key: Key, keyHeader: "api-key")).Status);
        Assert.Equal(200, (await Sim.Get("/v1/models")).Status);
        Assert.Equal(200, (await Sim.SendAsync(HttpMethod.Get, "/v1/models", key: Key, keyHeader: "api-key")).Status);
    }

    [Fact]
    public async Task A_wrong_key_is_rejected_with_openais_invalid_api_key_body_and_the_key_is_masked()
    {
        const string wrong = "sk-this-is-not-a-valid-key-123456";
        var response = await Sim.Chat(Req("hi"), key: wrong);
        Assert.Equal(401, response.Status);
        Assert.StartsWith("application/json", response.ContentType);
        Assert.Equal(["error"], response.Json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["message", "type", "param", "code"], response.Error.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.StartsWith("Incorrect API key provided: sk-", response.Error.GetProperty("message").GetString());
        Assert.EndsWith("You can find your API key at https://platform.openai.com/account/api-keys.", response.Error.GetProperty("message").GetString());
        Assert.DoesNotContain(wrong, response.Text);
        Assert.Contains("3456", response.Error.GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", response.Error.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, response.Error.GetProperty("param").ValueKind);
        Assert.Equal("invalid_api_key", response.Error.GetProperty("code").GetString());
        Assert.StartsWith("req_", response.Header("x-request-id"));
    }

    [Fact]
    public async Task A_missing_key_or_a_non_bearer_scheme_is_rejected()
    {
        var none = await Sim.Chat(Req("hi"), key: null);
        Assert.Equal(401, none.Status);
        Assert.StartsWith("You didn't provide an API key.", none.Error.GetProperty("message").GetString());
        Assert.Equal("invalid_api_key", none.Error.GetProperty("code").GetString());

        using var basic = Sim.Build(HttpMethod.Post, "/v1/chat/completions", Req("hi"), key: null);
        basic.Headers.TryAddWithoutValidation("Authorization", "Basic dXNlcjpwYXNz");
        using var response = await Sim.Http.SendAsync(basic);
        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task Azure_routes_answer_401_with_the_azure_gateway_body()
    {
        foreach (var path in new[] { "/openai/v1/chat/completions", "/openai/deployments/gpt-sim/chat/completions?api-version=2024-10-21", "/openai/v1/models" })
        {
            var response = path.EndsWith("models")
                ? await Sim.Get(path, key: "nope")
                : await Sim.Chat(Req("hi"), path, key: "nope");
            Assert.Equal(401, response.Status);
            Assert.Equal(["error"], response.Json.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(["code", "message"], response.Error.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("401", response.Error.GetProperty("code").GetString());
            Assert.StartsWith("Access denied due to invalid subscription key or wrong API endpoint.", response.Error.GetProperty("message").GetString());
        }
        // an Azure client without any key hits the same wall
        Assert.Equal("401", (await Sim.Chat(Req("hi"), "/openai/v1/chat/completions", key: null)).Error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Healthz_is_open_but_the_control_plane_needs_the_key()
    {
        var health = await Sim.Get("/healthz", key: null);
        Assert.Equal(200, health.Status);
        Assert.Equal("ok", health.Text);
        Assert.Equal(401, (await Sim.Get("/_sim/requests", key: null)).Status);
        Assert.Equal(401, (await Sim.Get("/_sim/stats", key: "wrong")).Status);
        Assert.Equal(401, (await Sim.Post("/_sim/reset", new { }, key: null)).Status);
        Assert.Equal(200, (await Sim.Get("/_sim/stats")).Status);
    }

    [Fact]
    public async Task Several_keys_can_be_configured()
    {
        await using var host = await SimHost.StartAsync(c => Options(c, keys: ["key-a", "key-b"]));
        Assert.Equal(200, (await host.Chat(Req("hi"), key: "key-a")).Status);
        Assert.Equal(200, (await host.Chat(Req("hi"), key: "key-b", keyHeader: "api-key")).Status);
        Assert.Equal(401, (await host.Chat(Req("hi"), key: Key)).Status);
    }

    [Fact]
    public async Task Anonymous_mode_accepts_any_request_like_a_local_ollama_and_logs_who_was_authenticated()
    {
        await using var host = await SimHost.StartAsync(c => Options(c, requireKey: false));
        Assert.Equal(200, (await host.Chat(Req("hi"), key: null)).Status);
        Assert.Equal(200, (await host.Chat(Req("hi"), key: "whatever")).Status);
        Assert.Equal(200, (await host.Chat(Req("hi"), key: Key)).Status);
        Assert.Equal(200, (await host.Get("/v1/models", key: null)).Status);
        Assert.Equal(200, (await host.Get("/_sim/stats", key: null)).Status);

        var log = await host.LogAsync("?path=/v1/chat");
        Assert.Equal([false, false, true], log.Select(e => e.GetProperty("authenticated").GetBoolean()).ToArray());
        Assert.Equal([JsonValueKind.Null, JsonValueKind.String, JsonValueKind.String], log.Select(e => e.GetProperty("apiKeyHint").ValueKind).ToArray());
    }
}
