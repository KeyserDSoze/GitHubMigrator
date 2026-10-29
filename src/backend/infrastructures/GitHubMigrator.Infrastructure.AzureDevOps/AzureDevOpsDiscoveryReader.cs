using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

// F-003 read-only inventory. Every request is built from a fixed Microsoft hostname.
// No custom URL, redirects, cookies, or user-provided request path is accepted.
public sealed class AzureDevOpsDiscoveryReader(HttpClient client, TimeProvider clock) : IDiscoveryReader
{
    public string ProviderKey => "azure-devops-services";
    private const int MaxPages = 30;
    private const int MaxProjects = 2000;
    private const int MaxRefsPerRepository = 30000;
    private const int MaxUsers = 15000;

    public async Task<DiscoveryInventory> DiscoverAsync(Guid connectionId, string organization, string accessToken, CancellationToken ct)
    {
        if (connectionId == Guid.Empty || string.IsNullOrWhiteSpace(organization) ||
            !System.Text.RegularExpressions.Regex.IsMatch(organization, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$"))
            throw new ArgumentException("Invalid source connection.");
        var issues = new List<DiscoveryIssue>();
        var projects = new List<SourceProject>();
        var references = new List<RepositoryRef>();
        var identities = new List<SourceIdentity>();
        var root = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}";
        var graphRoot = $"https://vssps.dev.azure.com/{Uri.EscapeDataString(organization)}";

        var projectItems = await ReadProjectsAsync(root, accessToken, issues, ct);
        foreach (var project in projectItems)
        {
            ct.ThrowIfCancellationRequested();
            var projectId = Text(project, "id");
            var projectName = Text(project, "name");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(projectName))
                throw new DiscoveryProviderException("UnexpectedResponse");
            var projectUri = $"{root}/{Uri.EscapeDataString(projectId)}";
            var sourceRepositories = new List<SourceRepository>();
            string? sourceControlType = null;
            try
            {
                using var details = await GetAsync($"{root}/_apis/projects/{Uri.EscapeDataString(projectId)}?includeCapabilities=true&api-version=7.1",
                    accessToken, ct);
                if (details.Json.RootElement.TryGetProperty("capabilities", out var capabilities) &&
                    capabilities.TryGetProperty("versioncontrol", out var versionControl))
                    sourceControlType = Text(versionControl, "sourceControlType");
            }
            catch (DiscoveryProviderException)
            {
                issues.Add(new("ProjectCapabilitiesUnavailable", projectId));
            }

            try
            {
                using var repos = await GetAsync($"{projectUri}/_apis/git/repositories?api-version=7.1", accessToken, ct);
                foreach (var entry in Items(repos.Json))
                {
                    var repoId = Text(entry, "id");
                    var repoName = Text(entry, "name");
                    if (string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(repoName)) continue;
                    sourceRepositories.Add(new(repoId, projectId, repoName, VersionControlKind.Git));
                    try
                    {
                        var refs = await ReadRefsAsync(projectUri, repoId, accessToken, issues, ct);
                        references.AddRange(refs);
                    }
                    catch (DiscoveryProviderException ex)
                    {
                        issues.Add(new(ex.Code, projectId, repoId));
                    }
                }
            }
            catch (DiscoveryProviderException ex)
            {
                issues.Add(new(ex.Code, projectId));
            }

            if (string.Equals(sourceControlType, "Tfvc", StringComparison.OrdinalIgnoreCase))
            {
                // REST returns TFVC branch ROOTS, not all versioned paths or full changeset history.
                try
                {
                    using var branches = await GetAsync($"{projectUri}/_apis/tfvc/branches?includeChildren=true&api-version=7.1",
                        accessToken, ct);
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var branch in Items(branches.Json)) CollectTfvcPaths(branch, paths, 0);
                    // Always include the project root for TFVC code not configured as a branch.
                    paths.Add($"$/{projectName}");
                    foreach (var path in paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        sourceRepositories.Add(new($"tfvc:{projectId}:{path}", projectId,
                            path.Split('/').Last(), VersionControlKind.Tfvc, path));
                    }
                    issues.Add(new("TfvcPathsRequireFullInventory", projectId));
                }
                catch (DiscoveryProviderException ex)
                {
                    issues.Add(new(ex.Code, projectId));
                    sourceRepositories.Add(new($"tfvc:{projectId}:$/{projectName}", projectId,
                        projectName, VersionControlKind.Tfvc, $"$/{projectName}"));
                    issues.Add(new("TfvcPathsRequireFullInventory", projectId));
                }
            }
            else if (sourceControlType is null)
            {
                issues.Add(new("SourceControlTypeUnknown", projectId));
            }

            projects.Add(new(projectId, projectName, sourceRepositories));
        }

        var identitiesComplete = true;
        try
        {
            identities.AddRange(await ReadUsersAsync(graphRoot, accessToken, issues, ct));
            if (issues.Any(x => x.Code is "GraphUsersTruncated")) identitiesComplete = false;
        }
        catch (DiscoveryProviderException ex)
        {
            identitiesComplete = false;
            issues.Add(new(ex.Code));
        }

        // Capture raw group/ACE evidence; it must not be confused with effective permissions.
        var security = await new AzureDevOpsSecurityReader(client).ReadAsync(
            organization, accessToken, projects, ct);
        issues.AddRange(security.Issues);
        issues.Add(new("EffectivePermissionsNotVerified"));
        var timestamp = clock.GetUtcNow();
        var snapshotId = Guid.NewGuid();
        var snapshot = new DiscoverySnapshot
        {
            SnapshotId = snapshotId,
            SourceSystem = SourceSystemKind.AzureDevOpsServices,
            CollectedAt = timestamp,
            Projects = projects,
            Identities = identities,
            AccessGrants = [],
            PermissionsComplete = false
        };
        return new DiscoveryInventory
        {
            Id = snapshotId,
            ConnectionId = connectionId,
            SourceProvider = ProviderKey,
            Organization = organization,
            CollectedAtUtc = timestamp,
            Snapshot = snapshot,
            Refs = references,
            Issues = issues,
            Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = identitiesComplete,
            PermissionsComplete = false,
            Groups = security.Groups,
            Memberships = security.Memberships,
            AclEvidence = security.Acls,
            SecurityCoverage = security.Coverage
        };
    }

    private async Task<IReadOnlyList<JsonElement>> ReadProjectsAsync(
        string root, string token, List<DiscoveryIssue> issues, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var skip = 0;
        string? continuation = null;
        var seen = new HashSet<string>();
        for (var page = 0; page < MaxPages; page++)
        {
            var path = $"{root}/_apis/projects?$top=100&$skip={skip}&api-version=7.1";
            if (!string.IsNullOrEmpty(continuation))
                path += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            using var response = await GetAsync(path, token, ct);
            var chunk = Items(response.Json).Select(x => x.Clone()).ToArray();
            items.AddRange(chunk);
            if (items.Count >= MaxProjects) { issues.Add(new("ProjectsTruncated")); break; }
            if (!string.IsNullOrEmpty(response.Continuation))
            {
                if (!seen.Add(response.Continuation))
                {
                    issues.Add(new("ProjectsPaginationLoop")); break;
                }
                continuation = response.Continuation;
            }
            else
            {
                continuation = null;
                skip += chunk.Length;
                if (chunk.Length < 100) return items;
            }
            if (page == MaxPages - 1) issues.Add(new("ProjectsTruncated"));
        }
        return items;
    }

    private async Task<IReadOnlyList<RepositoryRef>> ReadRefsAsync(
        string projectUri, string repoId, string token, List<DiscoveryIssue> issues, CancellationToken ct)
    {
        var refs = new List<RepositoryRef>();
        var seen = new HashSet<string>();
        string? continuation = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{projectUri}/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/refs?$top=1000&api-version=7.1";
            if (!string.IsNullOrEmpty(continuation))
                url += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            using var response = await GetAsync(url, token, ct);
            foreach (var entry in Items(response.Json))
            {
                var name = Text(entry, "name");
                var sha = Text(entry, "objectId");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(sha))
                    refs.Add(new(repoId, name, sha));
            }
            if (refs.Count >= MaxRefsPerRepository) { issues.Add(new("RefsTruncated", RepositoryId: repoId)); break; }
            if (string.IsNullOrEmpty(response.Continuation)) return refs;
            if (!seen.Add(response.Continuation)) { issues.Add(new("RefsPaginationLoop", RepositoryId: repoId)); break; }
            continuation = response.Continuation;
            if (page == MaxPages - 1) issues.Add(new("RefsTruncated", RepositoryId: repoId));
        }
        return refs;
    }

    private async Task<IReadOnlyList<SourceIdentity>> ReadUsersAsync(
        string graphRoot, string token, List<DiscoveryIssue> issues, CancellationToken ct)
    {
        var users = new Dictionary<string, SourceIdentity>(StringComparer.Ordinal);
        string? continuation = null;
        var seen = new HashSet<string>();
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{graphRoot}/_apis/graph/users?api-version=7.1-preview.1";
            if (!string.IsNullOrEmpty(continuation))
                url += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            using var response = await GetAsync(url, token, ct);
            foreach (var entry in Items(response.Json))
            {
                var id = Text(entry, "descriptor");
                if (string.IsNullOrWhiteSpace(id)) continue;
                users[id] = new SourceIdentity(id, Text(entry, "displayName") ?? id,
                    Text(entry, "mailAddress"), Text(entry, "legacyDescriptor"));
            }
            if (users.Count >= MaxUsers) { issues.Add(new("GraphUsersTruncated")); break; }
            if (string.IsNullOrEmpty(response.Continuation)) return users.Values.ToArray();
            if (!seen.Add(response.Continuation)) { issues.Add(new("GraphUsersTruncated")); break; }
            continuation = response.Continuation;
            if (page == MaxPages - 1) issues.Add(new("GraphUsersTruncated"));
        }
        return users.Values.ToArray();
    }

    private static void CollectTfvcPaths(JsonElement branch, HashSet<string> paths, int depth)
    {
        if (depth > 30 || paths.Count >= 2000) return;
        var path = Text(branch, "path");
        if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("$/", StringComparison.Ordinal))
            paths.Add(path);
        if (branch.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                if (child.ValueKind == JsonValueKind.Object) CollectTfvcPaths(child, paths, depth + 1);
    }

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<JsonElement> Items(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new DiscoveryProviderException("UnexpectedResponse");
        return value.EnumerateArray();
    }

    private async Task<ResponsePage> GetAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new DiscoveryProviderException("Unauthorized");
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new DiscoveryProviderException("InsufficientScope");
            if ((int)response.StatusCode is >= 300 and < 400) throw new DiscoveryProviderException("UnexpectedRedirect");
            if (!response.IsSuccessStatusCode) throw new DiscoveryProviderException("ProviderUnavailable");
            if (response.Content.Headers.ContentLength is > 10_000_000)
                throw new DiscoveryProviderException("ResponseTooLarge");
            var continuation = response.Headers.TryGetValues("X-MS-ContinuationToken", out var values)
                ? values.FirstOrDefault() : null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return new ResponsePage(document, continuation);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DiscoveryProviderException("ProviderTimeout");
        }
        catch (HttpRequestException)
        {
            throw new DiscoveryProviderException("ProviderUnavailable");
        }
        catch (JsonException)
        {
            throw new DiscoveryProviderException("UnexpectedResponse");
        }
    }

    private sealed record ResponsePage(JsonDocument Json, string? Continuation) : IDisposable
    {
        public void Dispose() => Json.Dispose();
    }
}
