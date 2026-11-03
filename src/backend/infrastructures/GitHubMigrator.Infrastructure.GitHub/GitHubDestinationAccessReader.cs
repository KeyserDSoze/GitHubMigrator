using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// Cloud-only, fixed-host read adapter. Provider JSON is never returned wholesale to the client.
public sealed class GitHubDestinationAccessReader(HttpClient client) : IGitHubDestinationAccessReader
{
    public async Task<GitHubDestinationAccessEvidence> ReadAsync(
        string organization, string expectedLogin, string token, CancellationToken ct)
    {
        using var member = await GetAsync(
            $"https://api.github.com/user/memberships/orgs/{Uri.EscapeDataString(organization)}",
            token, ct);
        var status = member.StatusCode;
        if (status == HttpStatusCode.Unauthorized) return Unknown("Unauthorized");
        if (status == HttpStatusCode.NotFound) return Unknown("NotConfirmed");
        if (status == HttpStatusCode.TooManyRequests || IsRateLimited(member))
            return Unknown("RateLimited");
        if (status == HttpStatusCode.Forbidden) return Unknown("Forbidden");
        if (status != HttpStatusCode.OK) return Unknown("Unavailable");

        using var membership = await ReadBoundedJsonAsync(member, ct);
        if (membership is null || membership.RootElement.ValueKind != JsonValueKind.Object)
            return Unknown("InvalidResponse");
        var root = membership.RootElement;
        if (!TryString(root, "state", out var memberState) ||
            !TryString(root, "role", out var memberRole) ||
            !root.TryGetProperty("organization", out var org) ||
            org.ValueKind != JsonValueKind.Object ||
            !TryString(org, "login", out var orgLogin) ||
            !string.Equals(orgLogin, organization, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("user", out var user) ||
            user.ValueKind != JsonValueKind.Object ||
            !TryString(user, "login", out var login) ||
            !string.Equals(login, expectedLogin, StringComparison.OrdinalIgnoreCase))
            return Unknown("IdentityMismatch");

        if (memberState == "pending") return Unknown("Pending");
        if (memberState != "active" || memberRole is not ("admin" or "member"))
            return Unknown("InvalidResponse");

        // A membership is confirmed, but not write privileges. Organization policy is only
        // an advisory setting and may be unavailable to this token.
        var role = memberRole == "admin" ? "Owner" : "Member";
        var policy = await ReadPrivateRepositoryPolicyAsync(organization, token, ct);
        return new("Active", role, policy);
    }

    private async Task<string> ReadPrivateRepositoryPolicyAsync(
        string organization, string token, CancellationToken ct)
    {
        using var response = await GetAsync(
            $"https://api.github.com/orgs/{Uri.EscapeDataString(organization)}", token, ct);
        if (response.StatusCode != HttpStatusCode.OK) return "Unverified";
        using var document = await ReadBoundedJsonAsync(response, ct);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            return "Unverified";
        var root = document.RootElement;
        if (!TryString(root, "login", out var login) ||
            !string.Equals(login, organization, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("members_can_create_private_repositories", out var option))
            return "Unverified";
        return option.ValueKind switch
        {
            JsonValueKind.True => "AllowedForMembers",
            JsonValueKind.False => "RestrictedForMembers",
            _ => "Unverified"
        };
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.6");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden &&
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
        values.Any(x => x == "0");

    private static bool TryString(JsonElement source, string key, out string value)
    {
        value = "";
        if (!source.TryGetProperty(key, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<JsonDocument?> ReadBoundedJsonAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 131_072;
        if (response.Content.Headers.ContentLength is > limit) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[limit + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (n == 0) break;
            count += n;
        }
        if (count > limit) return null;
        try { return JsonDocument.Parse(bytes.AsMemory(0, count)); }
        catch (JsonException) { return null; }
    }

    private static GitHubDestinationAccessEvidence Unknown(string status) =>
        new(status, "Unknown", "Unverified");
}
