using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// F-053 is a future post-send readback adapter, not registered as a writer,
// worker or HTTP API endpoint. All requests target fixed GitHub REST GETs.
public sealed class GitHubRemoteTipObjectReader(HttpClient client, TimeProvider clock)
    : IGitHubRemoteTipObjectReader
{
    private const int MaxObjectResponseBytes = 262_144;

    public async Task<GitHubRemoteTipSnapshot> ReadAsync(
        string organization, string repository, string token,
        IReadOnlyList<GitMirrorRef> createCandidates, CancellationToken ct)
    {
        // Validate before ALL network I/O. Credentials remain in memory only.
        if (!GitHubRefObservationIntegrity.ValidOwner(organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(repository) ||
            string.IsNullOrWhiteSpace(token))
            throw new GitHubRemoteTipException("RemoteTipRequestInvalid");
        var candidates = GitHubRemoteTipIntegrity.CanonicalCandidates(
            createCandidates);
        var refsReader = new GitHubRefObservationReader(client, clock);
        var before = await refsReader.ReadAsync(
            organization, repository, token, ct);
        var known = before.Refs.ToDictionary(x => x.Name,
            StringComparer.Ordinal);
        foreach (var candidate in candidates)
            if (!known.TryGetValue(candidate.Name, out var found) ||
                found.ObjectId != candidate.ObjectId)
                throw new GitHubRemoteTipException("RemoteTipRefMissingOrDrifted");

        var tips = new List<GitHubRemoteTipObject>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var reference = known[candidate.Name];
            var current = candidate.ObjectId;
            var depth = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            if (reference.ObjectType == "tag")
            {
                while (true)
                {
                    if (depth == GitHubRemoteTipIntegrity.MaxTagDepth ||
                        !visited.Add(current))
                        throw new GitHubRemoteTipException("RemoteTipTagChainInvalid");
                    using var tag = await GetObjectAsync(organization, repository,
                        "tags", current, token, ct);
                    if (!TryString(tag.RootElement, "sha", out var oid) ||
                        oid != current ||
                        !tag.RootElement.TryGetProperty("object", out var target) ||
                        target.ValueKind != JsonValueKind.Object ||
                        !TryString(target, "sha", out var next) ||
                        !GitHubRemoteTipIntegrity.ValidObjectId(next) ||
                        next.Length != current.Length ||
                        !TryString(target, "type", out var type) ||
                        type is not ("tag" or "commit"))
                        throw new GitHubRemoteTipException("RemoteTipTagChainInvalid");
                    depth++;
                    current = next;
                    if (type == "commit") break;
                }
            }

            using var commit = await GetObjectAsync(organization, repository,
                "commits", current, token, ct);
            if (!TryString(commit.RootElement, "sha", out var commitOid) ||
                commitOid != current ||
                !commit.RootElement.TryGetProperty("tree", out var tree) ||
                tree.ValueKind != JsonValueKind.Object ||
                !TryString(tree, "sha", out var treeOid) ||
                !GitHubRemoteTipIntegrity.ValidObjectId(treeOid) ||
                treeOid.Length != current.Length)
                throw new GitHubRemoteTipException("RemoteTipCommitInvalid");
            tips.Add(new(candidate.Name, candidate.ObjectId,
                reference.ObjectType, current, treeOid, depth));
        }

        // Re-observe all refs after the object queries. Drift on an unrelated
        // ref is just as important as a candidate ref changing.
        var after = await refsReader.ReadAsync(
            organization, repository, token, ct);
        if (GitHubRemoteRefFenceIntegrity.Fingerprint(before.Refs) !=
            GitHubRemoteRefFenceIntegrity.Fingerprint(after.Refs))
            throw new GitHubRemoteTipException("RemoteTipRefChangedDuringReadback");
        return new(after, GitHubRemoteTipIntegrity.Seal(
            after, candidates, clock.GetUtcNow(), tips));
    }

    private async Task<JsonDocument> GetObjectAsync(
        string organization, string repository, string kind,
        string oid, string token, CancellationToken ct)
    {
        if (kind is not ("tags" or "commits") ||
            !GitHubRemoteTipIntegrity.ValidObjectId(oid))
            throw new GitHubRemoteTipException("RemoteTipRequestInvalid");

        var url = "https://api.github.com/repos/" +
            Uri.EscapeDataString(organization) + "/" +
            Uri.EscapeDataString(repository) + "/git/" + kind + "/" + oid;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/0.7");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new GitHubRemoteTipException("RemoteTipProviderUnavailable");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubRemoteTipException("RemoteTipProviderTimeout");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new GitHubRemoteTipException("RemoteTipObjectNotVisible");
            if (response.StatusCode is HttpStatusCode.Forbidden or
                HttpStatusCode.Unauthorized)
                throw new GitHubRemoteTipException("RemoteTipAccessDenied");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new GitHubRemoteTipException("RemoteTipRateLimited");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new GitHubRemoteTipException("RemoteTipProviderUnavailable");
            if (response.Headers.Contains("Link"))
                throw new GitHubRemoteTipException("RemoteTipPaginationUnverified");
            if (response.Content.Headers.ContentLength is > MaxObjectResponseBytes)
                throw new GitHubRemoteTipException("RemoteTipResponseLimit");

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await input.ReadAsync(chunk.AsMemory(), ct);
                if (count == 0) break;
                if (count > MaxObjectResponseBytes - buffer.Length)
                    throw new GitHubRemoteTipException("RemoteTipResponseLimit");
                buffer.Write(chunk, 0, count);
            }
            try
            {
                var document = JsonDocument.Parse(buffer.ToArray());
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document;
                document.Dispose();
            }
            catch (JsonException) { }
            throw new GitHubRemoteTipException("RemoteTipResponseInvalid");
        }
    }

    private static bool TryString(
        JsonElement parent, string property, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(property, out var item) ||
            item.ValueKind != JsonValueKind.String)
            return false;
        value = item.GetString() ?? "";
        return true;
    }
}
