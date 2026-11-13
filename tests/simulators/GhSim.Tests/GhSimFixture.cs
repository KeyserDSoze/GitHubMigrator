using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhSim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GhSim.Tests;

public sealed record Reply(int Status, JsonElement Json, HttpResponseHeaders Headers, string Text);

// GhSim runs in-process on a random port with a throw-away data directory; only the git CLI is required.
public sealed class GhSimFixture : IAsyncLifetime
{
    public const string AdminToken = "gh-sim-pat";
    private string _dir = "";
    public WebApplication App { get; private set; } = null!;
    public string Origin { get; private set; } = "";
    public string Api => Origin + "/api/v3";
    public HttpClient Http { get; } = new();

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ghsim-" + Guid.NewGuid().ToString("N"));
        App = await GhSimApp.StartAsync(new GhSimOptions(GhSeed.Load(null), _dir, "http://127.0.0.1:0"));
        Origin = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        GhStore.ForceDelete(_dir);
    }

    public async Task<Reply> SendAsync(string method, string path, object? body = null, string? token = AdminToken,
        string? accept = null)
    {
        var url = path.StartsWith("http") ? path : (path.StartsWith("/_sim") ? Origin + path : Api + path);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (text.Length > 0) { try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { } }
        return new((int)response.StatusCode, json, response.Headers, text);
    }

    public Task<Reply> Get(string path, string? token = AdminToken) => SendAsync("GET", path, null, token);
    public Task<Reply> Post(string path, object body, string? token = AdminToken) => SendAsync("POST", path, body, token);
    public Task<Reply> Put(string path, object body, string? token = AdminToken) => SendAsync("PUT", path, body, token);
    public Task<Reply> Delete(string path, string? token = AdminToken) => SendAsync("DELETE", path, null, token);

    public string RemoteUrl(string slug, string token = AdminToken) =>
        $"http://x-access-token:{token}@{new Uri(Origin).Authority}/{slug}.git";

    public async Task<string> CreateRepoAsync(string name, string org = "orion-org")
    {
        var created = await Post($"/orgs/{org}/repos", new { name, @private = true });
        Assert.Equal(201, created.Status);
        return $"{org}/{name}";
    }

    public static (int Exit, string Output) Git(string? workDir, params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir ?? Path.GetTempPath() };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_AUTHOR_NAME"] = info.Environment["GIT_COMMITTER_NAME"] = "Test";
        info.Environment["GIT_AUTHOR_EMAIL"] = info.Environment["GIT_COMMITTER_EMAIL"] = "test@example.test";
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }

    public static string MustGit(string? workDir, params string[] args)
    {
        var (exit, output) = Git(workDir, args);
        Assert.True(exit == 0, $"git {string.Join(' ', args)} failed: {output}");
        return output.Trim();
    }

    // A local work repository with main (2 commits), a feature branch, a lightweight and an annotated tag.
    public static string NewLocalRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ghsim-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        MustGit(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        MustGit(dir, "add", ".");
        MustGit(dir, "commit", "-qm", "c1");
        File.AppendAllText(Path.Combine(dir, "a.txt"), "two\n");
        MustGit(dir, "commit", "-qam", "c2");
        MustGit(dir, "branch", "feature/x");
        MustGit(dir, "tag", "light");
        MustGit(dir, "tag", "-a", "v1", "-m", "release v1");
        return dir;
    }

    public static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
