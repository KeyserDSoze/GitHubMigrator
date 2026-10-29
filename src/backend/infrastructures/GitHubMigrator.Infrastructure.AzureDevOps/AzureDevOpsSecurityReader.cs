using System.Net;
using System.Net.Http.Headers;
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
    DiscoveryCoverage Coverage);

// Fixed-host read-only security evidence, not a target access policy or an effective rights calculator.
// Git namespace: 2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87; GenericRead=2, Contribute=4.
public sealed class AzureDevOpsSecurityReader(HttpClient client)
{
    private const string GitNamespace = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87";
    private const int MaxGroupPages = 20;
    private const int MaxGroups = 1000;
    private const int MaxMembershipEdges = 20000;
    private const int MaxRepositories = 1500;
    private const int MaxAclEntries = 30000;

    public async Task<SecurityEvidenceResult> ReadAsync(
        string organization, string token, IReadOnlyList<SourceProject> projects, CancellationToken ct)
    {
        var groups = new List<DiscoveryGroup>();
        var edges = new List<DiscoveryMembership>();
        var acls = new List<RepositoryAclEvidence>();
        var issues = new List<DiscoveryIssue>();
        bool groupsComplete = true, membershipsComplete = true, aclsCollected = true;
        var graphRoot = $"https://vssps.dev.azure.com/{Uri.EscapeDataString(organization)}";
        var root = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}";
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? continuation = null;
            for (var page = 0; page < MaxGroupPages; page++)
            {
                var url = $"{graphRoot}/_apis/graph/groups?api-version=7.1-preview.1";
                if (!string.IsNullOrEmpty(continuation))
                    url += $"&continuationToken={Uri.EscapeDataString(continuation)}";
                using var response = await ReadAsync(url, token, ct);
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
        foreach (var group in distinctGroups.Take(MaxGroups))
        {
            ct.ThrowIfCancellationRequested();
            if (edges.Count >= MaxMembershipEdges)
            {
                membershipsComplete = false;
                issues.Add(new("MembershipsTruncated"));
                break;
            }
            try
            {
                var url = $"{graphRoot}/_apis/graph/Memberships/{Uri.EscapeDataString(group.Descriptor)}" +
                    "?direction=down&depth=1&api-version=7.1-preview.1";
                using var response = await ReadAsync(url, token, ct);
                foreach (var membership in Values(response.Document))
                {
                    var container = Text(membership, "containerDescriptor");
                    var member = Text(membership, "memberDescriptor");
                    // Reject malformed / unrelated edges, but do not infer transitive membership.
                    if (string.Equals(container, group.Descriptor, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(member))
                        edges.Add(new(container, member));
                }
            }
            catch (DiscoveryProviderException ex)
            {
                membershipsComplete = false;
                issues.Add(new("MembershipsUnavailable:" + ex.Code));
            }
        }
        if (edges.Count > MaxMembershipEdges)
        {
            edges = edges.Take(MaxMembershipEdges).ToList();
            membershipsComplete = false;
            issues.Add(new("MembershipsTruncated"));
        }

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
                parentRows = await ReadAclAsync(root, token, projectToken, ct);
            }
            catch (DiscoveryProviderException ex)
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
                    foreach (var row in await ReadAclAsync(root, token, repoToken, ct))
                        acls.Add(new(repository.ProviderId, row.Token, row.Descriptor, row.Allow, row.Deny,
                            row.InheritPermissions, "repository"));
                }
                catch (DiscoveryProviderException ex)
                {
                    aclsCollected = false;
                    issues.Add(new("RepositoryAclUnavailable:" + ex.Code, project.ProviderId, repository.ProviderId));
                }
                if (acls.Count >= MaxAclEntries)
                {
                    aclsCollected = false;
                    issues.Add(new("AclEntriesTruncated", project.ProviderId, repository.ProviderId));
                    break;
                }
            }
            if (acls.Count >= MaxAclEntries) break;
        }

        // Never assert effective authorization from raw ACE masks: nested identity groups,
        // deny precedence, branch scopes and external directory membership can change results.
        return new SecurityEvidenceResult(distinctGroups, edges.Distinct().ToArray(),
            acls.Take(MaxAclEntries).ToArray(), issues,
            new(groupsComplete, membershipsComplete, aclsCollected, false));
    }

    private async Task<IReadOnlyList<AclRow>> ReadAclAsync(string root, string token, string securityToken,
        CancellationToken ct)
    {
        var url = $"{root}/_apis/accesscontrollists/{GitNamespace}?token={Uri.EscapeDataString(securityToken)}&api-version=7.1";
        using var response = await ReadAsync(url, token, ct);
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
                items.Add(new(returnedToken, descriptor, allowMask, denyMask, inherit));
                if (items.Count > MaxAclEntries) throw new DiscoveryProviderException("AclEntriesTruncated");
            }
        }
        return items;
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
