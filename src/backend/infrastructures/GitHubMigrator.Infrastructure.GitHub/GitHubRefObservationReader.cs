using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// GitHub Cloud-only fixed-host, GET-only Git-data reader.
// Matching refs returns the Git ref OID, including an annotated TAG object
// (unlike the repository Tags endpoint, which can report a peeled commit).
public sealed class GitHubRefObservationReader(HttpClient client, TimeProvider clock, GitHubEndpoints? endpoints = null)
    : IGitHubRefObservationReader
{
    private GitHubEndpoints Endpoints => endpoints ?? GitHubEndpoints.Cloud;
    private const int MaxResponseBytes = 2_000_000;

    public async Task<GitHubRefObservation> ReadAsync(
        string organization, string repository, string token, CancellationToken ct)
    {
        // Validate *before* constructing any provider request or reading a PAT.
        if (!GitHubRefObservationIntegrity.ValidOwner(organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(repository) ||
            string.IsNullOrWhiteSpace(token))
            throw new GitHubRefObservationException("InvalidTargetRefRequest");

        var refs = new List<GitHubObservedRef>();
        await ReadNamespaceAsync(organization, repository, "heads", token, refs, ct);
        await ReadNamespaceAsync(organization, repository, "tags", token, refs, ct);
        // Response order cannot affect proof. A network error, cap overflow,
        // duplicate, invalid object type or mixed case collision fails closed.
        return GitHubRefObservationIntegrity.Seal(organization, repository,
            clock.GetUtcNow(), refs);
    }

    private async Task ReadNamespaceAsync(
        string org, string repo, string kind, string token,
        List<GitHubObservedRef> output, CancellationToken ct)
    {
        var url = Endpoints.Api("repos/" +
            Uri.EscapeDataString(org) + "/" + Uri.EscapeDataString(repo) +
            "/git/matching-refs/" + kind);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.7");
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new GitHubRefObservationException("TargetRefProviderUnavailable");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubRefObservationException("TargetRefProviderTimeout");
        }

        using (response)
        {
            // 404 is missing OR hidden; it cannot establish an empty target.
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new GitHubRefObservationException("TargetRepositoryNotVisible");
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                var rateLimited = response.Headers.TryGetValues(
                    "X-RateLimit-Remaining", out var remaining) &&
                    remaining.Any(x => x == "0");
                throw new GitHubRefObservationException(rateLimited
                    ? "TargetRefRateLimited" : "TargetRefAccessDenied");
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new GitHubRefObservationException("TargetRefRateLimited");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new GitHubRefObservationException("TargetRefProviderUnavailable");

            // This GitHub Git-data endpoint does NOT declare a page parameter.
            // Never mislabel a truncated or paginated reply as a complete set.
            if (response.Headers.Contains("Link"))
                throw new GitHubRefObservationException("TargetRefPaginationUnverified");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new GitHubRefObservationException("TargetRefResponseLimit");

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var memory = new MemoryStream();
            var buffer = new byte[16_384];
            while (true)
            {
                var received = await source.ReadAsync(buffer.AsMemory(), ct);
                if (received == 0) break;
                if (received > MaxResponseBytes - memory.Length)
                    throw new GitHubRefObservationException("TargetRefResponseLimit");
                memory.Write(buffer, 0, received);
            }

            JsonDocument document;
            try { document = JsonDocument.Parse(memory.ToArray()); }
            catch (JsonException)
            {
                throw new GitHubRefObservationException("InvalidTargetRefResponse");
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new GitHubRefObservationException("InvalidTargetRefResponse");
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    if (output.Count >= GitMirrorRefReconciler.MaxRefsPerSide ||
                        row.ValueKind != JsonValueKind.Object ||
                        !row.TryGetProperty("ref", out var refValue) ||
                        refValue.ValueKind != JsonValueKind.String ||
                        !row.TryGetProperty("object", out var obj) ||
                        obj.ValueKind != JsonValueKind.Object ||
                        !obj.TryGetProperty("sha", out var oid) ||
                        oid.ValueKind != JsonValueKind.String ||
                        !obj.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String)
                        throw new GitHubRefObservationException("InvalidTargetRefResponse");

                    var name = refValue.GetString() ?? "";
                    var objectType = type.GetString() ?? "";
                    if (!name.StartsWith("refs/" + kind + "/", StringComparison.Ordinal) ||
                        objectType is not ("commit" or "tag") ||
                        (kind == "heads" && objectType != "commit"))
                        throw new GitHubRefObservationException("InvalidTargetRefResponse");
                    output.Add(new(name, oid.GetString() ?? "", objectType));
                }
            }
        }
    }
}
