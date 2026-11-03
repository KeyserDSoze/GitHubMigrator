using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// Fixed api.github.com endpoint; GET only, no redirects/cookies configured in DI.
// A 404 MUST NOT be interpreted as guaranteed name availability.
public sealed class GitHubTargetRepositoryProbe(HttpClient client) : IGitHubTargetRepositoryProbe
{
    public async Task<string> ProbeAsync(string organization, string repository, string token, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.5");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return "NotVisibleOrMissing";
        if (response.StatusCode == HttpStatusCode.Unauthorized) return "Unauthorized";
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return "RateLimited";
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
                values.Any(x => x == "0");
            return exhausted ? "RateLimited" : "Forbidden";
        }
        if (response.StatusCode != HttpStatusCode.OK) return "Unavailable";
        if (response.Content.Headers.ContentLength is > 131_072) return "InvalidResponse";

        // Bound response even when Content-Length is absent. Do not reflect untrusted API JSON.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[131_073];
        var count = 0;
        while (count < bytes.Length)
        {
            var received = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (received == 0) break;
            count += received;
        }
        if (count == bytes.Length) return "InvalidResponse";
        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = json.RootElement;
            var expected = organization + "/" + repository;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("full_name", out var fullName) &&
                fullName.ValueKind == JsonValueKind.String &&
                string.Equals(fullName.GetString(), expected, StringComparison.OrdinalIgnoreCase)
                    ? "Exists" : "InvalidResponse";
        }
        catch (JsonException)
        {
            return "InvalidResponse";
        }
    }
}
