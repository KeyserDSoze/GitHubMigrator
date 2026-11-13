using System.Net;
using System.Text.Json;
using GitHubMigrator.Business;
using System.Text.Json.Nodes;
using Xunit;

namespace GitHubMigrator.LiveSmoke.Tests;

// Opt-in tests against REAL Azure DevOps Services / GitHub. Everything here is read-only:
// every HTTP request goes through ReadOnlyGuardHandler, which refuses anything but GET/HEAD.
public sealed record LiveAdo(string Organization, string Pat, string? Project, string? Repository);
public sealed record LiveGitHub(string Organization, string Pat, string? Repository);
public sealed record LiveAi(string Provider, string Endpoint, string Model, string Auth, string? Key, string? ApiVersion);

public static class LiveEnvironment
{
    public static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public static LiveAdo? Ado =>
        Env("GITHUBMIGRATOR_LIVE_ADO_ORG") is { } org && Env("GITHUBMIGRATOR_LIVE_ADO_PAT") is { } pat
            ? new(org, pat, Env("GITHUBMIGRATOR_LIVE_ADO_PROJECT"), Env("GITHUBMIGRATOR_LIVE_ADO_REPO")) : null;

    public static LiveGitHub? GitHub =>
        Env("GITHUBMIGRATOR_LIVE_GH_ORG") is { } org && Env("GITHUBMIGRATOR_LIVE_GH_PAT") is { } pat
            ? new(org, pat, Env("GITHUBMIGRATOR_LIVE_GH_REPO")) : null;

    // A real model endpoint. Unlike the read-only checks above this one sends prompts (fixed, secret-free ones).
    //   GITHUBMIGRATOR_LIVE_AI_ENDPOINT, GITHUBMIGRATOR_LIVE_AI_MODEL, GITHUBMIGRATOR_LIVE_AI_KEY (unless GITHUBMIGRATOR_LIVE_AI_AUTH=None),
    //   GITHUBMIGRATOR_LIVE_AI_PROVIDER = OpenAICompatible (default) | AzureOpenAI, GITHUBMIGRATOR_LIVE_AI_API_VERSION (Azure deployment route)
    public static LiveAi? Ai =>
        Env("GITHUBMIGRATOR_LIVE_AI_ENDPOINT") is { } endpoint && Env("GITHUBMIGRATOR_LIVE_AI_MODEL") is { } model &&
        (Env("GITHUBMIGRATOR_LIVE_AI_AUTH") ?? "ApiKey") is var auth && (auth == "None" || Env("GITHUBMIGRATOR_LIVE_AI_KEY") is not null)
            ? new(Env("GITHUBMIGRATOR_LIVE_AI_PROVIDER") ?? "OpenAICompatible", endpoint, model, auth, Env("GITHUBMIGRATOR_LIVE_AI_KEY"),
                Env("GITHUBMIGRATOR_LIVE_AI_API_VERSION")) : null;

    // Self-test ("dry run"): point the SAME tests at the simulators to prove the test logic, never at a real service.
    //   GITHUBMIGRATOR_LIVE_ADO_SIM_ORIGIN = origin of tools/AdoSim started with ADO_SIM_FLAVOR=services
    //   GITHUBMIGRATOR_LIVE_GH_SIM_ORIGIN  = origin of tools/GhSim
    public static AzureDevOpsServicesEndpoints AdoEndpoints =>
        Env("GITHUBMIGRATOR_LIVE_ADO_SIM_ORIGIN") is { } o ? new(o, o + "/vssps", o) : AzureDevOpsServicesEndpoints.Cloud;

    public static GitHubEndpoints GhEndpoints =>
        Env("GITHUBMIGRATOR_LIVE_GH_SIM_ORIGIN") is { } o ? new(o + "/api/v3", o) : GitHubEndpoints.Cloud;

    public static HttpClient ReadOnlyClient() =>
        new(new ReadOnlyGuardHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })) { Timeout = TimeSpan.FromSeconds(60) };
}

// The model smoke test may POST, but only a completion request and only to the configured endpoint: nothing else.
public sealed class CompletionOnlyGuardHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            throw new InvalidOperationException($"The model smoke test only sends chat completions: refused {request.Method} {request.RequestUri!.AbsolutePath}");
        return base.SendAsync(request, ct);
    }
}

public sealed class ReadOnlyGuardHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            throw new InvalidOperationException($"Live smoke tests are read-only: refused {request.Method} {request.RequestUri!.Host}");
        return base.SendAsync(request, ct);
    }
}

public sealed class LiveAdoFactAttribute : FactAttribute
{
    public LiveAdoFactAttribute()
    {
        if (LiveEnvironment.Ado is null)
            Skip = "Set GITHUBMIGRATOR_LIVE_ADO_ORG and GITHUBMIGRATOR_LIVE_ADO_PAT to run against a real Azure DevOps Services organization.";
    }
}

public sealed class LiveAiFactAttribute : FactAttribute
{
    public LiveAiFactAttribute()
    {
        if (LiveEnvironment.Ai is null)
            Skip = "Set GITHUBMIGRATOR_LIVE_AI_ENDPOINT, GITHUBMIGRATOR_LIVE_AI_MODEL and GITHUBMIGRATOR_LIVE_AI_KEY (or GITHUBMIGRATOR_LIVE_AI_AUTH=None) to run against a real model.";
    }
}

public sealed class LiveGitHubFactAttribute : FactAttribute
{
    public LiveGitHubFactAttribute()
    {
        if (LiveEnvironment.GitHub is null)
            Skip = "Set GITHUBMIGRATOR_LIVE_GH_ORG and GITHUBMIGRATOR_LIVE_GH_PAT to run against real GitHub.";
    }
}

// What the simulators assume, against what the real service answered. Written once at the end of the run.
public sealed class LiveReport : IDisposable
{
    private readonly List<JsonObject> _entries = [];
    private readonly object _gate = new();

    // status: "confirmed" (real service behaves as the simulator assumes), "differs" (fix the simulator or the product), "info"
    public void Record(string area, string name, string status, string detail)
    {
        lock (_gate)
            _entries.Add(new JsonObject { ["area"] = area, ["name"] = name, ["status"] = status, ["detail"] = detail });
    }

    public void Dispose()
    {
        JsonObject[] snapshot;
        lock (_gate) snapshot = [.. _entries];
        if (snapshot.Length == 0) return;
        var path = LiveEnvironment.Env("GITHUBMIGRATOR_LIVE_REPORT") ?? Path.Combine(Directory.GetCurrentDirectory(), "live-smoke-report.json");
        var report = new JsonObject
        {
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["target"] = LiveEnvironment.Env("GITHUBMIGRATOR_LIVE_ADO_SIM_ORIGIN") is not null || LiveEnvironment.Env("GITHUBMIGRATOR_LIVE_GH_SIM_ORIGIN") is not null || LiveEnvironment.Env("GITHUBMIGRATOR_LIVE_AI_SIM") is not null
                ? "SIMULATORS (dry run: proves the test logic, says nothing about the real services)" : "real services",
            ["entries"] = new JsonArray(snapshot.Select(e => (JsonNode?)e.DeepClone()).ToArray())
        };
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

[CollectionDefinition("live")]
public sealed class LiveCollection : ICollectionFixture<LiveReport>;
