using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// Explicit existence probes against one fixed API hostname, no redirects/cookies.
// These probes do NOT create an issue, link a commit or amend a pull request.
public sealed class GitHubTraceabilityReader(HttpClient client) : IGitHubTraceabilityReader
{
    public Task<bool> IssueExistsAsync(string organization, string repo, int number, string token, CancellationToken ct) =>
        ExistsAsync(organization, repo, $"issues/{number}", token, "issue", ct);
    public Task<bool> CommitExistsAsync(string organization, string repo, string sha, string token, CancellationToken ct) =>
        ExistsAsync(organization, repo, $"commits/{Uri.EscapeDataString(sha)}", token, "commit", ct);
    public Task<bool> PullRequestExistsAsync(string organization, string repo, int number, string token, CancellationToken ct) =>
        ExistsAsync(organization, repo, $"pulls/{number}", token, "pr", ct);

    private async Task<bool> ExistsAsync(string org, string repo, string path, string token,
        string kind, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(repo)}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.4");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("TraceabilityLookupUnavailable");
        if (response.Content.Headers.ContentLength is > 500_000)
            throw new HttpRequestException("TraceabilityResponseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new HttpRequestException("UnexpectedTraceabilityResponse");
        if (kind == "issue")
            return root.TryGetProperty("number", out var issueNum) && issueNum.ValueKind == JsonValueKind.Number &&
                   !root.TryGetProperty("pull_request", out _);
        if (kind == "pr")
            return root.TryGetProperty("number", out var prNum) && prNum.ValueKind == JsonValueKind.Number &&
                   root.TryGetProperty("base", out _);
        return root.TryGetProperty("sha", out var value) && value.ValueKind == JsonValueKind.String &&
               path.EndsWith(value.GetString()!, StringComparison.OrdinalIgnoreCase);
    }
}
