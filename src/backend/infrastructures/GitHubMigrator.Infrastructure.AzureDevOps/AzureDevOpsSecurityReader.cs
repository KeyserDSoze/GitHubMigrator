using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

public sealed record SecurityEvidenceResult(
    IReadOnlyList<DiscoveryGroup> Groups,
    IReadOnlyList<DiscoveryMembership> Memberships,
    IReadOnlyList<RepositoryAclEvidence> Acls,
    IReadOnlyList<DiscoveryIssue> Issues,
    DiscoveryCoverage Coverage, int MembershipCount = 0, bool MembershipsExternalized = false,
    int AclCount = 0, bool AclsExternalized = false,
    int GroupCount = 0, bool GroupsExternalized = false);

// Fixed-host read-only security evidence, not a target access policy or an effective rights calculator.
// Git namespace: 2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87; GenericRead=2, Contribute=4.
public sealed class AzureDevOpsSecurityReader(
    HttpClient client, IDiscoveryPageCheckpointStore? checkpoints = null,
    TimeProvider? clock = null)
{
    private const string GitNamespace = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87";
    private const int MaxGroupPages = 20;
    private const int MaxMembershipPages = 20;
    private const int MaxGroups = 1000;
    private const int MaxMembershipEdges = 20000;
    private const int MaxRepositories = 1500;
    private const int MaxAclEntries = 30000;

    public Task<SecurityEvidenceResult> ReadAsync(
        string organization, string token, IReadOnlyList<SourceProject> projects,
        CancellationToken ct, DiscoveryLeaseContext? lease = null) =>
        ReadAsync(AzureDevOpsEndpoints.Services(organization), token, projects, ct, lease);

    public async Task<SecurityEvidenceResult> ReadAsync(
        AzureDevOpsEndpoints endpoints, string token, IReadOnlyList<SourceProject> projects,
        CancellationToken ct, DiscoveryLeaseContext? lease = null)
    {
        var groups = new List<DiscoveryGroup>();
        var groupStore = checkpoints as IDiscoveryGroupObservationStore;
        var externalizeGroups = lease is not null && groupStore is not null;
        var edges = new List<DiscoveryMembership>();
        // Leased production scans use PostgreSQL membership observations.
        // Legacy/checkpoint-only readers retain their inline representation.
        var membershipStore = checkpoints as IDiscoveryMembershipEvidenceVerifier;
        var externalize = lease is not null && membershipStore is not null;
        var observedMemberships = 0;
        var acls = new List<RepositoryAclEvidence>();
        var aclStore = checkpoints as IDiscoveryAclEvidenceStore;
        var externalizeAcls = lease is not null && aclStore is not null;
        var observedAces = 0;
        var issues = new List<DiscoveryIssue>();
        bool groupsComplete = true, membershipsComplete = true, aclsCollected = true;
        var root = endpoints.Root;
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? continuation = null;
            for (var page = 0; page < MaxGroupPages; page++)
            {
                var url = endpoints.GroupsUrl(continuation);
                using var response = await ReadCheckpointedAsync(
                    url, token, lease, "security-groups", page, "groups", ct, endpoints.NormalizeGroups);
                foreach (var group in Values(response.Document))
                {
                    var descriptor = Text(group, "descriptor");
                    if (!string.IsNullOrWhiteSpace(descriptor))
                        groups.Add(new(descriptor, Text(group, "displayName") ?? descriptor,
                            Text(group, "legacyDescriptor")));
                }
                if (groups.Count >= MaxGroups)
                {
                    groupsComplete = false;
                    issues.Add(new("GroupsTruncated"));
                    break;
                }
                if (string.IsNullOrEmpty(response.Continuation)) break;
                if (!seen.Add(response.Continuation) || page == MaxGroupPages - 1)
                {
                    groupsComplete = false;
                    issues.Add(new("GroupsPaginationIncomplete"));
                    break;
                }
                continuation = response.Continuation;
            }
        }
        catch (DiscoveryProviderException ex)
            when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
        {
            groupsComplete = false;
            issues.Add(new("GroupsUnavailable:" + ex.Code));
        }

        if (!groupsComplete)
        {
            membershipsComplete = false;
            issues.Add(new("MembershipGraphIncomplete"));
        }

        var distinctGroups = groups.DistinctBy(g => g.Descriptor).ToArray();
        if (distinctGroups.Length > MaxGroups) membershipsComplete = false;
        // The group names/descriptors are required for membership traversal
        // but must not be serialized into a durable source snapshot.
        var observedGroupCount = externalizeGroups
            ? (await groupStore!.GetProgressAsync(lease!.Value.JobId, ct)).ObservedGroups
            : distinctGroups.Length;
        if (observedGroupCount > MaxGroups)
            throw new DiscoveryCheckpointException("GroupObservationLimit");
        foreach (var group in distinctGroups.Take(MaxGroups))
        {
            ct.ThrowIfCancellationRequested();
            if (observedMemberships >= MaxMembershipEdges)
            {
                membershipsComplete = false;
                issues.Add(new("MembershipsTruncated"));
                break;
            }
            try
            {
                var seenCursors = new HashSet<string>(StringComparer.Ordinal);
                string? continuation = null;
                for (var page = 0; page < MaxMembershipPages; page++)
                {
                    var url = endpoints.MembershipsUrl(group.Descriptor, continuation);
                    using var response = await ReadCheckpointedAsync(
                        url, token, lease, "security-members:" + Hash(group.Descriptor),
                        page, "memberships", ct,
                        document => endpoints.NormalizeMemberships(document, group.Descriptor));
                    foreach (var membership in Values(response.Document))
                    {
                        var container = Text(membership, "containerDescriptor");
                        var member = Text(membership, "memberDescriptor");
                        // Reject unrelated edges and never infer transitive membership.
                        if (!externalize &&
                            string.Equals(container, group.Descriptor, StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(member))
                            edges.Add(new(group.Descriptor, member));
                    }
                    observedMemberships = externalize
                        ? (await membershipStore!.GetProgressAsync(lease!.Value.JobId, ct)).ObservedMemberships
                        : edges.Distinct().Count();
                    if (observedMemberships >= MaxMembershipEdges)
                    {
                        membershipsComplete = false;
                        issues.Add(new("MembershipsTruncated"));
                        break;
                    }
                    if (string.IsNullOrEmpty(response.Continuation)) break;
                    if (!seenCursors.Add(response.Continuation) || page == MaxMembershipPages - 1)
                    {
                        membershipsComplete = false;
                        issues.Add(new("MembershipsPaginationIncomplete"));
                        break;
                    }
                    continuation = response.Continuation;
                }
            }
            catch (DiscoveryProviderException ex)
                when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
            {
                membershipsComplete = false;
                issues.Add(new("MembershipsUnavailable:" + ex.Code));
            }
        }
        if (!externalize && edges.Count > MaxMembershipEdges)
        {
            edges = edges.Take(MaxMembershipEdges).ToList();
            membershipsComplete = false;
            issues.Add(new("MembershipsTruncated"));
        }
        observedMemberships = externalize
            ? (await membershipStore!.GetProgressAsync(lease!.Value.JobId, ct)).ObservedMemberships
            : edges.Distinct().Count();

        // Collect the project-level ACL and each Git repository ACL separately. The service may
        // return 0 ACEs at a token even where permissions inherit from another scope/group.
        var gitRepoCount = projects.Sum(p => p.Repositories.Count(r => r.VersionControl == VersionControlKind.Git));
        if (gitRepoCount > MaxRepositories)
        {
            issues.Add(new("AclRepositoriesTruncated"));
            aclsCollected = false;
        }

        var processedRepos = 0;
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            var repositories = project.Repositories.Where(r => r.VersionControl == VersionControlKind.Git).ToArray();
            if (repositories.Length == 0) continue;
            var projectToken = $"repoV2/{project.ProviderId}";
            IReadOnlyList<AclRow> parentRows = [];
            try
            {
                parentRows = await ReadAclAsync(root, endpoints.Api, token, projectToken, lease, ct);
            }
            catch (DiscoveryProviderException ex)
                when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
            {
                aclsCollected = false;
                issues.Add(new("ProjectAclUnavailable:" + ex.Code, project.ProviderId));
            }
            foreach (var repository in repositories)
            {
                if (processedRepos++ >= MaxRepositories) break;
                foreach (var row in parentRows)
                    acls.Add(new(repository.ProviderId, row.Token, row.Descriptor, row.Allow, row.Deny,
                        row.InheritPermissions, "project"));
                try
                {
                    var repoToken = $"{projectToken}/{repository.ProviderId}";
                    foreach (var row in await ReadAclAsync(root, endpoints.Api, token, repoToken, lease, ct))
                        acls.Add(new(repository.ProviderId, row.Token, row.Descriptor, row.Allow, row.Deny,
                            row.InheritPermissions, "repository"));
                }
                catch (DiscoveryProviderException ex)
                    when (lease is null || ex.Code is not ("ProviderUnavailable" or "ProviderTimeout"))
                {
                    aclsCollected = false;
                    issues.Add(new("RepositoryAclUnavailable:" + ex.Code, project.ProviderId, repository.ProviderId));
                }
                observedAces = externalizeAcls
                    ? (await aclStore!.GetProgressAsync(lease!.Value.JobId, ct)).ObservedAces
                    : acls.Count;
                if (observedAces >= MaxAclEntries)
                {
                    aclsCollected = false;
                    issues.Add(new("AclEntriesTruncated", project.ProviderId, repository.ProviderId));
                    break;
                }
            }
            if (observedAces >= MaxAclEntries) break;
        }

        // Never assert effective authorization from raw ACE masks: nested identity groups,
        // deny precedence, branch scopes and external directory membership can change results.
        observedAces = externalizeAcls
            ? (await aclStore!.GetProgressAsync(lease!.Value.JobId, ct)).ObservedAces
            : acls.Count;
        return new SecurityEvidenceResult(externalizeGroups ? [] : distinctGroups,
            externalize ? [] : edges.Distinct().ToArray(),
            externalizeAcls ? [] : acls.Take(MaxAclEntries).ToArray(), issues,
            new(groupsComplete, membershipsComplete, aclsCollected, false),
            observedMemberships, externalize, observedAces, externalizeAcls,
            observedGroupCount, externalizeGroups);
    }

    private async Task<IReadOnlyList<AclRow>> ReadAclAsync(string root, string api, string token, string securityToken,
        DiscoveryLeaseContext? lease, CancellationToken ct)
    {
        var url = $"{root}/_apis/accesscontrollists/{GitNamespace}?token={Uri.EscapeDataString(securityToken)}&api-version={api}";
        using var response = await ReadCheckpointedAsync(
            url, token, lease, "security-acl:" + Hash(securityToken), 0, "acls", ct);
        // Sanitization and the canonical ACE/schema check already occurred
        // atomically in the leased PostgreSQL checkpoint SaveAsync. Do not
        // build a large per-token ACE list in the discovery worker.
        if (lease is not null && checkpoints is IDiscoveryAclEvidenceStore)
            return [];
        var items = new List<AclRow>();
        foreach (var acl in Values(response.Document))
        {
            var returnedToken = Text(acl, "token");
            if (!string.Equals(returnedToken, securityToken, StringComparison.OrdinalIgnoreCase))
                throw new DiscoveryProviderException("UnexpectedAclToken");
            var inherit = acl.TryGetProperty("inheritPermissions", out var inheritValue) &&
                          inheritValue.ValueKind == JsonValueKind.True;
            if (!acl.TryGetProperty("acesDictionary", out var dictionary) ||
                dictionary.ValueKind != JsonValueKind.Object)
                throw new DiscoveryProviderException("UnexpectedResponse");
            foreach (var entry in dictionary.EnumerateObject())
            {
                var ace = entry.Value;
                if (ace.ValueKind != JsonValueKind.Object) throw new DiscoveryProviderException("UnexpectedResponse");
                var descriptor = Text(ace, "descriptor") ?? entry.Name;
                if (!string.Equals(descriptor, entry.Name, StringComparison.Ordinal))
                    throw new DiscoveryProviderException("UnexpectedResponse");
                if (!ace.TryGetProperty("allow", out var allow) || !allow.TryGetInt64(out var allowMask) ||
                    !ace.TryGetProperty("deny", out var deny) || !deny.TryGetInt64(out var denyMask))
                    throw new DiscoveryProviderException("UnexpectedResponse");
                items.Add(new(securityToken, descriptor, allowMask, denyMask, inherit));
                if (items.Count > MaxAclEntries) throw new DiscoveryProviderException("AclEntriesTruncated");
            }
        }
        return items;
    }

    private static bool CanBackfillSecurityResource(
        string resource, IDiscoveryPageCheckpointStore? store) =>
        (resource.StartsWith("security-members:", StringComparison.Ordinal) &&
            store is IDiscoveryMembershipEvidenceVerifier) ||
        (resource.StartsWith("security-acl:", StringComparison.Ordinal) &&
            store is IDiscoveryAclEvidenceStore);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..40];

    private async Task<JsonPage> ReadCheckpointedAsync(
        string url, string token, DiscoveryLeaseContext? lease, string resource,
        int page, string kind, CancellationToken ct,
        Func<JsonDocument, JsonDocument>? normalize = null)
    {
        if (lease is null)
        {
            var direct = await ReadAsync(url, token, ct);
            return normalize is null ? direct : new JsonPage(normalize(direct.Document), direct.Continuation);
        }
        if (checkpoints is null)
            throw new DiscoveryCheckpointException("CheckpointStorageUnavailable");
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var existing = await checkpoints.GetAsync(lease.Value.JobId, resource, page, ct);
        if (existing is not null)
        {
            if (!string.Equals(requestHash, existing.RequestHash, StringComparison.Ordinal))
                throw new DiscoveryCheckpointException("CheckpointRequestMismatch");
            if (CanBackfillSecurityResource(resource, checkpoints))
                await checkpoints.SaveAsync(lease.Value, existing, ct);
            try { return new JsonPage(JsonDocument.Parse(existing.SafePayloadJson), existing.NextContinuation); }
            catch (JsonException) { throw new DiscoveryCheckpointException("CheckpointPayloadInvalid"); }
        }

        using var fetched = await ReadAsync(url, token, ct);
        using var response = new JsonPage(normalize is null ? fetched.Document : normalize(fetched.Document),
            fetched.Continuation);
        // Strict projections only. The full Graph/ACL responses may contain
        // unexpected fields; those must never enter PostgreSQL checkpoints.
        var safe = kind switch
        {
            "groups" => JsonSerializer.Serialize(new { value = Values(response.Document)
                .Select(group => new { descriptor = Text(group, "descriptor"),
                    displayName = Text(group, "displayName"),
                    legacyDescriptor = Text(group, "legacyDescriptor") }).ToArray() }),
            "memberships" => JsonSerializer.Serialize(new { value = Values(response.Document)
                .Select(edge => new { containerDescriptor = Text(edge, "containerDescriptor"),
                    memberDescriptor = Text(edge, "memberDescriptor") }).ToArray() }),
            "acls" => SafeAcls(response.Document),
            _ => throw new DiscoveryCheckpointException("UnknownCheckpointKind")
        };
        if (Encoding.UTF8.GetByteCount(safe) > 1_048_576)
            throw new DiscoveryCheckpointException("CheckpointPageTooLarge");
        var checkpoint = await checkpoints.SaveAsync(lease.Value,
            new DiscoveryPageCheckpoint(lease.Value.JobId, resource, page,
                requestHash, safe, response.Continuation,
                (clock ?? TimeProvider.System).GetUtcNow()), ct);
        try { return new JsonPage(JsonDocument.Parse(checkpoint.SafePayloadJson),
            checkpoint.NextContinuation); }
        catch (JsonException) { throw new DiscoveryCheckpointException("CheckpointPayloadInvalid"); }
    }

    private static string SafeAcls(JsonDocument doc)
    {
        var rows = new List<object>();
        foreach (var acl in Values(doc))
        {
            var token = Text(acl, "token");
            if (string.IsNullOrWhiteSpace(token) ||
                !acl.TryGetProperty("acesDictionary", out var entries) ||
                entries.ValueKind != JsonValueKind.Object)
                throw new DiscoveryProviderException("UnexpectedResponse");
            var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateObject())
            {
                var descriptor = Text(entry.Value, "descriptor") ?? entry.Name;
                if (!string.Equals(descriptor, entry.Name, StringComparison.Ordinal) ||
                    !entry.Value.TryGetProperty("allow", out var allow) ||
                    !allow.TryGetInt64(out var allowMask) ||
                    !entry.Value.TryGetProperty("deny", out var deny) ||
                    !deny.TryGetInt64(out var denyMask))
                    throw new DiscoveryProviderException("UnexpectedResponse");
                dictionary.Add(entry.Name, new { descriptor, allow = allowMask, deny = denyMask });
            }
            rows.Add(new {
                token,
                inheritPermissions = acl.TryGetProperty("inheritPermissions", out var inherit) &&
                    inherit.ValueKind == JsonValueKind.True,
                acesDictionary = dictionary
            });
        }
        return JsonSerializer.Serialize(new { value = rows });
    }

    private async Task<JsonPage> ReadAsync(string url, string token, CancellationToken ct)
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
            return new(document, continuation);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DiscoveryProviderException("ProviderTimeout"); }
        catch (HttpRequestException) { throw new DiscoveryProviderException("ProviderUnavailable"); }
        catch (JsonException) { throw new DiscoveryProviderException("UnexpectedResponse"); }
    }

    private static IEnumerable<JsonElement> Values(JsonDocument json)
    {
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new DiscoveryProviderException("UnexpectedResponse");
        return items.EnumerateArray();
    }

    private static string? Text(JsonElement entry, string key) =>
        entry.ValueKind == JsonValueKind.Object &&
        entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private sealed record AclRow(string Token, string Descriptor, long Allow, long Deny, bool InheritPermissions);
    private sealed record JsonPage(JsonDocument Document, string? Continuation) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }
}
