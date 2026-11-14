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

    // What the template reader (ADR-023) assumes of the real service, by GET only: the pipeline definition says where its file is, a file is readable by repository id and by branch, a missing path is an
    // error and not content, and what a real pipeline's `template:` lines resolve to. Names, counts and reasons are reported; no text of any file ever is.
    [LiveAdoFact]
    public async Task Template_reading_asks_the_real_service_what_the_simulator_assumes()
    {
        using var http = Raw();
        var reader = new AzureDevOpsWorkloadReader(LiveEnvironment.ReadOnlyClient(), null, null, Endpoints);
        var projects = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/_apis/projects?$top=200&api-version=7.1");
        (string ProjectId, int PipelineId, PipelineYaml Yaml)? located = null;
        (string ProjectId, int PipelineId, PipelineYaml Yaml, ResolvedTemplates Templates)? withTemplates = null;
        var looked = 0;
        foreach (var project in projects.Json!.RootElement.GetProperty("value").EnumerateArray())
        {
            if (Ado.Project is not null && !Ado.Project.Equals(project.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase)) continue;
            var projectId = project.GetProperty("id").GetString()!;
            var list = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{projectId}/_apis/pipelines?api-version=7.1");
            if (list.Status != 200) continue;
            foreach (var pipeline in list.Json!.RootElement.GetProperty("value").EnumerateArray())
            {
                if (++looked > 25) break;
                var id = pipeline.GetProperty("id").GetInt32();
                var yaml = await reader.ReadPipelineYamlAsync(Ado.Organization, projectId, id, Ado.Pat, default);
                if (yaml.Content is null) continue;
                located ??= (projectId, id, yaml);
                var resolved = await PipelineTemplates.ResolveAsync(yaml.Content, new(projectId, yaml.RepositoryId, yaml.Path), reader, Ado.Organization, Ado.Pat, new(), default);
                if (!resolved.IsEmpty) { withTemplates = (projectId, id, yaml, resolved); break; }
            }
            if (withTemplates is not null || looked > 25) break;
        }
        if (located is null)
        {
            report.Record("ado-services", "template-read", "info", "No YAML pipeline was found to read templates for.");
            return;
        }

        var (locatedProject, _, root) = located.Value;
        var hasLocation = !string.IsNullOrEmpty(root.RepositoryId) && !string.IsNullOrEmpty(root.Path);
        report.Record("ado-services", "template-root-location", hasLocation ? "confirmed" : "differs",
            hasLocation ? "The pipeline definition names the repository (by id) and the path of its YAML file." :
                "The pipeline definition did NOT give the repository and path of its file: relative templates cannot be found.");
        Assert.True(hasLocation);

        // the file the definition points at, read again by repository id (what a relative template read does) and compared with what the pipeline read returned
        var files = (IRepositoryFileReader)reader;
        var again = await files.ReadRepositoryFileAsync(Ado.Organization, locatedProject, root.RepositoryId!, root.Path!, null, null, Ado.Pat, default);
        report.Record("ado-services", "repository-file-read-by-repository-id", again.Content == root.Content ? "confirmed" : "differs",
            again.Content == root.Content ? "A file is read by repository id and path, on the default branch, with the same content the pipeline read returned." :
                $"Reading the pipeline's own file by repository id returned different content (null: {again.Content is null}).");
        Assert.Equal(root.Content, again.Content);

        // by branch: the repository's default branch, as a `ref: refs/heads/...` of a repository resource would name it
        var repository = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{locatedProject}/_apis/git/repositories/{Uri.EscapeDataString(root.RepositoryId!)}?api-version=7.1");
        var defaultBranch = repository.Json is not null && repository.Json.RootElement.TryGetProperty("defaultBranch", out var b) ? b.GetString() : null;
        var (type, version, ok) = PipelineTemplates.ParseRef(defaultBranch);
        if (!ok || type is null)
            report.Record("ado-services", "repository-file-read-at-branch", "info", "The repository has no default branch name this reader can use; the read by branch was not tried.");
        else
        {
            var atBranch = await files.ReadRepositoryFileAsync(Ado.Organization, locatedProject, root.RepositoryId!, root.Path!, type, version, Ado.Pat, default);
            report.Record("ado-services", "repository-file-read-at-branch", atBranch.Content is not null ? "confirmed" : "differs",
                atBranch.Content is not null ? "A file is read at a branch with versionDescriptor (type branch)." : "Reading at the default branch with versionDescriptor returned no content.");
            Assert.NotNull(atBranch.Content);
        }

        // a path that is not there is an error (the resolver reports it as NotReadable), never content
        try
        {
            var missing = await files.ReadRepositoryFileAsync(Ado.Organization, locatedProject, root.RepositoryId!, "/__no_such_template__/never.yml", null, null, Ado.Pat, default);
            report.Record("ado-services", "repository-file-missing-path", missing.Content is null ? "info" : "differs",
                missing.Content is null ? "A missing path answered without content (no error)." : "A missing path answered WITH content: the reader would send a file that does not exist.");
            Assert.Null(missing.Content);
        }
        catch (HttpRequestException)
        {
            report.Record("ado-services", "repository-file-missing-path", "confirmed", "A missing path is an HTTP error (the resolver reports the template as NotReadable).");
        }

        // a folder is not a template
        try
        {
            var folder = await files.ReadRepositoryFileAsync(Ado.Organization, locatedProject, root.RepositoryId!, "/", null, null, Ado.Pat, default);
            report.Record("ado-services", "repository-file-folder", folder.Content is null ? "confirmed" : "differs",
                folder.Content is null ? "A folder answers without content." : "A folder answered WITH content.");
        }
        catch (HttpRequestException ex) { report.Record("ado-services", "repository-file-folder", "info", "A folder is an HTTP error: " + ex.Message); }

        // what a real pipeline's templates resolve to: counts, locations and reasons only
        if (withTemplates is null)
            report.Record("ado-services", "template-resolution", "info", $"None of the {looked} YAML pipelines looked at includes a `template:`; point GITHUBMIGRATOR_LIVE_ADO_PROJECT at a project that does to measure it.");
        else
        {
            var resolved = withTemplates.Value.Templates;
            output.WriteLine($"pipeline {withTemplates.Value.PipelineId}: {resolved.Files.Count} templates read, {resolved.Unresolved.Count} not");
            report.Record("ado-services", "template-resolution", resolved.Files.Count > 0 ? "confirmed" : "info",
                $"{resolved.Files.Count} template file(s) read ({string.Join("; ", resolved.Files.Select(f => $"{(f.Repository == "self" ? "self" : "another repository")}:{f.Path} {f.Bytes} B, tasks {f.Tasks.Count}"))}); " +
                $"{resolved.Unresolved.Count} not read ({string.Join("; ", resolved.Unresolved.Select(u => $"{u.Reference} {u.Reason}"))}).");
        }
        Assert.DoesNotContain(Ado.Pat, JsonSerializer.Serialize(located.Value.Yaml with { Content = null }));
    }

    // The documented BuildDefinition.process has only `type`: how a classic (designer) pipeline's phases, steps and tasks really look is not in Microsoft's reference, and the simulator's shape is an assumption.
    // This records the shape of the first classic pipeline found, as key NAMES and value kinds only (never a value), plus whether the task catalogue answers and resolves a step's task. It is what a converter
    // of classic pipelines would have to be built on.
    [LiveAdoFact]
    public async Task Classic_pipeline_shape_is_recorded_as_key_names_only()
    {
        using var http = Raw();
        var projects = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/_apis/projects?$top=200&api-version=7.1");
        string? projectId = null; int definitionId = 0; string? configurationType = null;
        var looked = 0;
        foreach (var project in projects.Json!.RootElement.GetProperty("value").EnumerateArray())
        {
            if (Ado.Project is not null && !Ado.Project.Equals(project.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase)) continue;
            var id = project.GetProperty("id").GetString()!;
            var list = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{id}/_apis/pipelines?api-version=7.1");
            if (list.Status != 200) continue;
            foreach (var pipeline in list.Json!.RootElement.GetProperty("value").EnumerateArray())
            {
                if (++looked > 40) break;
                var one = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{id}/_apis/pipelines/{pipeline.GetProperty("id").GetInt32()}?api-version=7.1");
                if (one.Json is null || !one.Json.RootElement.TryGetProperty("configuration", out var configuration) || !configuration.TryGetProperty("type", out var type)) continue;
                if (type.GetString() is "yaml" or null) continue;
                projectId = id; definitionId = pipeline.GetProperty("id").GetInt32(); configurationType = type.GetString();
                break;
            }
            if (projectId is not null || looked > 40) break;
        }
        if (projectId is null)
        {
            report.Record("ado-services", "classic-pipeline-shape", "info", $"None of the {looked} pipelines looked at is a classic one: the shape could not be recorded. Point ..._ADO_PROJECT at a project that has one.");
            return;
        }
        report.Record("ado-services", "classic-pipeline-configuration-type", "info", $"`configuration.type` of a classic pipeline in _apis/pipelines is '{configurationType}' (the product treats anything but 'yaml' as classic).");

        var definition = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/{projectId}/_apis/build/definitions/{definitionId}?api-version=7.1");
        Assert.Equal(200, definition.Status);
        var root = definition.Json!.RootElement;
        report.Record("ado-services", "classic-definition-keys", "info", "definition keys: " + Keys(root));
        if (!root.TryGetProperty("process", out var process))
        {
            report.Record("ado-services", "classic-process-shape", "differs", "The build definition has no `process`.");
            return;
        }
        report.Record("ado-services", "classic-process-shape", "info", "process keys: " + Keys(process) + (process.TryGetProperty("type", out var processType) ? $"; type={processType}" : ""));
        if (root.TryGetProperty("variables", out var variables) && variables.ValueKind == JsonValueKind.Object)
        {
            var first = variables.EnumerateObject().FirstOrDefault();
            report.Record("ado-services", "classic-variables-shape", "info", $"{variables.EnumerateObject().Count()} variable(s); a variable's keys: " + (first.Value.ValueKind == JsonValueKind.Object ? Keys(first.Value) : "none"));
        }
        foreach (var name in new[] { "queue", "triggers", "options", "repository", "demands" })
            if (root.TryGetProperty(name, out var part))
                report.Record("ado-services", "classic-" + name + "-shape", "info", part.ValueKind == JsonValueKind.Array
                    ? $"array of {part.GetArrayLength()}; an item's keys: " + (part.GetArrayLength() > 0 ? Keys(part[0]) : "none")
                    : "keys: " + Keys(part));

        if (!process.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array || phases.GetArrayLength() == 0)
        {
            report.Record("ado-services", "classic-phases-shape", "differs", "process has no `phases` array: the steps of a classic pipeline are not where the simulator puts them.");
            return;
        }
        var phase = phases[0];
        report.Record("ado-services", "classic-phases-shape", "info", $"{phases.GetArrayLength()} phase(s); a phase's keys: " + Keys(phase));
        // the places the converter reads besides the steps themselves (key names only): where a phase says which pool and demands it runs on, the process-level agent specification, what a phase depends on, and what
        // "Get sources" keeps in the repository's properties
        if (phase.TryGetProperty("target", out var phaseTarget) && phaseTarget.ValueKind == JsonValueKind.Object)
            report.Record("ado-services", "classic-phase-target-shape", "info", "keys: " + Keys(phaseTarget) + (phaseTarget.TryGetProperty("type", out var targetType) ? $"; type={targetType}" : "") +
                (phaseTarget.TryGetProperty("queue", out var targetQueue) && targetQueue.ValueKind == JsonValueKind.Object ? "; queue keys: " + Keys(targetQueue) : "") +
                (phaseTarget.TryGetProperty("agentSpecification", out var targetSpec) && targetSpec.ValueKind == JsonValueKind.Object ? "; agentSpecification keys: " + Keys(targetSpec) : ""));
        if (process.TryGetProperty("target", out var processTarget) && processTarget.ValueKind == JsonValueKind.Object)
            report.Record("ado-services", "classic-process-target-shape", "info", "keys: " + Keys(processTarget) +
                (processTarget.TryGetProperty("agentSpecification", out var processSpec) && processSpec.ValueKind == JsonValueKind.Object ? "; agentSpecification keys: " + Keys(processSpec) : ""));
        if (phase.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array)
            report.Record("ado-services", "classic-phase-dependencies-shape", "info", $"{dependencies.GetArrayLength()} dependenc(ies); an item's keys: " + (dependencies.GetArrayLength() > 0 ? Keys(dependencies[0]) : "none"));
        if (root.TryGetProperty("repository", out var repository) && repository.TryGetProperty("properties", out var repositoryProperties) && repositoryProperties.ValueKind == JsonValueKind.Object)
            report.Record("ado-services", "classic-repository-properties-shape", "info", "property names: " + string.Join(",", repositoryProperties.EnumerateObject().Select(p => p.Name).Order().Take(20)));
        if (!phase.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
        {
            report.Record("ado-services", "classic-steps-shape", "differs", "the first phase has no `steps` array.");
            return;
        }
        var step = steps[0];
        report.Record("ado-services", "classic-steps-shape", "info", $"{steps.GetArrayLength()} step(s) in the first phase; a step's keys: " + Keys(step));
        string? taskId = null;
        if (step.TryGetProperty("task", out var task))
        {
            var kind = task.ValueKind;
            taskId = kind == JsonValueKind.Object && task.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
            report.Record("ado-services", "classic-step-task-shape", kind == JsonValueKind.String ? "differs" : "info",
                kind == JsonValueKind.String ? "A step's `task` is a plain string (that is what the simulator emits, 'Name@N'): confirm it is the same on the real service." :
                    $"A step's `task` is {kind}; keys: " + Keys(task) + "; id is a GUID: " + Guid.TryParse(taskId, out _));
        }
        if (step.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
            report.Record("ado-services", "classic-step-inputs-shape", "info", $"{inputs.EnumerateObject().Count()} input(s); names only: " + string.Join(",", inputs.EnumerateObject().Select(p => p.Name).Order().Take(12)));

        // task GUIDs are only meaningful with the catalogue: does it answer, and does it know this step's task?
        var catalogue = await GetAsync(http, $"{Endpoints.Core(Ado.Organization)}/_apis/distributedtask/tasks?api-version=7.1");
        if (catalogue.Status != 200 || catalogue.Json is null || !catalogue.Json.RootElement.TryGetProperty("value", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            report.Record("ado-services", "task-catalogue", "differs", $"GET _apis/distributedtask/tasks answered HTTP {catalogue.Status} without a `value` array.");
        else
        {
            report.Record("ado-services", "task-catalogue", "info", $"{tasks.GetArrayLength()} task definitions; an item's keys: " + (tasks.GetArrayLength() > 0 ? Keys(tasks[0]) : "none"));
            if (taskId is not null)
                report.Record("ado-services", "task-catalogue-resolves-step-task", tasks.EnumerateArray().Any(t => t.TryGetProperty("id", out var i) && string.Equals(i.GetString(), taskId, StringComparison.OrdinalIgnoreCase)) ? "confirmed" : "differs",
                    "whether the catalogue has the id of the first step's task (the name and version of a step come from there).");
        }
        output.WriteLine("classic pipeline shape recorded as key names only; see live-smoke-report.json");
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
