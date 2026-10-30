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
        var secrets = await ReadNamesAsync($"{root}/secrets", token, "secrets", ct);
        var variables = await ReadNamesAsync($"{root}/variables", token, "variables", ct);
        return new(secrets, variables);
    }

    private async Task<string[]> ReadNamesAsync(string root, string token, string property, CancellationToken ct)
    {
        var names = new List<string>();
        for (var page = 1; page <= 5; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{root}?per_page=100&page={page}");
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
            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String)
                    names.Add(name.GetString()!);
            }
            var total = json.RootElement.TryGetProperty("total_count", out var n) && n.TryGetInt32(out var count)
                ? count : 0;
            if (arr.GetArrayLength() < 100 && (total == 0 || names.Count >= total))
                return names.ToArray();
            if (page == 5) throw new HttpRequestException("TargetConfigurationTruncated");
        }
        return names.ToArray();
    }
}
