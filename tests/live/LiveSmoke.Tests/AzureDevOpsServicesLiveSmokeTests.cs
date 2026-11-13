using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.AzureDevOps;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;
using Xunit.Abstractions;

namespace GitHubMigrator.LiveSmoke.Tests;

// Read-only checks of the real product adapters against a REAL Azure DevOps Services organization,
// plus "contract probes" that record whether the real service answers the way tools/AdoSim assumes.
// See docs/live-smoke-tests.md. The PAT is only ever sent in an Authorization header or to git's environment.
[Collection("live")]
public sealed class AzureDevOpsServicesLiveSmokeTests(LiveReport report, ITestOutputHelper output)
{
    private static LiveAdo Ado => LiveEnvironment.Ado!;
    private static AzureDevOpsServicesEndpoints Endpoints => LiveEnvironment.AdoEndpoints;
    private const string GitNamespace = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87";

    private static HttpClient Raw()
    {
        var http = LiveEnvironment.ReadOnlyClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Ado.Pat)));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    private static async Task<(int Status, JsonDocument? Json, string? Continuation)> GetAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        // an invalid or unauthorized PAT may answer 203 with an HTML sign-in page instead of JSON
        JsonDocument? json = null;
        try { json = JsonDocument.Parse(text); } catch (JsonException) { }
        var continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
        return ((int)response.StatusCode, json, continuation);
    }

    private static string Keys(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? string.Join(",", element.EnumerateObject().Select(p => p.Name).Order()) : element.ValueKind.ToString();

    [LiveAdoFact]
    public async Task The_product_validator_accepts_the_real_pat_and_rejects_a_bogus_one()
    {
        var validator = new AzureDevOpsConnectionValidator(LiveEnvironment.ReadOnlyClient(), Endpoints);
        var ok = await validator.ValidateAsync(Ado.Organization, Ado.Pat, default);
        output.WriteLine($"valid pat: {ok.IsValid} code={ok.Code} assessment={ok.PermissionAssessment}");
        Assert.True(ok.IsValid, "The PAT needs at least 'Project and Team: Read'. Code: " + ok.Code);

        var bogus = await validator.ValidateAsync(Ado.Organization, "invalid-pat-for-live-smoke-test", default);
        output.WriteLine($"bogus pat: {bogus.IsValid} code={bogus.Code}");
        Assert.False(bogus.IsValid);
        report.Record("ado-services", "validator-invalid-pat-code", "info", "The product validator answers '" + bogus.Code + "' for an invalid PAT.");
    }

    [LiveAdoFact]
    public async Task Discovery_reads_the_real_estate_and_leaks_no_credential()
    {
        var reader = new AzureDevOpsDiscoveryReader(LiveEnvironment.ReadOnlyClient(), TimeProvider.System, null, Endpoints);
        var inventory = await reader.DiscoverAsync(Guid.NewGuid(), Ado.Organization, Ado.Pat, default);
        var repositories = inventory.Snapshot.Projects.SelectMany(p => p.Repositories).ToArray();
        var summary = $"projects={inventory.Snapshot.Projects.Count} gitRepos={repositories.Count(r => r.VersionControl == VersionControlKind.Git)} " +
            $"tfvc={repositories.Count(r => r.VersionControl == VersionControlKind.Tfvc)} refs={inventory.Refs.Count} users={inventory.Snapshot.Identities.Count} " +
            $"groups={inventory.Groups.Count} memberships={inventory.Memberships.Count} acl={inventory.AclEvidence.Count} issues=[{string.Join(",", inventory.Issues.Select(i => i.Code).Distinct())}]";
        output.WriteLine(summary);
        report.Record("ado-services", "discovery", "info", summary);

        Assert.NotEmpty(inventory.Snapshot.Projects);
        Assert.Equal("azure-devops-services", inventory.SourceProvider);
        Assert.DoesNotContain(Ado.Pat, JsonSerializer.Serialize(inventory));
        report.Record("ado-services", "graph-identities-complete", inventory.IdentitiesComplete ? "confirmed" : "differs",
            $"IdentitiesComplete={inventory.IdentitiesComplete}; GroupsComplete={inventory.SecurityCoverage.GroupsComplete}; AclsCollected={inventory.SecurityCoverage.AclsCollected}");

        var known = inventory.Snapshot.Identities.Select(i => i.LegacyDescriptor).Concat(inventory.Groups.Select(g => g.LegacyDescriptor))
            .Where(d => d is not null).ToHashSet();
        if (inventory.AclEvidence.Count > 0)
        {
            var distinct = inventory.AclEvidence.Select(a => a.SecurityDescriptor).Distinct().Count();
            var resolved = inventory.AclEvidence.Select(a => a.SecurityDescriptor).Distinct().Count(d => known.Contains(d));
            // real organizations always have some orphaned principals: require that most ACEs resolve, report the exact ratio
            report.Record("ado-services", "acl-principals-resolve-through-graph-legacy-descriptors", resolved * 2 >= distinct ? "confirmed" : "differs",
                $"{resolved} of {distinct} distinct ACE descriptors resolved to a Graph legacyDescriptor (the simulator resolves all but one orphan).");
        }
    }

    [LiveAdoFact]
    public async Task A_real_repository_is_fetched_into_an_exact_verified_mirror()
    {
        using var http = Raw();
        var projects = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/_apis/projects?$top=200&api-version=7.1");
        Assert.Equal(200, projects.Status);
        var candidates = new List<(string ProjectId, string ProjectName, string RepoId, string RepoName, long Size)>();
        foreach (var project in projects.Json!.RootElement.GetProperty("value").EnumerateArray())
        {
            var projectId = project.GetProperty("id").GetString()!;
            if (Ado.Project is not null && !Ado.Project.Equals(project.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase)) continue;
            var repos = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{projectId}/_apis/git/repositories?api-version=7.1");
            if (repos.Status != 200 || repos.Json is null) continue;
            foreach (var repo in repos.Json.RootElement.GetProperty("value").EnumerateArray())
            {
                if (repo.TryGetProperty("isDisabled", out var disabled) && disabled.GetBoolean()) continue;
                var size = repo.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                var name = repo.GetProperty("name").GetString()!;
                if (Ado.Repository is not null ? !Ado.Repository.Equals(name, StringComparison.OrdinalIgnoreCase) : size is <= 0 or > 100L * 1024 * 1024) continue;
                candidates.Add((projectId, project.GetProperty("name").GetString()!, repo.GetProperty("id").GetString()!, name, size));
            }
        }
        if (candidates.Count == 0)
        {
            report.Record("ado-services", "git-fetch", "info", "No non-empty repository up to 100 MB found (set GITHUBMIGRATOR_LIVE_ADO_REPO to pick one).");
            return;
        }
        var chosen = candidates.OrderBy(c => c.Size).First();
        var refsReply = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{chosen.ProjectId}/_apis/git/repositories/{chosen.RepoId}/refs?api-version=7.1&$top=1000");
        Assert.Equal(200, refsReply.Status);
        var rawRefs = refsReply.Json!.RootElement.GetProperty("value").EnumerateArray()
            .Where(r => r.GetProperty("name").GetString()!.StartsWith("refs/heads/") || r.GetProperty("name").GetString()!.StartsWith("refs/tags/")).ToArray();
        var expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(rawRefs
            .Select(r => new GitMirrorRef(r.GetProperty("name").GetString()!, r.GetProperty("objectId").GetString()!)).ToArray());
        report.Record("ado-services", "refs-pagination-header", refsReply.Continuation is null ? "info" : "info",
            $"{expected.Count} refs in one page; continuation header present: {refsReply.Continuation is not null}");

        var root = Path.Combine(Path.GetTempPath(), "live-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var connectionId = Guid.NewGuid();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await new AzureDevOpsGitMirrorFetcher(root, null, new AzureGitRemotePolicy(Endpoints, null)).FetchAsync(new AzureGitMirrorFetchRequest(
                new(connectionId, Ado.Organization, chosen.ProjectName, chosen.RepoName, chosen.RepoId), Ado.Pat, expected), default);
            watch.Stop();
            Assert.True(Directory.Exists(LocalGitSourceBundleAcquirer.LocalMirrorDirectory(root, connectionId, chosen.RepoId)));
            output.WriteLine($"fetched {chosen.ProjectName}/{chosen.RepoName}: {expected.Count} refs in {watch.Elapsed.TotalSeconds:0.0}s, mirror verified (exact refs + fsck)");
            var annotated = rawRefs.Count(r => r.TryGetProperty("peeledObjectId", out _));
            report.Record("ado-services", "git-fetch", "confirmed",
                $"Exact mirror of {expected.Count} refs verified (fsck --strict) in {watch.Elapsed.TotalSeconds:0.0}s. " +
                (annotated > 0 ? $"{annotated} annotated tags: the REST objectId equals the tag object id git reports."
                    : "No annotated tag in this repository: the REST objectId of annotated tags is NOT exercised (pick a repository that has one with GITHUBMIGRATOR_LIVE_ADO_REPO)."));
        }
        finally
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    [LiveAdoFact]
    public async Task The_first_pipeline_is_read_with_variable_groups_and_service_connections()
    {
        using var http = Raw();
        var projects = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/_apis/projects?$top=200&api-version=7.1");
        string? projectId = null; int pipelineId = 0;
        foreach (var project in projects.Json!.RootElement.GetProperty("value").EnumerateArray())
        {
            if (Ado.Project is not null && !Ado.Project.Equals(project.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase)) continue;
            var list = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{project.GetProperty("id").GetString()}/_apis/pipelines?api-version=7.1");
            if (list.Status == 200 && list.Json!.RootElement.GetProperty("value").GetArrayLength() > 0)
            {
                projectId = project.GetProperty("id").GetString();
                pipelineId = list.Json.RootElement.GetProperty("value")[0].GetProperty("id").GetInt32();
                break;
            }
        }
        if (projectId is null)
        {
            report.Record("ado-services", "pipeline-read", "info", "No pipeline found in the organization.");
            return;
        }
        var source = await new AzureDevOpsWorkloadReader(LiveEnvironment.ReadOnlyClient(), null, null, Endpoints)
            .GetPipelineAsync(Ado.Organization, projectId, pipelineId, Ado.Pat, default);
        output.WriteLine($"pipeline {source.Id} '{source.Name}' kind={source.Kind} tasks={source.Structure?.Tasks.Count} variables={source.Variables.Count} " +
            $"groups={source.Variables.Select(v => v.Group).Distinct().Count(g => g != "pipeline")} serviceConnections={source.ServiceConnections.Count}");
        Assert.NotNull(source.Name);
        Assert.DoesNotContain(Ado.Pat, JsonSerializer.Serialize(source));
        report.Record("ado-services", "pipeline-read", "confirmed",
            $"kind={source.Kind}; yamlRead={source.SourceYaml is not null}; parseFailed={source.Structure?.ParseFailed}; variables={source.Variables.Count}; serviceConnections={source.ServiceConnections.Count}");

        // BuildDefinition.variableGroups shape (the product reads ints and objects; the simulator emits objects)
        for (var attempt = 0; attempt < 1; attempt++)
        {
            var definition = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{projectId}/_apis/build/definitions/{pipelineId}?api-version=7.1");
            if (definition.Json is null || !definition.Json.RootElement.TryGetProperty("variableGroups", out var groups) || groups.GetArrayLength() == 0)
            {
                report.Record("ado-services", "build-definition-variable-groups-shape", "info", "The first pipeline references no variable group (YAML pipelines list them in the YAML, not in the definition).");
                break;
            }
            var shape = groups[0].ValueKind == JsonValueKind.Object ? "objects" : "integers";
            report.Record("ado-services", "build-definition-variable-groups-shape", shape == "objects" ? "confirmed" : "differs", "variableGroups are " + shape);
        }
    }

    [LiveAdoFact]
    public async Task Contract_probes_record_how_the_real_graph_and_security_apis_answer()
    {
        using var http = Raw();
        var org = Uri.EscapeDataString(Ado.Organization);

        var users = await GetAsync(http, $"{Endpoints.GraphRoot}/{org}/_apis/graph/users?api-version=7.1-preview.1");
        if (users.Status == 200 && users.Json!.RootElement.GetProperty("value").GetArrayLength() > 0)
        {
            var first = users.Json.RootElement.GetProperty("value")[0];
            var hasLegacy = first.TryGetProperty("legacyDescriptor", out _);
            report.Record("ado-services", "graph-users-fields", "info", "keys: " + Keys(first));
            report.Record("ado-services", "graph-users-legacy-descriptor", hasLegacy ? "confirmed" : "differs",
                hasLegacy ? "Graph users publish legacyDescriptor (the simulator links ACE descriptors through it)." :
                    "Graph users do NOT publish legacyDescriptor: the product cannot map ACE principals to users this way and AdoSim must stop emitting it.");
            report.Record("ado-services", "graph-continuation-header", "info", "x-ms-continuationtoken present on first page: " + (users.Continuation is not null));
        }
        else report.Record("ado-services", "graph-users", "differs", $"HTTP {users.Status}: the PAT may lack 'Graph: Read'.");

        var groups = await GetAsync(http, $"{Endpoints.GraphRoot}/{org}/_apis/graph/groups?api-version=7.1-preview.1");
        if (groups.Status == 200 && groups.Json!.RootElement.GetProperty("value").GetArrayLength() > 0)
        {
            var group = groups.Json.RootElement.GetProperty("value")[0];
            report.Record("ado-services", "graph-groups-fields", "info", "keys: " + Keys(group));
            var descriptor = group.GetProperty("descriptor").GetString()!;
            var members = await GetAsync(http, $"{Endpoints.GraphRoot}/{org}/_apis/graph/Memberships/{Uri.EscapeDataString(descriptor)}?direction=down&depth=1&api-version=7.1-preview.1");
            if (members.Status == 200 && members.Json!.RootElement.GetProperty("value").GetArrayLength() > 0)
            {
                var edge = members.Json.RootElement.GetProperty("value")[0];
                var shaped = edge.TryGetProperty("containerDescriptor", out _) && edge.TryGetProperty("memberDescriptor", out _);
                report.Record("ado-services", "graph-membership-edge-shape", shaped ? "confirmed" : "differs", "keys: " + Keys(edge));
            }
        }

        var projects = await GetAsync(http, $"{Endpoints.CoreRoot}/{org}/_apis/projects?$top=50&api-version=7.1");
        foreach (var project in projects.Json!.RootElement.GetProperty("value").EnumerateArray().Take(10))
        {
            var projectId = project.GetProperty("id").GetString()!;
            var repos = await GetAsync(http, $"{Endpoints.CoreRoot}/{org}/{projectId}/_apis/git/repositories?api-version=7.1");
            if (repos.Status != 200 || repos.Json!.RootElement.GetProperty("value").GetArrayLength() == 0) continue;
            var repoId = repos.Json.RootElement.GetProperty("value")[0].GetProperty("id").GetString()!;
            var token = Uri.EscapeDataString($"repoV2/{projectId}/{repoId}");
            var acl = await GetAsync(http, $"{Endpoints.CoreRoot}/{org}/_apis/accesscontrollists/{GitNamespace}?token={token}&includeExtendedInfo=false&api-version=7.1");
            if (acl.Status == 200 && acl.Json!.RootElement.GetProperty("value").GetArrayLength() > 0)
            {
                var list = acl.Json.RootElement.GetProperty("value")[0];
                var keys = list.GetProperty("acesDictionary").EnumerateObject().Select(p => p.Name.Split(';')[0]).Distinct().Order().ToArray();
                report.Record("ado-services", "acl-shape", list.TryGetProperty("inheritPermissions", out _) ? "confirmed" : "differs",
                    "list keys: " + Keys(list) + "; ACE descriptor kinds: " + string.Join(", ", keys));
                break;
            }
            if (acl.Status is 401 or 403)
            {
                report.Record("ado-services", "acl-shape", "info", $"HTTP {acl.Status}: the security APIs need a PAT with 'Security: Manage' (high privilege).");
                break;
            }
        }
        Assert.True(true);
        output.WriteLine("contract probes recorded; see live-smoke-report.json");
    }
}
