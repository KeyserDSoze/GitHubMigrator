using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdoSim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AdoSim.Tests;

// An in-process simulator on an ephemeral port with its own temporary data directory.
public sealed class SimHost : IAsyncDisposable
{
    public const string Pat = "sim-pat";
    private static readonly SemaphoreSlim Throttle = new(6);

    public WebApplication App { get; private set; } = null!;
    public string Dir { get; }
    public string Origin { get; private set; } = "";
    public string Flavor { get; private init; } = SimFlavors.Server;
    public string Root => Origin + (Flavor == SimFlavors.Services ? "" : "/tfs") + "/DefaultCollection";
    // Services only: the Graph API lives under its own prefix (its own host on dev.azure.com).
    public string GraphRoot => Origin + "/vssps/DefaultCollection";
    public SeedSpec Seed { get; }
    public HttpClient Http { get; } = new();
    private readonly bool _ownsDir;
    private readonly int _refsPage;

    private SimHost(SeedSpec seed, string dir, bool ownsDir, int refsPage)
    {
        Seed = seed; Dir = dir; _ownsDir = ownsDir; _refsPage = refsPage;
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Pat)));
    }

    public static async Task<SimHost> StartAsync(string? profile = "default", string? bulk = null, string? dir = null,
        int refsPage = 50, SeedSpec? seed = null, string flavor = SimFlavors.Server)
    {
        var spec = seed ?? SeedSpec.Load(profile, bulk);
        var host = new SimHost(spec, dir ?? Path.Combine(Path.GetTempPath(), "adosim-test-" + Guid.NewGuid().ToString("N")),
            dir is null, refsPage) { Flavor = flavor };
        await host.StartAppAsync();
        return host;
    }

    public async Task StartAppAsync()
    {
        await Throttle.WaitAsync();
        try
        {
            App = await AdoSimApp.StartAsync(new SimOptions(Seed, Dir, Pat,
                Flavor == SimFlavors.Services ? "7.1" : "7.0", _refsPage, "http://127.0.0.1:0", Flavor, GraphPageSize: 3));
        }
        finally { Throttle.Release(); }
        Origin = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task StopAppAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAppAsync();
        Http.Dispose();
        if (_ownsDir) DeleteDir(Dir);
    }

    public static void DeleteDir(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch (IOException) { /* temp data only */ }
    }

    public static string Esc(string text) => Uri.EscapeDataString(text);

    // `path` is relative to /tfs/{collection}; api-version is appended unless already present.
    public async Task<HttpResponseMessage> GetAsync(string path, string apiVersion = "7.0")
    {
        var url = Root + path;
        if (!path.Contains("api-version=")) url += (path.Contains('?') ? "&" : "?") + "api-version=" + apiVersion;
        return await Http.GetAsync(url);
    }

    public async Task<JsonElement> JsonAsync(string path, string apiVersion = "7.0")
    {
        using var response = await GetAsync(path, apiVersion);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path} -> {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<JsonElement> ExpectedAsync()
    {
        using var open = new HttpClient();
        return JsonDocument.Parse(await open.GetStringAsync(Origin + "/_sim/expected")).RootElement.Clone();
    }

    public async Task<HttpResponseMessage> PostCommitAsync(string project, string repo, object body, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Origin}/_sim/repos/{Esc(project)}/{Esc(repo)}/commits")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (!authenticated) request.Headers.Authorization = null;
        using var client = authenticated ? null : new HttpClient();
        return await (client ?? Http).SendAsync(request);
    }

    public string CloneUrl(string project, string repo, string pat = Pat) =>
        $"http://x:{pat}@{new Uri(Origin).Authority}{(Flavor == SimFlavors.Services ? "" : "/tfs")}/DefaultCollection/{Esc(project)}/_git/{Esc(repo)}";

    public async Task<string> CloneAsync(string project, string repo, string? into = null)
    {
        var target = into ?? Path.Combine(Dir, "clones", Guid.NewGuid().ToString("N"), repo);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await Git.MustAsync(null, null, "clone", "--quiet", CloneUrl(project, repo), target);
        return target;
    }

    public async Task<Dictionary<string, string>> RefsAsync(string project, string repo)
    {
        var refs = new Dictionary<string, string>();
        string? token = null;
        do
        {
            using var response = await GetAsync($"/{Esc(project)}/_apis/git/repositories/{Esc(repo)}/refs" +
                (token is null ? "" : "?continuationToken=" + token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var r in doc.RootElement.GetProperty("value").EnumerateArray())
                refs[r.GetProperty("name").GetString()!] = r.GetProperty("objectId").GetString()!;
            token = response.Headers.TryGetValues("X-MS-ContinuationToken", out var values) ? values.First() : null;
        } while (token is not null);
        return refs;
    }

    // name -> sha of every ref of every non-disabled repository, keyed by "project/repo".
    public async Task<SortedDictionary<string, string>> AllRefsAsync()
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in Seed.Projects.Where(p => !string.Equals(p.SourceControl, "Tfvc", StringComparison.OrdinalIgnoreCase)))
            foreach (var repo in (project.Repos ?? []).Where(r => !r.Disabled))
                foreach (var (name, sha) in await RefsAsync(project.Name, repo.Name))
                    all[$"{project.Name}/{repo.Name}:{name}"] = sha;
        return all;
    }
}

public static class JsonExtensions
{
    public static JsonElement[] Items(this JsonElement list) => list.GetProperty("value").EnumerateArray().ToArray();
    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;
}
