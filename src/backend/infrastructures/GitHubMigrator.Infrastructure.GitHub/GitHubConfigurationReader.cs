using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// Secret VALUES cannot be read back through GitHub API. Names only.
public sealed class GitHubConfigurationReader(HttpClient client) : IGitHubConfigurationReader
{
    public async Task<GitHubConfigNames> GetNamesAsync(string organization, string repository,
        string token, CancellationToken ct)
    {
        var root = $"https://api.github.com/repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}/actions";
        var secrets = await ReadNamesAsync($"{root}/secrets?per_page=100", token, "secrets", ct);
        var variables = await ReadNamesAsync($"{root}/variables?per_page=100", token, "variables", ct);
        return new(secrets, variables);
    }

    private async Task<string[]> ReadNamesAsync(string url, string token, string property, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("GitHubMigrator/0.4");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("TargetConfigurationUnavailable");
        if (response.Content.Headers.ContentLength is > 2_000_000)
            throw new HttpRequestException("ResponseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!json.RootElement.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new HttpRequestException("UnexpectedTargetResponse");
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object &&
                e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
    }
}
