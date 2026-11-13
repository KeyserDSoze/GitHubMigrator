using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlmSim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LlmSim.Tests;

public sealed record Res(int Status, JsonElement Json, string Text, HttpResponseMessage Message)
{
    public string? Header(string name) =>
        Message.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
        : Message.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : null;

    public string ContentType => Message.Content.Headers.ContentType?.ToString() ?? "";
    public JsonElement Error => Json.GetProperty("error");
    public string Content => Json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
    public string FinishReason => Json.GetProperty("choices")[0].GetProperty("finish_reason").GetString()!;
}

// What a streaming client saw: the "data: " payloads in order, and the error if the connection broke.
public sealed record Sse(int Status, string ContentType, List<string> Data, List<string> OtherLines, Exception? Failure)
{
    public bool Done => Data.Contains("[DONE]");
    public List<JsonElement> Chunks => [.. Data.Where(d => d != "[DONE]").Select(d => JsonDocument.Parse(d).RootElement.Clone())];
    public string Text => string.Concat(Chunks.Where(c => c.GetProperty("choices").GetArrayLength() > 0)
        .Select(c => c.GetProperty("choices")[0].GetProperty("delta"))
        .Where(d => d.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        .Select(d => d.GetProperty("content").GetString()));
}

public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

// LlmSim running in-process on a random port: no network beyond loopback, no database.
public sealed class SimHost : IAsyncDisposable
{
    public const string Key = "llm-sim-key";
    public WebApplication App { get; private set; } = null!;
    public string Origin { get; private set; } = "";
    public ManualClock Clock { get; } = new();
    public HttpClient Http { get; } = new();

    public static async Task<SimHost> StartAsync(Func<ManualClock, LlmSimOptions>? options = null)
    {
        var host = new SimHost();
        host.App = await LlmSimApp.StartAsync(options?.Invoke(host.Clock) ?? new LlmSimOptions("http://127.0.0.1:0", Clock: host.Clock));
        host.Origin = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return host;
    }

    public static LlmSimOptions Options(ManualClock clock, string? recipes = null, bool requireKey = true,
        IReadOnlyList<string>? keys = null, IReadOnlyList<string>? models = null) =>
        new("http://127.0.0.1:0", keys, requireKey, models, recipes, clock);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }

    public HttpRequestMessage Build(HttpMethod method, string path, object? body, string? key = Key, string keyHeader = "Authorization",
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path.StartsWith("http") ? path : Origin + path);
        if (key is not null)
        {
            if (keyHeader == "Authorization") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            else request.Headers.TryAddWithoutValidation(keyHeader, key);
        }
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    public async Task<Res> SendAsync(HttpMethod method, string path, object? body = null, string? key = Key, string keyHeader = "Authorization",
        params (string Name, string Value)[] headers)
    {
        using var request = Build(method, path, body, key, keyHeader, headers);
        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (text.Length > 0) { try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { } }
        return new Res((int)response.StatusCode, json, text, response);
    }

    public Task<Res> Get(string path, string? key = Key) => SendAsync(HttpMethod.Get, path, null, key);
    public Task<Res> Post(string path, object body, string? key = Key) => SendAsync(HttpMethod.Post, path, body, key);
    public Task<Res> Delete(string path, string? key = Key) => SendAsync(HttpMethod.Delete, path, null, key);

    public Task<Res> Chat(object body, string path = "/v1/chat/completions", string? key = Key, string keyHeader = "Authorization") =>
        SendAsync(HttpMethod.Post, path, body, key, keyHeader);

    public async Task<Res> ScriptAsync(params object[] replies)
    {
        var response = await Post("/_sim/script", new { responses = replies });
        Assert.Equal(201, response.Status);
        return response;
    }

    public async Task AddRecipesAsync(params object[] recipes)
    {
        var response = await Post("/_sim/recipes", recipes);
        Assert.Equal(201, response.Status);
    }

    public async Task<Sse> StreamAsync(object body, string path = "/v1/chat/completions", string? key = Key)
    {
        using var request = Build(HttpMethod.Post, path, body, key);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var data = new List<string>(); var other = new List<string>(); Exception? failure = null;
        try
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal)) data.Add(line[6..]);
                else if (line.Length > 0) other.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { failure = ex; }
        return new Sse((int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "", data, other, failure);
    }

    public Task ResetAsync() => Post("/_sim/reset", new { });

    public async Task<List<JsonElement>> LogAsync(string query = "")
    {
        var response = await Get("/_sim/requests" + query);
        Assert.Equal(200, response.Status);
        return [.. response.Json.EnumerateArray()];
    }

    public static Dictionary<string, object?> Req(string user, string model = "gpt-sim", params (string Key, object? Value)[] extra)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = model, ["messages"] = new[] { new { role = "user", content = user } }
        };
        foreach (var (key, value) in extra) request[key] = value;
        return request;
    }

    public static Dictionary<string, object?> ReqMessages(string model, params (string Role, string Content)[] messages) => new()
    {
        ["model"] = model, ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
    };
}

public sealed class LlmSimFixture : IAsyncLifetime
{
    public SimHost Host { get; private set; } = null!;
    public async Task InitializeAsync() => Host = await SimHost.StartAsync();
    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

// One simulator per test class; every test starts from a clean slate.
public abstract class SimTests(LlmSimFixture fixture) : IAsyncLifetime
{
    protected SimHost Sim => fixture.Host;
    public Task InitializeAsync() => Sim.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    protected static Stopwatch Timer() => Stopwatch.StartNew();
}
