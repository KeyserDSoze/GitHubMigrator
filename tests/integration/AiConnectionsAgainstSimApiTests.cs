using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using LlmSim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The real API host (PostgreSQL, JWT, AES-GCM secret store, production DI) with AI connections pointed at the LLM
// simulator over real HTTP. What a real provider answers must still be confirmed online.
public sealed class AiConnectionsAgainstSimApiTests : IAsyncLifetime
{
    private const string KeyA = "alpha-key-0001";
    private const string KeyB = "bravo-key-0002";
    private WebApplication _sim = null!;
    private string _origin = "";
    private readonly HttpClient _control = new();
    private ApiHarness _api = null!;

    public async Task InitializeAsync()
    {
        _sim = await LlmSimApp.StartAsync(new LlmSimOptions("http://127.0.0.1:0", [KeyA, KeyB]));
        _origin = _sim.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KeyA);
        _api = new ApiHarness { LlmSimOrigin = _origin };
        await _api.InitializeAsync();
        // The database outlives a test run and is shared: start from no AI connections (no other test uses them).
        await InDbAsync(async (db, _) =>
        {
            await db.AiConnections.ExecuteDeleteAsync();
            await db.ConnectionSecrets.Where(x => x.Reference.StartsWith("ai-connection-")).ExecuteDeleteAsync();
            return 0;
        });
    }

    public async Task DisposeAsync()
    {
        _control.Dispose();
        await _api.DisposeAsync();
        await _sim.StopAsync();
        await _sim.DisposeAsync();
    }

    private enum Who { Anonymous, User, Admin, Superadmin }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Who who = Who.Admin, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        var token = who switch
        {
            Who.Admin => await _api.LoginAsync(_api.AdminName, ApiHarness.AdminPassword),
            Who.Superadmin => await _api.LoginAsync(_api.SuperadminName, ApiHarness.SuperadminPassword),
            Who.User => await _api.LoginAsync(_api.UserName, ApiHarness.UserPassword),
            _ => null
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _api.Client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private object Draft(string key = KeyA, string? endpoint = null, string model = "gpt-sim") => new
    {
        name = "Corporate model", provider = "OpenAICompatible", endpoint = endpoint ?? _origin + "/v1", model, auth = "ApiKey", apiKey = key
    };

    private async Task<JsonElement[]> SimLogAsync()
    {
        using var document = JsonDocument.Parse(await _control.GetStringAsync(_origin + "/_sim/requests"));
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private async Task ScriptAsync(params object[] replies) =>
        (await _control.PostAsync(_origin + "/_sim/script", new StringContent(JsonSerializer.Serialize(new { responses = replies }), Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();

    private async Task<T> InDbAsync<T>(Func<AppDbContext, IConnectionSecretStore, Task<T>> read)
    {
        await using var scope = _api.Host.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IConnectionSecretStore>());
    }

    private async Task<Guid> CreateAsync(string key = KeyA)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: Draft(key));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    // ---- life cycle -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_connection_is_created_listed_verified_and_deleted_and_the_key_is_only_ever_stored_encrypted()
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: Draft());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(KeyA, text);
        Assert.DoesNotContain("ai-connection-", text);
        var item = JsonDocument.Parse(text).RootElement;
        var id = item.GetProperty("id").GetGuid();
        Assert.Equal("Verified", item.GetProperty("verificationCode").GetString());
        Assert.Equal("gpt-sim", item.GetProperty("verifiedModel").GetString());
        Assert.False(item.GetProperty("sendSourceContent").GetBoolean());

        // the ping that proved it: one request, the key in the masked header only
        var ping = Assert.Single(await SimLogAsync());
        Assert.Equal("/v1/chat/completions", ping.GetProperty("path").GetString());
        Assert.Equal("alp***", ping.GetProperty("apiKeyHint").GetString());
        Assert.DoesNotContain(KeyA, ping.GetProperty("body").GetString());
        Assert.Equal("ping", ping.GetProperty("json").GetProperty("messages")[1].GetProperty("content").GetString());

        using var listed = await SendAsync(HttpMethod.Get, "/api/ai-connections");
        var listText = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(KeyA, listText);
        Assert.Equal(id, JsonDocument.Parse(listText).RootElement.EnumerateArray().Single().GetProperty("id").GetGuid());

        var stored = await InDbAsync(async (db, secrets) =>
        {
            var row = await db.AiConnections.AsNoTracking().SingleAsync(x => x.Id == id);
            var secret = await db.ConnectionSecrets.AsNoTracking().SingleAsync(x => x.Reference == row.SecretReference);
            return (row, secret, plain: await secrets.ReadAsync(row.SecretReference, default));
        });
        Assert.Equal($"ai-connection-{id:N}", stored.row.SecretReference);
        Assert.Equal(KeyA, stored.plain);
        Assert.NotEqual(Encoding.UTF8.GetBytes(KeyA), stored.secret.Ciphertext);
        Assert.DoesNotContain(KeyA, JsonSerializer.Serialize(stored.row));

        using var verified = await SendAsync(HttpMethod.Post, $"/api/ai-connections/{id}/verify");
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal("Verified", (await JsonOf(verified)).GetProperty("verificationCode").GetString());

        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/ai-connections/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await InDbAsync((db, _) => db.ConnectionSecrets.CountAsync(x => x.Reference == $"ai-connection-{id:N}")));
        Assert.Equal(0, await InDbAsync((db, _) => db.AiConnections.CountAsync()));
        using var again = await SendAsync(HttpMethod.Delete, $"/api/ai-connections/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_key_the_model_rejects_creates_nothing_and_says_why()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: Draft("wrong-key-000000"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Unauthorized", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, await InDbAsync((db, _) => db.AiConnections.CountAsync()));
        Assert.Equal(0, await InDbAsync((db, _) => db.ConnectionSecrets.CountAsync(x => x.Reference.StartsWith("ai-connection-"))));
    }

    [Fact]
    public async Task An_unknown_model_and_a_model_that_is_down_are_reported_with_their_own_codes()
    {
        using var unknown = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: Draft(model: "no-such-model"));
        Assert.Equal("ModelNotFound", (await JsonOf(unknown)).GetProperty("error").GetString());

        await ScriptAsync(new { status = 500 });
        using var down = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: Draft());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, down.StatusCode);
        Assert.Equal("Unavailable", (await JsonOf(down)).GetProperty("error").GetString());
        Assert.Equal(0, await InDbAsync((db, _) => db.AiConnections.CountAsync()));
    }

    [Fact]
    public async Task Testing_a_draft_does_not_save_anything()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/ai-connections/validate", body: Draft());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonOf(response);
        Assert.True(result.GetProperty("isValid").GetBoolean());
        Assert.Equal("gpt-sim", result.GetProperty("model").GetString());
        Assert.Single(await SimLogAsync());
        Assert.Equal(0, await InDbAsync((db, _) => db.AiConnections.CountAsync()));
        Assert.Equal(0, await InDbAsync((db, _) => db.ConnectionSecrets.CountAsync(x => x.Reference.StartsWith("ai-connection-"))));
    }

    // ---- what the operator allows -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_outside_the_operator_allow_list_is_refused_before_any_request_leaves()
    {
        foreach (var endpoint in new[] { "https://evil.example/v1", "http://169.254.169.254/latest", "https://user:pw@127.0.0.1/v1", "not a url" })
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: Draft(endpoint: endpoint));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("InvalidAiConnectionInput", (await JsonOf(response)).GetProperty("error").GetString());
        }
        Assert.Empty(await SimLogAsync());
    }

    [Fact]
    public async Task Malformed_input_is_a_bad_request_never_a_server_error()
    {
        foreach (var body in new object[]
        {
            new { name = "x", provider = "Nope", endpoint = _origin + "/v1", model = "gpt-sim", auth = "ApiKey", apiKey = KeyA },
            new { name = "x", provider = "1", endpoint = _origin + "/v1", model = "gpt-sim", auth = "ApiKey", apiKey = KeyA },
            new { name = "x", provider = "OpenAICompatible", endpoint = _origin + "/v1", model = "gpt sim; drop", auth = "ApiKey", apiKey = KeyA },
            new { name = "x", provider = "OpenAICompatible", endpoint = _origin + "/v1", model = "gpt-sim", auth = "ApiKey", apiKey = "short" },
            new { name = "x", provider = "OpenAICompatible", endpoint = _origin + "/v1", model = "gpt-sim", auth = "ManagedIdentity" },
            new { name = "", provider = "OpenAICompatible", endpoint = _origin + "/v1", model = "gpt-sim", auth = "ApiKey", apiKey = KeyA },
            new { name = "x", provider = "OpenAICompatible", endpoint = _origin + "/v1", model = "gpt-sim", auth = "ApiKey", apiKey = KeyA, maxOutputTokens = 1 },
        })
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty(await SimLogAsync());
    }

    [Fact]
    public async Task The_options_tell_the_ui_what_this_deployment_allows()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/ai-connections/options");
        var options = await JsonOf(response);
        Assert.Equal(["127.0.0.1"], options.GetProperty("allowedHosts").EnumerateArray().Select(e => e.GetString()));
        Assert.True(options.GetProperty("allowHttp").GetBoolean());
        Assert.False(options.GetProperty("managedIdentityAvailable").GetBoolean());
        Assert.Contains("OpenAICompatible", options.GetProperty("providers").EnumerateArray().Select(e => e.GetString()));
    }

    // ---- verification, rotation and consent --------------------------------------------------------------------------

    [Fact]
    public async Task Verifying_again_reflects_what_the_model_does_now_and_consent_needs_a_working_connection()
    {
        var id = await CreateAsync();
        await ScriptAsync(new { status = 401 });
        using var failed = await SendAsync(HttpMethod.Post, $"/api/ai-connections/{id}/verify");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("Unauthorized", (await JsonOf(failed)).GetProperty("verificationCode").GetString());

        using var refused = await SendAsync(HttpMethod.Put, $"/api/ai-connections/{id}/source-consent", Who.Superadmin, new { sendSourceContent = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("NotVerified", (await JsonOf(refused)).GetProperty("error").GetString());

        using var recovered = await SendAsync(HttpMethod.Post, $"/api/ai-connections/{id}/verify");
        Assert.Equal("Verified", (await JsonOf(recovered)).GetProperty("verificationCode").GetString());
    }

    [Fact]
    public async Task Only_a_superadmin_changes_the_source_consent_and_it_is_recorded_with_who_and_when()
    {
        var id = await CreateAsync();
        var url = $"/api/ai-connections/{id}/source-consent";
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Put, url, Who.Anonymous, new { sendSourceContent = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, url, Who.User, new { sendSourceContent = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, url, Who.Admin, new { sendSourceContent = true })).StatusCode);

        using var allowed = await SendAsync(HttpMethod.Put, url, Who.Superadmin, new { sendSourceContent = true });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var item = await JsonOf(allowed);
        Assert.True(item.GetProperty("sendSourceContent").GetBoolean());
        Assert.Equal(_api.SuperadminName, item.GetProperty("consentBy").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("consentAtUtc").ValueKind);

        var stored = await InDbAsync((db, _) => db.AiConnections.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.True(stored.SendSourceContent);
        Assert.Equal(_api.SuperadminName, stored.ConsentBy);

        using var revoked = await SendAsync(HttpMethod.Put, url, Who.Superadmin, new { sendSourceContent = false });
        Assert.False((await JsonOf(revoked)).GetProperty("sendSourceContent").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/api/ai-connections/{Guid.NewGuid()}/source-consent", Who.Superadmin, new { sendSourceContent = true })).StatusCode);
    }

    [Fact]
    public async Task Rotating_replaces_the_stored_key_only_after_the_model_accepted_the_new_one()
    {
        var id = await CreateAsync(KeyA);
        using var rejected = await SendAsync(HttpMethod.Put, $"/api/ai-connections/{id}/credential", body: new { apiKey = "wrong-key-000000" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(KeyA, await InDbAsync((_, secrets) => secrets.ReadAsync($"ai-connection-{id:N}", default)));

        using var short1 = await SendAsync(HttpMethod.Put, $"/api/ai-connections/{id}/credential", body: new { apiKey = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, short1.StatusCode);

        using var rotated = await SendAsync(HttpMethod.Put, $"/api/ai-connections/{id}/credential", body: new { apiKey = KeyB });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.DoesNotContain(KeyB, await rotated.Content.ReadAsStringAsync());
        Assert.Equal(KeyB, await InDbAsync((_, secrets) => secrets.ReadAsync($"ai-connection-{id:N}", default)));
        Assert.Equal("bra***", (await SimLogAsync())[^1].GetProperty("apiKeyHint").GetString());

        using var verified = await SendAsync(HttpMethod.Post, $"/api/ai-connections/{id}/verify");
        Assert.Equal("Verified", (await JsonOf(verified)).GetProperty("verificationCode").GetString());
        Assert.Equal("bra***", (await SimLogAsync())[^1].GetProperty("apiKeyHint").GetString());
    }

    // ---- access ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_administrators_can_read_or_change_ai_connections()
    {
        var id = await CreateAsync();
        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, "/api/ai-connections"), (HttpMethod.Get, "/api/ai-connections/options"),
            (HttpMethod.Post, "/api/ai-connections/validate"), (HttpMethod.Post, "/api/ai-connections"),
            (HttpMethod.Post, $"/api/ai-connections/{id}/verify"), (HttpMethod.Put, $"/api/ai-connections/{id}/credential"),
            (HttpMethod.Delete, $"/api/ai-connections/{id}")
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(method, url, Who.Anonymous, Draft())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(method, url, Who.User, Draft())).StatusCode);
        }
        Assert.Equal(1, await InDbAsync((db, _) => db.AiConnections.CountAsync()));
    }
}
