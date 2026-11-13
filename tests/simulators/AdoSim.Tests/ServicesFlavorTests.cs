using System.Net;
using System.Text.Json;
using AdoSim;
using Xunit;

namespace AdoSim.Tests;

// The Azure DevOps Services stand-in: same estate, Services URL shapes and Graph identities.
public sealed class ServicesFlavorTests : IAsyncLifetime
{
    private SimHost _sim = null!;
    private SimHost _server = null!;

    public async Task InitializeAsync()
    {
        _sim = await SimHost.StartAsync("orion", flavor: SimFlavors.Services);
        _server = await SimHost.StartAsync("default");
    }

    public async Task DisposeAsync()
    {
        await _sim.DisposeAsync();
        await _server.DisposeAsync();
    }

    private async Task<List<JsonElement>> GraphAsync(string path)
    {
        var all = new List<JsonElement>();
        string? token = null;
        do
        {
            using var response = await _sim.Http.GetAsync(_sim.GraphRoot + path + "?api-version=7.1-preview.1" + (token is null ? "" : "&continuationToken=" + token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            all.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()));
            token = response.Headers.TryGetValues("X-MS-ContinuationToken", out var values) ? values.First() : null;
        } while (token is not null);
        return all;
    }

    [Fact]
    public async Task Core_rest_and_git_live_under_the_organization_without_tfs()
    {
        var projects = await _sim.JsonAsync("/_apis/projects?$top=100", "7.1");
        Assert.Equal((await _sim.ExpectedAsync()).GetProperty("projects").GetInt32(), projects.GetProperty("value").GetArrayLength());

        // the Server prefix does not exist on this flavour, and the Server estate has no Graph
        using var withTfs = await _sim.Http.GetAsync(_sim.Origin + "/tfs/DefaultCollection/_apis/projects?api-version=7.1");
        Assert.Equal(HttpStatusCode.NotFound, withTfs.StatusCode);
        using var serverGraph = await _server.Http.GetAsync(_server.Origin + "/vssps/DefaultCollection/_apis/graph/users?api-version=7.1-preview.1");
        Assert.Equal(HttpStatusCode.NotFound, serverGraph.StatusCode);
        Assert.False(string.IsNullOrEmpty((await _sim.JsonAsync("/_apis/connectionData", "7.1")).GetProperty("deploymentType").GetString()));

        var clone = await _sim.CloneAsync("Orion Commerce", "orion-api");
        Assert.True(File.Exists(Path.Combine(clone, "azure-pipelines.yml")));
        var remoteUrl = (await _sim.JsonAsync($"/{SimHost.Esc("Orion Commerce")}/_apis/git/repositories/orion-api", "7.1")).Str("remoteUrl");
        Assert.StartsWith(_sim.Origin + "/DefaultCollection/", remoteUrl);
    }

    [Fact]
    public async Task Everything_requires_the_pat_and_newer_api_versions_are_refused()
    {
        using var open = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await open.GetAsync(_sim.Root + "/_apis/projects?api-version=7.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await open.GetAsync(_sim.GraphRoot + "/_apis/graph/users?api-version=7.1-preview.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await open.GetAsync(_sim.Origin + "/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await open.GetAsync(_sim.Origin + "/_sim/expected")).StatusCode);
        using var tooNew = await _sim.GetAsync("/_apis/projects", "7.2");
        Assert.Equal(HttpStatusCode.BadRequest, tooNew.StatusCode);
        using var wrongPat = new HttpClient();
        wrongPat.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String("x:wrong"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongPat.GetAsync(_sim.Root + "/_apis/projects?api-version=7.1")).StatusCode);
    }

    [Fact]
    public async Task Graph_pages_users_groups_and_direct_memberships_with_continuation_tokens()
    {
        var expected = await _sim.ExpectedAsync();
        var users = await GraphAsync("/_apis/graph/users");
        var groups = await GraphAsync("/_apis/graph/groups");
        Assert.Equal(expected.GetProperty("users").GetInt32(), users.Count);
        Assert.Equal(expected.GetProperty("groups").GetInt32(), groups.Count);
        Assert.True(users.Count > 3, "the page size of 3 must have forced continuation");

        Assert.All(users, u =>
        {
            Assert.StartsWith("aad.", u.Str("descriptor"));
            Assert.Equal("user", u.Str("subjectKind"));
            Assert.NotEqual(u.Str("descriptor"), u.Str("legacyDescriptor"));
        });
        Assert.All(groups, g => Assert.StartsWith("vssgp.", g.Str("descriptor")));
        Assert.Equal(users.Count, users.Select(u => u.Str("descriptor")).Distinct().Count());

        var edges = 0;
        foreach (var group in groups)
        {
            var members = await GraphAsync($"/_apis/graph/Memberships/{Uri.EscapeDataString(group.Str("descriptor"))}");
            Assert.All(members, m => Assert.Equal(group.Str("descriptor"), m.Str("containerDescriptor")));
            edges += members.Count;
        }
        Assert.Equal(expected.GetProperty("memberships").GetInt32(), edges);
        using var unknown = await _sim.Http.GetAsync(_sim.GraphRoot + "/_apis/graph/Memberships/vssgp.unknown?api-version=7.1-preview.1");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Access_control_entries_reference_the_legacy_descriptors_that_graph_publishes()
    {
        var groups = await GraphAsync("/_apis/graph/groups");
        var users = await GraphAsync("/_apis/graph/users");
        var known = groups.Concat(users).Select(e => e.Str("legacyDescriptor")).ToHashSet();
        var project = (await _sim.JsonAsync("/_apis/projects?$top=100", "7.1")).GetProperty("value").EnumerateArray()
            .First(p => p.Str("name") == "Orion Commerce");
        var repos = (await _sim.JsonAsync($"/{project.Str("id")}/_apis/git/repositories", "7.1")).GetProperty("value").EnumerateArray().ToList();
        var resolved = 0;
        foreach (var repo in repos)
        {
            var token = Uri.EscapeDataString($"repoV2/{project.Str("id")}/{repo.Str("id")}");
            var acl = await _sim.JsonAsync($"/_apis/accesscontrollists/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87?token={token}", "7.1");
            foreach (var list in acl.GetProperty("value").EnumerateArray())
                foreach (var ace in list.GetProperty("acesDictionary").EnumerateObject())
                    if (known.Contains(ace.Name)) resolved++;
        }
        Assert.True(resolved > 0, "at least one ACE must resolve through a Graph legacyDescriptor");
    }
}
