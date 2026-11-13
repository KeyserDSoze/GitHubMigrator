using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// Read-only advisory policy inspector. Never interprets 404 as proof that
// branch protection / inherited rules do not apply. All GitHub endpoints fixed.
public sealed class GitHubImportPolicyReader(
    HttpClient client, TimeProvider clock, GitHubEndpoints? endpoints = null) : IGitHubImportPolicyReader
{
    private GitHubEndpoints Endpoints => endpoints ?? GitHubEndpoints.Cloud;
    private static readonly Regex OrgName = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex RepoName = new(
        "^[A-Za-z0-9_.-]{1,100}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex Branch = new(
        "^[A-Za-z0-9_.-][A-Za-z0-9_.\\/-]{0,254}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public async Task<GitHubImportPolicyObservation> InspectAsync(
        string organization, string repository, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(organization) ||
            string.IsNullOrWhiteSpace(repository) ||
            !OrgName.IsMatch(organization) || !RepoName.IsMatch(repository) ||
            repository is "." or ".." ||
            string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GitHubImportIntentException("ImportPolicyInvalidDestination");

        var root = Endpoints.Api("repos/" +
            Uri.EscapeDataString(organization) + "/" +
            Uri.EscapeDataString(repository));
        var now = clock.GetUtcNow();

        // A default branch can only come from GitHub's exact matching
        // repository metadata. No client-controlled URL or ref is accepted.
        using var repositoryReply = await GetAsync(root, token, ct);
        if (repositoryReply.StatusCode != HttpStatusCode.OK)
            return Unknown("Unavailable", now);
        using var details = await ReadJsonAsync(repositoryReply, ct);
        if (details is null || details.RootElement.ValueKind != JsonValueKind.Object)
            return Unknown("Unavailable", now);
        var meta = details.RootElement;
        if (!TryText(meta, "full_name", out var fullName) ||
            !string.Equals(fullName, organization + "/" + repository,
                StringComparison.OrdinalIgnoreCase) ||
            !TryText(meta, "default_branch", out var defaultBranch) ||
            !Branch.IsMatch(defaultBranch) ||
            defaultBranch is "." or ".." ||
            defaultBranch.Contains("..", StringComparison.Ordinal) ||
            !meta.TryGetProperty("owner", out var owner) ||
            owner.ValueKind != JsonValueKind.Object ||
            !TryText(owner, "login", out var ownerLogin) ||
            !string.Equals(ownerLogin, organization, StringComparison.OrdinalIgnoreCase))
            return Unknown("Unavailable", now);

        using var rulesetReply = await GetAsync(
            root + "/rulesets?includes_parents=true&per_page=100", token, ct);
        if (rulesetReply.StatusCode != HttpStatusCode.OK)
            return Unknown(
                rulesetReply.StatusCode == HttpStatusCode.Forbidden
                    ? "Forbidden" : "Unavailable", now);
        if (rulesetReply.Headers.TryGetValues("Link", out var links) &&
            links.Any(x => x.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
            return Unknown("Incomplete", now);
        using var rules = await ReadJsonAsync(rulesetReply, ct);
        if (rules is null || rules.RootElement.ValueKind != JsonValueKind.Array ||
            rules.RootElement.GetArrayLength() >= 100)
            return Unknown("Incomplete", now);
        var rulesetCount = 0; var active = 0;
        foreach (var rule in rules.RootElement.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object ||
                !TryText(rule, "enforcement", out var enforcement) ||
                enforcement is not ("active" or "evaluate" or "disabled"))
                return Unknown("Unavailable", now);
            rulesetCount++;
            if (enforcement != "disabled") active++;
        }

        // Includes *only* default-branch protection. All other branch
        // names and org policies still require independent execution checks.
        using var branchReply = await GetAsync(root + "/branches/" +
            Uri.EscapeDataString(defaultBranch) + "/protection", token, ct);
        var protection = branchReply.StatusCode switch
        {
            HttpStatusCode.OK => "ProtectionVisible",
            HttpStatusCode.NotFound => "NotVisibleOrMissing",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.TooManyRequests => "RateLimited",
            _ => "Unverified"
        };
        return new("Observed", rulesetCount, active, protection, now);
    }

    private async Task<HttpResponseMessage> GetAsync(
        string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/1.0");
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static bool TryText(JsonElement obj, string key, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(key, out var elem) ||
            elem.ValueKind != JsonValueKind.String) return false;
        value = elem.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<JsonDocument?> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 65_536;
        if (response.Content.Headers.ContentLength is > limit) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[limit + 1];
        var used = 0;
        while (used < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(used), ct);
            if (n == 0) break;
            used += n;
        }
        if (used > limit) return null;
        try { return JsonDocument.Parse(bytes.AsMemory(0, used)); }
        catch (JsonException) { return null; }
    }

    private static GitHubImportPolicyObservation Unknown(
        string status, DateTimeOffset now) =>
        new(status, 0, 0, "Unverified", now);
}
