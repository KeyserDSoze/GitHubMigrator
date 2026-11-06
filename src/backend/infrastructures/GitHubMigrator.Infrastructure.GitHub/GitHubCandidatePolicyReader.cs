using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitHub;

// Only GET calls to fixed api.github.com endpoints. F-046 does not mint a
// token, send any Git command, or infer authorization to create remote refs.
// HTTP errors, redirects, incomplete pagination and untrusted JSON are UNKNOWN.
public sealed class GitHubCandidatePolicyReader(
    HttpClient client, TimeProvider clock) : IGitHubCandidatePolicyReader
{
    private const int MaxBody = 65_536;
    private const int MaxPages = 100;

    public async Task<GitHubCandidatePolicyObservation> ReadAsync(
        string organization, string repository, long expectedRepositoryId,
        string readOnlyToken, IReadOnlyList<GitMirrorRef> candidates,
        CancellationToken ct)
    {
        if (!GitHubRefObservationIntegrity.ValidOwner(organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(repository) ||
            expectedRepositoryId <= 0 || string.IsNullOrWhiteSpace(readOnlyToken) ||
            readOnlyToken.Any(char.IsControl) || candidates is null ||
            candidates.Count is < 1 or > GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            candidates.Any(x => x is null ||
                !x.Name.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                !x.Name.StartsWith("refs/tags/", StringComparison.Ordinal)) ||
            !GitMirrorRefReconciler.CanonicalizeObservedRefs(candidates)
                .SequenceEqual(candidates))
            throw new GitHubCandidatePolicyException("CandidatePolicyRequestInvalid");

        var root = "https://api.github.com/repos/" +
            Uri.EscapeDataString(organization) + "/" +
            Uri.EscapeDataString(repository);

        // Repository identity must still be the numeric F-043 destination
        // before querying policies. 404 can never mean an empty repo.
        using var metaReply = await GetAsync(root, readOnlyToken, ct);
        using var meta = await ReadArrayOrObjectAsync(metaReply, ct);
        if (meta is null || meta.RootElement.ValueKind != JsonValueKind.Object ||
            !ValidRepository(meta.RootElement, organization, repository,
                expectedRepositoryId))
            return Unverified(candidates, "RepoUnverified");

        // Global catalog is necessary even for branch-specific rules: push
        // rulesets can affect BOTH heads and tags; tag rulesets aren't
        // represented by the branch-specific endpoint.
        using var globalReply = await GetAsync(
            root + "/rulesets?includes_parents=true&per_page=100", readOnlyToken, ct);
        using var catalog = await ReadArrayOrObjectAsync(globalReply, ct);
        if (catalog is null || catalog.RootElement.ValueKind != JsonValueKind.Array ||
            !Complete(globalReply, catalog.RootElement) ||
            !ParseGlobal(catalog.RootElement, out var hasPushRules,
                out var hasTagRules))
            return Unverified(candidates, "CatalogUnverified");

        // Limited parallel GETs for head rules, never one unbounded task per
        // source ref. A missing branch may still have applicable rules:
        // GitHub's branch-rules endpoint explicitly supports absent names.
        using var permits = new SemaphoreSlim(4);
        var tasks = candidates.Select(async candidate =>
        {
            if (candidate.Name.StartsWith("refs/tags/", StringComparison.Ordinal))
                return new GitHubCandidateRuleStatus(
                    GitHubCandidatePolicyIntegrity.RefNameDigest(candidate.Name),
                    "Tag",
                    hasTagRules || hasPushRules
                        ? GitHubCandidatePolicyStates.RulesRequireReview
                        : GitHubCandidatePolicyStates.NoActiveRulesReported,
                    hasTagRules || hasPushRules ? 1 : 0, "Observed");

            await permits.WaitAsync(ct);
            try
            {
                var branchName = candidate.Name["refs/heads/".Length..];
                var url = root + "/rules/branches/" +
                    Uri.EscapeDataString(branchName) + "?per_page=100";
                using var response = await GetAsync(url, readOnlyToken, ct);
                using var json = await ReadArrayOrObjectAsync(response, ct);
                if (json is null || json.RootElement.ValueKind != JsonValueKind.Array ||
                    !Complete(response, json.RootElement) ||
                    !ParseBranchRules(json.RootElement, out var branchCount))
                    return Unknown(candidate.Name, "BranchUnverified");
                var active = hasPushRules || branchCount > 0;
                return new GitHubCandidateRuleStatus(
                    GitHubCandidatePolicyIntegrity.RefNameDigest(candidate.Name),
                    "Branch", active
                        ? GitHubCandidatePolicyStates.RulesRequireReview
                        : GitHubCandidatePolicyStates.NoActiveRulesReported,
                    active ? Math.Max(branchCount, 1) : 0, "Observed");
            }
            catch (HttpRequestException)
            {
                return Unknown(candidate.Name, "BranchUnverified");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Unknown(candidate.Name, "BranchUnverified");
            }
            finally { permits.Release(); }
        }).ToArray();
        var observations = await Task.WhenAll(tasks);
        return new(observations, clock.GetUtcNow(), "Observed");
    }

    private static GitHubCandidatePolicyObservation Unverified(
        IReadOnlyList<GitMirrorRef> refs, string status) =>
        new(refs.Select(x => Unknown(x.Name, status)).ToArray(),
            DateTimeOffset.UtcNow, status);

    private static GitHubCandidateRuleStatus Unknown(
        string name, string providerStatus) => new(
            GitHubCandidatePolicyIntegrity.RefNameDigest(name),
            name.StartsWith("refs/tags/", StringComparison.Ordinal) ? "Tag" : "Branch",
            GitHubCandidatePolicyStates.PolicyUnverified, 0, providerStatus);

    private async Task<HttpResponseMessage> GetAsync(
        string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitHubMigrator/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        // The injected client is configured without redirects or cookies.
        try
        {
            return await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }
    }

    private static bool Complete(HttpResponseMessage response, JsonElement data)
    {
        if (data.GetArrayLength() >= MaxPages) return false;
        if (response.Headers.TryGetValues("Link", out var links) &&
            links.Any(x => x.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    private static bool ValidRepository(
        JsonElement json, string org, string repo, long expectedId) =>
        TryLong(json, "id", out var id) && id == expectedId &&
        TryText(json, "full_name", out var full) &&
        string.Equals(full, org + "/" + repo, StringComparison.OrdinalIgnoreCase) &&
        json.TryGetProperty("owner", out var owner) &&
        owner.ValueKind == JsonValueKind.Object &&
        TryText(owner, "login", out var login) &&
        string.Equals(login, org, StringComparison.OrdinalIgnoreCase) &&
        json.TryGetProperty("archived", out var archived) &&
        archived.ValueKind == JsonValueKind.False &&
        json.TryGetProperty("disabled", out var disabled) &&
        disabled.ValueKind == JsonValueKind.False;

    private static bool ParseGlobal(
        JsonElement root, out bool push, out bool tag)
    {
        push = false; tag = false;
        foreach (var obj in root.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object ||
                !TryText(obj, "target", out var target) ||
                target is not ("push" or "tag" or "branch") ||
                !TryText(obj, "enforcement", out var state) ||
                state is not ("active" or "evaluate" or "disabled"))
                return false;
            if (state == "active")
            {
                if (target == "push") push = true;
                if (target == "tag") tag = true;
            }
        }
        return true;
    }

    private static bool ParseBranchRules(JsonElement root, out int count)
    {
        count = 0;
        foreach (var obj in root.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object ||
                !TryText(obj, "type", out var kind) ||
                kind.Length > 80)
                return false;
            count++;
        }
        return true;
    }

    private static bool TryText(
        JsonElement obj, string key, out string text)
    {
        text = "";
        if (!obj.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryLong(JsonElement obj, string key, out long value)
    {
        value = 0;
        return obj.TryGetProperty(key, out var json) &&
            json.ValueKind == JsonValueKind.Number &&
            json.TryGetInt64(out value);
    }

    private static async Task<JsonDocument?> ReadArrayOrObjectAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaxBody ||
            response.Content.Headers.ContentType?.MediaType is not
                ("application/json" or "application/vnd.github+json") &&
            response.Content.Headers.ContentType is not null)
            return null;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[MaxBody + 1];
        var used = 0;
        while (used < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(used), ct);
            if (read == 0) break;
            used += read;
        }
        if (used > MaxBody) return null;
        try
        {
            var doc = JsonDocument.Parse(bytes.AsMemory(0, used),
                new JsonDocumentOptions { MaxDepth = 24 });
            if (!HasUniqueMembers(doc.RootElement))
            {
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (JsonException) { return null; }
    }

    private static bool HasUniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(HasUniqueMembers);
        if (value.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!names.Add(item.Name) || !HasUniqueMembers(item.Value))
                return false;
        return true;
    }
}
