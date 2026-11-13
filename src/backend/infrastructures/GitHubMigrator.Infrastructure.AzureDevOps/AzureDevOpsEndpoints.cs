using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.AzureDevOps;

// Single source of truth for where and how an Azure DevOps flavour is queried.
// Services uses the Graph API; Server (2019+) has no Graph API, so identities come from the
// Identities API and are rewritten into the Graph shape the rest of the pipeline already
// checkpoints and verifies.
public sealed class AzureDevOpsEndpoints
{
    private readonly string _graphRoot;

    private AzureDevOpsEndpoints(bool isServer, string root, string graphRoot, string api)
    {
        IsServer = isServer; Root = root; _graphRoot = graphRoot; Api = api;
    }

    public bool IsServer { get; }
    // Collection/organization root: .../{organization} or {server}/{collection}.
    public string Root { get; }
    public string Api { get; }

    public static readonly string[] ServerApiVersions = ["7.1", "7.0", "6.0", "5.2", "5.1", "5.0"];

    public static AzureDevOpsEndpoints Services(string organization, AzureDevOpsServicesEndpoints? roots = null)
    {
        roots ??= AzureDevOpsServicesEndpoints.Cloud;
        return new(false, roots.Core(organization), roots.Graph(organization), "7.1");
    }

    public static AzureDevOpsEndpoints Server(string serverRoot, string collection, string api)
    {
        var root = $"{serverRoot.TrimEnd('/')}/{Uri.EscapeDataString(collection)}";
        return new(true, root, root, api);
    }

    public string UsersUrl(string? continuation) => IsServer
        ? $"{Root}/_apis/identities?searchFilter=General&filterValue=&queryMembership=None&api-version={Api}"
        : WithToken($"{_graphRoot}/_apis/graph/users?api-version=7.1-preview.1", continuation);

    public string GroupsUrl(string? continuation) => IsServer
        ? $"{Root}/_apis/identities?searchFilter=General&filterValue=&queryMembership=None&api-version={Api}"
        : WithToken($"{_graphRoot}/_apis/graph/groups?api-version=7.1-preview.1", continuation);

    public string MembershipsUrl(string groupDescriptor, string? continuation) => IsServer
        ? $"{Root}/_apis/identities?descriptors={Uri.EscapeDataString(groupDescriptor)}&queryMembership=Direct&api-version={Api}"
        : WithToken($"{_graphRoot}/_apis/graph/Memberships/{Uri.EscapeDataString(groupDescriptor)}" +
            "?direction=down&depth=1&api-version=7.1-preview.1", continuation);

    private static string WithToken(string url, string? continuation) =>
        string.IsNullOrEmpty(continuation) ? url : url + "&continuationToken=" + Uri.EscapeDataString(continuation);

    // Rewrites an Identities API response into Graph-shaped users (identity descriptor doubles as
    // legacyDescriptor because Server ACEs reference that descriptor directly).
    public JsonDocument NormalizeUsers(JsonDocument source) => IsServer
        ? Rewrite(source, containers: false, identity => new JsonObject
        {
            ["descriptor"] = Text(identity, "descriptor"),
            ["displayName"] = DisplayName(identity),
            ["mailAddress"] = Property(identity, "Mail"),
            ["legacyDescriptor"] = Text(identity, "descriptor")
        }) : source;

    public JsonDocument NormalizeGroups(JsonDocument source) => IsServer
        ? Rewrite(source, containers: true, identity => new JsonObject
        {
            ["descriptor"] = Text(identity, "descriptor"),
            ["displayName"] = DisplayName(identity),
            ["legacyDescriptor"] = Text(identity, "descriptor")
        }) : source;

    public JsonDocument NormalizeMemberships(JsonDocument source, string groupDescriptor)
    {
        if (!IsServer) return source;
        var edges = new JsonArray();
        if (source.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            foreach (var identity in value.EnumerateArray())
                if (string.Equals(Text(identity, "descriptor"), groupDescriptor, StringComparison.Ordinal) &&
                    identity.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                    foreach (var member in members.EnumerateArray())
                        if (member.ValueKind == JsonValueKind.String)
                            edges.Add(new JsonObject
                            {
                                ["containerDescriptor"] = groupDescriptor,
                                ["memberDescriptor"] = member.GetString()
                            });
        return JsonDocument.Parse(new JsonObject { ["value"] = edges }.ToJsonString());
    }

    private static JsonDocument Rewrite(JsonDocument source, bool containers, Func<JsonElement, JsonObject> map)
    {
        var result = new JsonArray();
        if (!source.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new DiscoveryProviderException("UnexpectedResponse");
        foreach (var identity in value.EnumerateArray())
        {
            var isContainer = identity.TryGetProperty("isContainer", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (isContainer == containers && !string.IsNullOrWhiteSpace(Text(identity, "descriptor")))
                result.Add(map(identity));
        }
        return JsonDocument.Parse(new JsonObject { ["value"] = result }.ToJsonString());
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? DisplayName(JsonElement identity) =>
        Text(identity, "customDisplayName") ?? Text(identity, "providerDisplayName") ?? Text(identity, "descriptor");

    // Identities properties arrive as { "Mail": { "$type": "...", "$value": "x@y" } }.
    private static string? Property(JsonElement identity, string name) =>
        identity.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object &&
        props.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty("$value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Self-hosted servers reject versions newer than they know (HTTP 400), so probe downwards.
    public static async Task<string> DetectServerApiVersionAsync(
        HttpClient client, string collectionRoot, string token, CancellationToken ct)
    {
        foreach (var version in ServerApiVersions)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{collectionRoot}/_apis/projects?$top=1&api-version={version}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new DiscoveryProviderException("Unauthorized");
                if (response.StatusCode == HttpStatusCode.Forbidden) throw new DiscoveryProviderException("InsufficientScope");
                if (response.IsSuccessStatusCode) return version;
                if (response.StatusCode != HttpStatusCode.BadRequest) throw new DiscoveryProviderException("ProviderUnavailable");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DiscoveryProviderException("ProviderTimeout");
            }
            catch (HttpRequestException)
            {
                throw new DiscoveryProviderException("ProviderUnavailable");
            }
        }
        throw new DiscoveryProviderException("UnsupportedServerVersion");
    }
}
