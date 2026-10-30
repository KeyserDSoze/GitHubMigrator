using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

// Read-only source adapter. Never project credential payloads or variable values.
public sealed class AzureDevOpsWorkloadReader(HttpClient client) : IAzureWorkloadReader
{
    public async Task<AzurePipelineSource> GetPipelineAsync(
        string organization, string projectId, int pipelineId, string token, CancellationToken ct)
    {
        var root = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(projectId)}";
        using var pipe = await GetAsync($"{root}/_apis/pipelines/{pipelineId}?api-version=7.1", token, ct);
        using var definition = await GetAsync($"{root}/_apis/build/definitions/{pipelineId}?api-version=7.1", token, ct);
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
                if (g.TryGetInt32(out var id) && id > 0)
                {
                    try
                    {
                        using var group = await GetAsync($"{root}/_apis/distributedtask/variablegroups/{id}?api-version=7.1", token, ct);
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
                $"{root}/_apis/serviceendpoint/endpoints?api-version=7.1", token, ct);
            foreach (var e in Items(serviceEndpoints))
                if (Text(e, "name") is { Length: > 0 } name)
                    endpoints.Add(new(name, Text(e, "type") ?? "unknown"));
        }
        catch (HttpRequestException) { /* report incomplete via manual target checklist */ }

        // Prove the YAML file exists, but never read it into an AI prompt or API result.
        string? yaml = null;
        if (kind == "yaml" && !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(repo))
            try
            {
                var url = $"{root}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/items" +
                          $"?path={Uri.EscapeDataString(path)}&includeContent=true&api-version=7.1";
                using var item = await GetAsync(url, token, ct);
                if (Text(item.RootElement, "content") is { Length: > 0 }) yaml = "available";
            }
            catch (HttpRequestException) { /* incomplete */ }

        return new(pipelineId, Text(pipe.RootElement, "name") ?? $"pipeline-{pipelineId}",
            kind, path, repo, variables.DistinctBy(v => (v.Name, v.Group)).Take(200).ToArray(),
            endpoints.Take(100).ToArray(), yaml);
    }

    public async Task<IReadOnlyList<AzureBoardItem>> GetBoardItemsAsync(
        string organization, string projectId, IReadOnlyList<int> ids, string token, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(projectId)}/_apis/wit/workitems" +
                  $"?ids={string.Join(",", ids)}&$expand=Relations&errorPolicy=Omit&api-version=7.1";
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
