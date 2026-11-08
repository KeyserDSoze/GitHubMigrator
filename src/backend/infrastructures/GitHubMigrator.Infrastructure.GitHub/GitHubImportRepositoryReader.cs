using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// F-039 GET-only, fixed-host repository metadata inspection. The push field
// is merely GitHub's reported repository permission, NOT proof of token scope,
// ruleset exceptions, or Git network authorization.
public sealed class GitHubImportRepositoryReader(HttpClient client, TimeProvider clock)
    : IGitHubImportRepositoryReader
{
    private static readonly Regex Org = new(
        @"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Repo = new(
        @"^[a-zA-Z0-9_.-]{1,100}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private const int MaxResponse = 65_536;

    public async Task<GitHubImportRepositoryObservation> ObserveAsync(
        string organization, string repository, string token, CancellationToken ct)
    {
        if (organization is null || repository is null ||
            !Org.IsMatch(organization) || organization.Contains("--", StringComparison.Ordinal) ||
            !Repo.IsMatch(repository) || repository is "." or ".." ||
            string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GitHubImportReadinessException("ImportReadinessProbeInvalid");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/" + Uri.EscapeDataString(organization) +
            "/" + Uri.EscapeDataString(repository));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.9");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        var now = clock.GetUtcNow();
        var status = response.StatusCode switch
        {
            HttpStatusCode.NotFound => "NotVisibleOrMissing",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.TooManyRequests => "RateLimited",
            HttpStatusCode.Forbidden => response.Headers.TryGetValues(
                "X-RateLimit-Remaining", out var values) && values.Any(v => v == "0")
                    ? "RateLimited" : "Forbidden",
            HttpStatusCode.OK => "Visible",
            _ => "Unavailable"
        };
        if (status != "Visible")
            return new(status, "Unverified", false, false, now);

        if (response.Content.Headers.ContentLength is > MaxResponse)
            return Invalid(now);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[MaxResponse + 1];
        var used = 0;
        while (used < bytes.Length)
        {
            var n = await body.ReadAsync(bytes.AsMemory(used), ct);
            if (n == 0) break;
            used += n;
        }
        if (used > MaxResponse) return Invalid(now);

        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(0, used));
            var node = json.RootElement;
            if (node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("full_name", out var fullName) ||
                fullName.ValueKind != JsonValueKind.String ||
                !string.Equals(fullName.GetString(),
                    organization + "/" + repository, StringComparison.OrdinalIgnoreCase) ||
                !node.TryGetProperty("owner", out var owner) ||
                owner.ValueKind != JsonValueKind.Object ||
                !owner.TryGetProperty("login", out var login) ||
                login.ValueKind != JsonValueKind.String ||
                !string.Equals(login.GetString(), organization,
                    StringComparison.OrdinalIgnoreCase) ||
                !node.TryGetProperty("archived", out var archived) ||
                archived.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !node.TryGetProperty("disabled", out var disabled) ||
                disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Invalid(now);

            var push = "Unverified";
            if (node.TryGetProperty("permissions", out var permissions) &&
                permissions.ValueKind == JsonValueKind.Object &&
                permissions.TryGetProperty("push", out var mayPush))
                push = mayPush.ValueKind switch
                {
                    JsonValueKind.True => "ReportedAllowed",
                    JsonValueKind.False => "ReportedDenied",
                    _ => "Unverified"
                };
            return new("Visible", push, archived.GetBoolean(),
                disabled.GetBoolean(), now);
        }
        catch (JsonException)
        { return Invalid(now); }
    }

    private static GitHubImportRepositoryObservation Invalid(DateTimeOffset now) =>
        new("InvalidResponse", "Unverified", false, false, now);
}
