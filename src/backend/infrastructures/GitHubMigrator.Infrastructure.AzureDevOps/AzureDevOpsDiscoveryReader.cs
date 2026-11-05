using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

// F-003 read-only inventory. Every request is built from a fixed Microsoft hostname.
// No custom URL, redirects, cookies, or user-provided request path is accepted.
public sealed class AzureDevOpsDiscoveryReader(
    HttpClient client, TimeProvider clock, IDiscoveryPageCheckpointStore? checkpoints = null)
    : IResumableDiscoveryReader
{
    public string ProviderKey => "azure-devops-services";
    private const int MaxPages = 30;
    private const int MaxProjects = 2000;
    private const int MaxRefsPerRepository = 30000;
    private const int MaxUsers = 15000;

    public Task<DiscoveryInventory> DiscoverAsync(
        Guid connectionId, string organization, string accessToken, CancellationToken ct) =>
        DiscoverCoreAsync(connectionId, organization, accessToken, null, ct);

    public Task<DiscoveryInventory> DiscoverWithCheckpointsAsync(
        Guid connectionId, string organization, string accessToken,
        DiscoveryLeaseContext lease, CancellationToken ct)
    {
        if (lease.JobId == Guid.Empty || lease.LeaseToken == Guid.Empty ||
            lease.Attempt < 1 || checkpoints is not IDiscoveryIdentityObservationStore)
            throw new DiscoveryCheckpointException("InvalidDiscoveryLeaseContext");
        return DiscoverCoreAsync(connectionId, organization, accessToken, lease, ct);
    }

    private async Task<DiscoveryInventory> DiscoverCoreAsync(
        Guid connectionId, string organization, string accessToken,
        DiscoveryLeaseContext? lease, CancellationToken ct)
    {
        if (connectionId == Guid.Empty || string.IsNullOrWhiteSpace(organization) ||
            !System.Text.RegularExpressions.Regex.IsMatch(organization, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$"))
            throw new ArgumentException("Invalid source connection.");
        var issues = new List<DiscoveryIssue>();
        var projects = new List<SourceProject>();
        // Only synchronous/legacy requests materialize refs in .NET memory.
        // Leased discovery jobs persist every ref page into PostgreSQL and
        // return only an exact count + externalized inventory marker.
        var references = new List<RepositoryRef>();
        var refCount = 0;
        var identities = new List<SourceIdentity>();
        var identityCount = 0;
        var root = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}";
        var graphRoot = $"https://vssps.dev.azure.com/{Uri.EscapeDataString(organization)}";

        var projectItems = await ReadProjectsAsync(root, accessToken, issues, lease, ct);
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
                using var details = await GetCheckpointedProjectCapabilitiesAsync(
                    $"{root}/_apis/projects/{Uri.EscapeDataString(projectId)}?includeCapabilities=true&api-version=7.1",
                    accessToken, lease, projectId, ct);
                if (details.Json.RootElement.TryGetProperty("capabilities", out var capabilities) &&
                    capabilities.TryGetProperty("versioncontrol", out var versionControl))
                    sourceControlType = Text(versionControl, "sourceControlType");
            }
            catch (DiscoveryProviderException ex)
                when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
            {
                issues.Add(new("ProjectCapabilitiesUnavailable", projectId));
            }

            try
            {
                using var repos = await GetPageAsync(
                    $"{projectUri}/_apis/git/repositories?api-version=7.1",
                    accessToken, lease, "repos:" + projectId, 0, ["id", "name"], ct);
                foreach (var entry in Items(repos.Json))
                {
                    var repoId = Text(entry, "id");
                    var repoName = Text(entry, "name");
                    if (string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(repoName)) continue;
                    sourceRepositories.Add(new(repoId, projectId, repoName, VersionControlKind.Git));
                    try
                    {
                        refCount = checked(refCount + await ReadRefsAsync(
                            projectUri, repoId, accessToken, issues, lease,
                            lease is null ? references : null, ct));
                    }
                    catch (DiscoveryProviderException ex)
                        when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
                    {
                        issues.Add(new(ex.Code, projectId, repoId));
                    }
                }
            }
            catch (DiscoveryProviderException ex)
                when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
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
            identities.AddRange(await ReadUsersAsync(graphRoot, accessToken, issues, lease, ct));
            identityCount = lease is { } active
                ? (await ((IDiscoveryIdentityObservationStore)checkpoints!)
                    .GetProgressAsync(active.JobId, ct)).ObservedUsers
                : identities.Count;
            if (issues.Any(x => x.Code is "GraphUsersTruncated")) identitiesComplete = false;
        }
        catch (DiscoveryProviderException ex)
            when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
        {
            identitiesComplete = false;
            issues.Add(new(ex.Code));
        }

        // Capture raw group/ACE evidence; it must not be confused with effective permissions.
        var security = await new AzureDevOpsSecurityReader(client, checkpoints, clock).ReadAsync(
            organization, accessToken, projects, ct, lease);
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
            Identities = lease is null ? identities : [],
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
            Refs = lease is null ? references : [],
            RefsExternalized = lease is not null,
            RefCount = refCount,
            IdentitiesExternalized = lease is not null,
            IdentityCount = identityCount,
            Issues = issues,
            Completeness = DiscoveryCompleteness.Partial,
            IdentitiesComplete = identitiesComplete,
            PermissionsComplete = false,
            Groups = security.Groups,
            GroupsExternalized = security.GroupsExternalized,
            GroupCount = security.GroupCount,
            Memberships = security.Memberships,
            MembershipsExternalized = security.MembershipsExternalized,
            MembershipCount = security.MembershipCount,
            AclEvidence = security.Acls,
            AclsExternalized = security.AclsExternalized,
            AclCount = security.AclCount,
            SecurityCoverage = security.Coverage
        };
    }

    private async Task<IReadOnlyList<JsonElement>> ReadProjectsAsync(
        string root, string token, List<DiscoveryIssue> issues,
        DiscoveryLeaseContext? lease, CancellationToken ct)
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
            using var response = await GetPageAsync(path, token, lease,
                "projects", page, ["id", "name"], ct);
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

    private async Task<int> ReadRefsAsync(
        string projectUri, string repoId, string token, List<DiscoveryIssue> issues,
        DiscoveryLeaseContext? lease, List<RepositoryRef>? collect, CancellationToken ct)
    {
        var observed = 0;
        var seen = new HashSet<string>();
        string? continuation = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{projectUri}/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/refs?$top=1000&api-version=7.1";
            if (!string.IsNullOrEmpty(continuation))
                url += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            using var response = await GetPageAsync(url, token, lease,
                "refs:" + repoId, page, ["name", "objectId"], ct);
            // No per-repository or cross-repository ref list in the leased
            // worker. Each response page is bounded (1,000 entries) and has
            // already been atomically normalized into PostgreSQL.
            foreach (var entry in Items(response.Json))
            {
                var name = Text(entry, "name");
                var sha = Text(entry, "objectId");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(sha)) continue;
                observed++;
                collect?.Add(new(repoId, name, sha));
            }
            if (observed >= MaxRefsPerRepository)
            {
                issues.Add(new("RefsTruncated", RepositoryId: repoId));
                break;
            }
            if (string.IsNullOrEmpty(response.Continuation)) return observed;
            if (!seen.Add(response.Continuation))
            {
                issues.Add(new("RefsPaginationLoop", RepositoryId: repoId));
                break;
            }
            continuation = response.Continuation;
            if (page == MaxPages - 1)
                issues.Add(new("RefsTruncated", RepositoryId: repoId));
        }
        return observed;
    }

    private async Task<IReadOnlyList<SourceIdentity>> ReadUsersAsync(
        string graphRoot, string token, List<DiscoveryIssue> issues,
        DiscoveryLeaseContext? lease, CancellationToken ct)
    {
        // Leased jobs do not collect Graph identity records in memory.
        // Each page has already committed its sanitized observations and
        // checkpoint under the same PostgreSQL transaction.
        Dictionary<string, SourceIdentity>? users =
            lease is null ? new(StringComparer.Ordinal) : null;
        var observed = 0;
        string? continuation = null;
        var seen = new HashSet<string>();
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{graphRoot}/_apis/graph/users?api-version=7.1-preview.1";
            if (!string.IsNullOrEmpty(continuation))
                url += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            using var response = await GetPageAsync(url, token, lease,
                "graph-users", page,
                ["descriptor", "displayName", "mailAddress", "legacyDescriptor"], ct);
            foreach (var entry in Items(response.Json))
            {
                var id = Text(entry, "descriptor");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (users is not null)
                    users[id] = new SourceIdentity(id, Text(entry, "displayName") ?? id,
                        Text(entry, "mailAddress"), Text(entry, "legacyDescriptor"));
            }
            observed = lease is { } active
                ? (await ((IDiscoveryIdentityObservationStore)checkpoints!)
                    .GetProgressAsync(active.JobId, ct)).ObservedUsers
                : users!.Count;
            if (observed >= MaxUsers) { issues.Add(new("GraphUsersTruncated")); break; }
            if (string.IsNullOrEmpty(response.Continuation)) return users?.Values.ToArray() ?? [];
            if (!seen.Add(response.Continuation)) { issues.Add(new("GraphUsersTruncated")); break; }
            continuation = response.Continuation;
            if (page == MaxPages - 1) issues.Add(new("GraphUsersTruncated"));
        }
        return users?.Values.ToArray() ?? [];
    }

    private async Task<ResponsePage> GetCheckpointedProjectCapabilitiesAsync(
        string url, string token, DiscoveryLeaseContext? lease,
        string projectId, CancellationToken ct)
    {
        if (lease is null) return await GetAsync(url, token, ct);
        if (checkpoints is null)
            throw new DiscoveryCheckpointException("CheckpointStorageUnavailable");

        var key = "capabilities:" + projectId;
        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var saved = await checkpoints.GetAsync(lease.Value.JobId, key, 0, ct);
        if (saved is not null)
        {
            if (!string.Equals(saved.RequestHash, requestHash, StringComparison.Ordinal))
                throw new DiscoveryCheckpointException("CheckpointRequestMismatch");
            try { return new ResponsePage(JsonDocument.Parse(saved.SafePayloadJson), null); }
            catch (JsonException) { throw new DiscoveryCheckpointException("CheckpointPayloadInvalid"); }
        }

        using var response = await GetAsync(url, token, ct);
        string? sourceControlType = null;
        if (response.Json.RootElement.TryGetProperty("capabilities", out var capabilities) &&
            capabilities.TryGetProperty("versioncontrol", out var versionControl))
            sourceControlType = Text(versionControl, "sourceControlType");

        var safeJson = JsonSerializer.Serialize(new {
            capabilities = new {
                versioncontrol = new { sourceControlType }
            }
        });
        var stored = await checkpoints.SaveAsync(lease.Value,
            new DiscoveryPageCheckpoint(lease.Value.JobId, key, 0,
                requestHash, safeJson, null, clock.GetUtcNow()), ct);
        try { return new ResponsePage(JsonDocument.Parse(stored.SafePayloadJson), null); }
        catch (JsonException) { throw new DiscoveryCheckpointException("CheckpointPayloadInvalid"); }
    }

    private async Task<ResponsePage> GetPageAsync(
        string url, string token, DiscoveryLeaseContext? lease,
        string resource, int page, string[] allowlist, CancellationToken ct)
    {
        // Synchronous discovery keeps the existing provider behavior. Only a
        // leased durable job can read/write immutable page checkpoints.
        if (lease is null) return await GetAsync(url, token, ct);
        if (checkpoints is null)
            throw new DiscoveryCheckpointException("CheckpointStorageUnavailable");

        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var saved = await checkpoints.GetAsync(lease.Value.JobId, resource, page, ct);
        if (saved is not null)
        {
            if (!string.Equals(saved.RequestHash, requestHash, StringComparison.Ordinal))
                throw new DiscoveryCheckpointException("CheckpointRequestMismatch");
            // Backfill provisional observations when resuming a checkpoint
            // written before the streaming-ref feature was deployed. Replaying
            // the same page is idempotent and remains fenced by the new lease.
            if (resource.StartsWith("refs:", StringComparison.Ordinal) ||
                string.Equals(resource, "graph-users", StringComparison.Ordinal))
                await checkpoints.SaveAsync(lease.Value, saved, ct);
            try { return new ResponsePage(JsonDocument.Parse(saved.SafePayloadJson), saved.NextContinuation); }
            catch (JsonException) { throw new DiscoveryCheckpointException("CheckpointPayloadInvalid"); }
        }

        using var fetched = await GetAsync(url, token, ct);
        // Never write arbitrary source JSON (which may contain credentials or
        // extended fields) into page checkpoints. Keep only the metadata that
        // the existing discovery inventory already retains.
        var items = Items(fetched.Json).Select(entry =>
            allowlist.ToDictionary(field => field, field => Text(entry, field),
                StringComparer.Ordinal)).ToArray();
        var safeJson = JsonSerializer.Serialize(new { value = items });
        if (Encoding.UTF8.GetByteCount(safeJson) > 1_048_576)
            throw new DiscoveryCheckpointException("CheckpointPageTooLarge");
        var persisted = await checkpoints.SaveAsync(lease.Value,
            new DiscoveryPageCheckpoint(lease.Value.JobId, resource, page,
                requestHash, safeJson, fetched.Continuation, clock.GetUtcNow()), ct);
        try { return new ResponsePage(JsonDocument.Parse(persisted.SafePayloadJson), persisted.NextContinuation); }
        catch (JsonException) { throw new DiscoveryCheckpointException("CheckpointPayloadInvalid"); }
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
