using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

// Read-only source adapter. Never project credential payloads or variable values.
public sealed class AzureDevOpsWorkloadReader(HttpClient client, ServerEndpointPolicy? policy = null,
    string? serverRoot = null, AzureDevOpsServicesEndpoints? services = null)
    : IAzureWorkloadReader, IServerAwareWorkloadReader, IPipelineYamlReader, IRepositoryFileReader
{
    // Cloud keeps the fixed Microsoft host; a server root was already policy-checked.
    private string Collection(string organization) => serverRoot is null
        ? (services ?? AzureDevOpsServicesEndpoints.Cloud).Core(organization)
        : $"{serverRoot}/{Uri.EscapeDataString(organization)}";
    private string Api => serverRoot is null ? "7.1" : "7.0";

    public IAzureWorkloadReader ForServer(string baseUrl)
    {
        var normalized = (policy ?? throw new ArgumentException("Self-hosted providers are disabled.")).Normalize(baseUrl);
        return new AzureDevOpsWorkloadReader(client, policy, normalized, services);
    }

    public async Task<AzurePipelineSource> GetPipelineAsync(
        string organization, string projectId, int pipelineId, string token, CancellationToken ct)
    {
        var root = $"{Collection(organization)}/{Uri.EscapeDataString(projectId)}";
        var api = Api;
        using var pipe = await GetAsync($"{root}/_apis/pipelines/{pipelineId}?api-version={api}", token, ct);
        using var definition = await GetAsync($"{root}/_apis/build/definitions/{pipelineId}?api-version={api}", token, ct);
        var configuration = Property(pipe.RootElement, "configuration");
        var kind = Text(configuration, "type") ?? "unknown";
        var path = Text(configuration, "path") ?? Text(Property(definition.RootElement, "process"), "yamlFilename");
        var repo = Text(Property(configuration, "repository"), "id") ??
                   Text(Property(definition.RootElement, "repository"), "id");

        var variables = new List<AzureVariableReference>();
        var defs = Property(definition.RootElement, "variables");
        if (defs.ValueKind == JsonValueKind.Object)
            foreach (var v in defs.EnumerateObject())
                variables.Add(new(v.Name, True(v.Value, "isSecret"), "pipeline"));

        var groups = Property(definition.RootElement, "variableGroups");
        if (groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray().Take(30))
                if (GroupId(g) is { } id and > 0)
                {
                    try
                    {
                        using var group = await GetAsync($"{root}/_apis/distributedtask/variablegroups/{id}?api-version={api}", token, ct);
                        var groupName = Text(group.RootElement, "name") ?? $"group-{id}";
                        var values = Property(group.RootElement, "variables");
                        if (values.ValueKind == JsonValueKind.Object)
                            foreach (var item in values.EnumerateObject())
                                variables.Add(new(item.Name, True(item.Value, "isSecret"), groupName));
                    }
                    catch (HttpRequestException) { /* missing source group is not inferred as configured */ }
                }

        var endpoints = new List<AzureServiceReference>();
        try
        {
            using var serviceEndpoints = await GetAsync(
                $"{root}/_apis/serviceendpoint/endpoints?api-version={api}", token, ct);
            foreach (var e in Items(serviceEndpoints))
                if (Text(e, "name") is { Length: > 0 } name)
                    endpoints.Add(new(name, Text(e, "type") ?? "unknown"));
        }
        catch (HttpRequestException) { /* report incomplete via manual target checklist */ }

        // Prove the YAML file exists, but never read it into an AI prompt or API result.
        string? yaml = null;
        PipelineStructure? structure = null;
        if (kind == "yaml" && !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(repo))
            try
            {
                var url = $"{root}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/items" +
                          $"?path={Uri.EscapeDataString(path)}&includeContent=true&$format=json&api-version={api}";
                using var item = await GetAsync(url, token, ct);
                if (Text(item.RootElement, "content") is { Length: > 0 } raw)
                {
                    yaml = "available";
                    structure = AzureYamlTaskAnalyzer.Analyze(raw);
                }
            }
            catch (HttpRequestException) { /* incomplete */ }

        return new(pipelineId, Text(pipe.RootElement, "name") ?? $"pipeline-{pipelineId}",
            kind, path, repo, variables.DistinctBy(v => (v.Name, v.Group)).Take(200).ToArray(),
            endpoints.Take(100).ToArray(), yaml, structure);
    }

    public const int MaxPipelineYamlChars = 200_000;

    public async Task<PipelineYaml> ReadPipelineYamlAsync(
        string organization, string projectId, int pipelineId, string token, CancellationToken ct)
    {
        var root = $"{Collection(organization)}/{Uri.EscapeDataString(projectId)}";
        using var pipe = await GetAsync($"{root}/_apis/pipelines/{pipelineId}?api-version={Api}", token, ct);
        var configuration = Property(pipe.RootElement, "configuration");
        if ((Text(configuration, "type") ?? "unknown") != "yaml") return await ReadClassicAsync(organization, root, pipelineId, token, ct);
        var path = Text(configuration, "path");
        var repo = Text(Property(configuration, "repository"), "id");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(repo))
        {
            using var definition = await GetAsync($"{root}/_apis/build/definitions/{pipelineId}?api-version={Api}", token, ct);
            path = string.IsNullOrEmpty(path) ? Text(Property(definition.RootElement, "process"), "yamlFilename") : path;
            repo = string.IsNullOrEmpty(repo) ? Text(Property(definition.RootElement, "repository"), "id") : repo;
        }
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(repo)) return new(null, false);
        try
        {
            var url = $"{root}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/items" +
                      $"?path={Uri.EscapeDataString(path)}&includeContent=true&$format=json&api-version={Api}";
            using var item = await GetAsync(url, token, ct);
            if (Text(item.RootElement, "content") is not { Length: > 0 } raw) return new(null, false);
            return raw.Length > MaxPipelineYamlChars ? new(null, true, repo, path) : new(raw, false, repo, path);
        }
        catch (HttpRequestException) { return new(null, false, repo, path); }
    }

    // A classic (designer) build pipeline has no file: its source is the definition. It is read with GET and rendered as text by ClassicPipelineRenderer, so the conversion works on text like for any other pipeline.
    // What the definition only holds by id is asked of the organization, best effort (a refusal just leaves the id in the text): the service connections its inputs name, and, for tasks the built-in table does not
    // know, the organization's task catalogue (an internal endpoint Microsoft does not document, so it is only a fallback, never relied on). At most 3 requests plus one per unknown task, MaxLookups at most.
    private async Task<PipelineYaml> ReadClassicAsync(string organization, string root, int pipelineId, string token, CancellationToken ct)
    {
        JsonDocument definition;
        try { definition = await GetAsync($"{root}/_apis/build/definitions/{pipelineId}?api-version={Api}", token, ct); }
        catch (HttpRequestException) { return new(null, false); }
        using (definition)
        {
            if (!ClassicPipelineRenderer.IsDesigner(definition.RootElement)) return new(null, false);

            var endpointNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var endpoints = await GetAsync($"{root}/_apis/serviceendpoint/endpoints?api-version={Api}", token, ct);
                foreach (var endpoint in Items(endpoints))
                    if (ClassicTaskCatalog.NormalizeId(Text(endpoint, "id")) is { } id && Text(endpoint, "name") is { Length: > 0 } name) endpointNames[id] = name;
            }
            catch (HttpRequestException) { /* the ids stay in the text */ }

            var taskNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ClassicPipelineRenderer.TaskIdsToLookUp(definition.RootElement))
                try
                {
                    using var task = await GetAsync($"{Collection(organization)}/_apis/distributedtask/tasks/{id}?api-version={Api}", token, ct);
                    if (TaskName(task.RootElement) is { } name) taskNames[id] = name;
                }
                catch (HttpRequestException) { /* the id stays in the text */ }

            var rendering = ClassicPipelineRenderer.Render(definition.RootElement, new(endpointNames, taskNames));
            if (rendering is null) return new(null, false);
            var repository = Text(Property(definition.RootElement, "repository"), "id");
            return rendering.TooLarge
                ? new(null, true, repository, null, PipelineSourceOrigins.ClassicDefinition, rendering.Notes)
                : new(rendering.Text, false, repository, null, PipelineSourceOrigins.ClassicDefinition, rendering.Notes);
        }
    }

    // The name of a task from the catalogue's answer: a list of its versions (as `value` or as the array itself) or one definition; the newest version's name.
    private static string? TaskName(JsonElement answer)
    {
        var items = answer.ValueKind == JsonValueKind.Array ? answer : Property(answer, "value");
        if (items.ValueKind != JsonValueKind.Array) return Text(answer, "name");
        string? best = null;
        var bestMajor = -1;
        foreach (var item in items.EnumerateArray().Take(100))
        {
            if (Text(item, "name") is not { Length: > 0 } name) continue;
            var major = Property(Property(item, "version"), "major");
            var value = major.ValueKind == JsonValueKind.Number && major.TryGetInt32(out var parsed) ? parsed : 0;
            if (value >= bestMajor) { best = name; bestMajor = value; }
        }
        return best;
    }

    // One file of one repository (a template the pipeline includes), GET only, in the organization of the connection. The project, repository and path were validated as plain names by the caller and are
    // escaped here; `versionType` is "branch" or "tag" with a plain `version`, or both null for the default branch. A folder, a missing file or a refusal is "not readable" (an exception), never content.
    public async Task<RepositoryFile> ReadRepositoryFileAsync(string organization, string projectId, string repository, string path, string? versionType, string? version,
        string token, CancellationToken ct)
    {
        if (versionType is not (null or "branch" or "tag") || (versionType is null) != (version is null)) throw new HttpRequestException("SourceReadFailed");
        var url = $"{Collection(organization)}/{Uri.EscapeDataString(projectId)}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/items" +
                  $"?path={Uri.EscapeDataString(path)}&includeContent=true&$format=json&api-version={Api}" +
                  (version is null ? "" : $"&versionDescriptor.version={Uri.EscapeDataString(version)}&versionDescriptor.versionType={versionType}");
        using var item = await GetAsync(url, token, ct);
        if (Text(item.RootElement, "content") is not { Length: > 0 } raw) return new(null, false);
        return raw.Length > MaxPipelineYamlChars ? new(null, true) : new(raw, false);
    }

    public async Task<IReadOnlyList<AzureBoardItem>> GetBoardItemsAsync(
        string organization, string projectId, IReadOnlyList<int> ids, string token, CancellationToken ct)
    {
        var url = $"{Collection(organization)}/{Uri.EscapeDataString(projectId)}/_apis/wit/workitems" +
                  $"?ids={string.Join(",", ids)}&$expand=Relations&errorPolicy=Omit&api-version={Api}";
        using var doc = await GetAsync(url, token, ct);
        var result = new List<AzureBoardItem>();
        foreach (var item in Items(doc))
        {
            if (!item.TryGetProperty("id", out var v) || !v.TryGetInt32(out var id)) continue;
            var fields = Property(item, "fields");
            var links = new List<AzureBoardLink>();
            var rels = Property(item, "relations");
            if (rels.ValueKind == JsonValueKind.Array)
                foreach (var r in rels.EnumerateArray().Take(300))
                {
                    var rel = Text(r, "rel") ?? "";
                    var address = Text(r, "url") ?? "";
                    if (rel.StartsWith("System.LinkTypes.", StringComparison.Ordinal))
                    {
                        if (int.TryParse(address.Split('/').LastOrDefault(), out var n))
                            links.Add(new("work-item", address, n, rel));
                        else links.Add(new("unknown", address));
                    }
                    else if (rel == "ArtifactLink" && address.Contains("/Git/Commit/", StringComparison.OrdinalIgnoreCase))
                        links.Add(new("commit", address));
                    else if (rel == "ArtifactLink" && address.Contains("/Git/PullRequestId/", StringComparison.OrdinalIgnoreCase))
                        links.Add(new("pull-request", address));
                    else links.Add(new("unknown", address));
                }
            result.Add(new(id, Text(fields, "System.Title") ?? $"Item {id}",
                Text(fields, "System.WorkItemType") ?? "Unknown",
                Text(fields, "System.State") ?? "Unknown",
                Text(fields, "System.Description"), links));
        }
        return result;
    }

    // The build definition lists variable groups as objects ({ "id": 3, "name": ... }) on Azure DevOps;
    // a bare integer is the older/lab shape. Both identify the group.
    private static int? GroupId(JsonElement group) =>
        group.ValueKind == JsonValueKind.Number && group.TryGetInt32(out var plain) ? plain
        : group.ValueKind == JsonValueKind.Object && group.TryGetProperty("id", out var id) &&
          id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var nested) ? nested : null;

    private async Task<JsonDocument> GetAsync(string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400 || !response.IsSuccessStatusCode)
            throw new HttpRequestException("SourceReadFailed");
        if (response.Content.Headers.ContentLength is > 4_000_000)
            throw new HttpRequestException("SourceResponseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static JsonElement Property(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool True(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.True;
    private static IEnumerable<JsonElement> Items(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new HttpRequestException("UnexpectedProviderResponse");
        return values.EnumerateArray();
    }
}
