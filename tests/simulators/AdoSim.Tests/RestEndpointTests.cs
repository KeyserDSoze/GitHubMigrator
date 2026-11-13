using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoSim.Tests;

[Collection("orion")]
public sealed class RestEndpointTests(OrionSim sim)
{
    private SimHost Sim => sim.Host;
    private const string C = "/Orion%20Commerce";

    public static IEnumerable<object[]> ProtectedPaths() => new[]
    {
        $"{C}/_apis/pipelines", $"{C}/_apis/pipelines/2001", $"{C}/_apis/pipelines/2001/runs", $"{C}/_apis/pipelines/2001/runs/5001",
        $"{C}/_apis/build/definitions", $"{C}/_apis/build/definitions/2006", $"{C}/_apis/serviceendpoint/endpoints",
        $"{C}/_apis/distributedtask/variablegroups", $"{C}/_apis/distributedtask/variablegroups/104",
        $"{C}/_apis/distributedtask/environments", $"{C}/_apis/distributedtask/environments/3",
        $"{C}/_apis/pipelines/checks/configurations?resourceType=environment&resourceId=3", $"{C}/_apis/policy/configurations",
        "/_apis/hooks/subscriptions", $"{C}/_apis/git/repositories/orion-api/pullrequests?searchCriteria.status=all",
        $"{C}/_apis/git/repositories/orion-api/pullrequests/1001", $"{C}/_apis/git/repositories/orion-api/pullrequests/1001/threads",
        $"{C}/_apis/git/repositories/orion-api/items?path=/README.md", $"{C}/_apis/git/repositories/orion-api/commits",
        $"{C}/_apis/git/repositories/orion-api/stats/branches", $"{C}/_apis/git/repositories/orion-api"
    }.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(ProtectedPaths))]
    public async Task Every_endpoint_requires_the_pat(string path)
    {
        var url = Sim.Root + path + (path.Contains('?') ? "&" : "?") + "api-version=7.0";
        using var anonymous = new HttpClient();
        using (var response = await anonymous.GetAsync(url)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var wrong = new HttpClient();
        wrong.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":wrong")));
        using (var response = await wrong.GetAsync(url)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var ok = await Sim.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedPaths))]
    public async Task Newer_api_versions_are_rejected_like_an_older_server(string path)
    {
        using var tooNew = await Sim.GetAsync(path, "7.1");
        Assert.Equal(HttpStatusCode.BadRequest, tooNew.StatusCode);
        Assert.Contains("VssInvalidPreviewVersionException", await tooNew.Content.ReadAsStringAsync());
        using var older = await Sim.GetAsync(path, "6.0");
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);
    }

    [Fact]
    public async Task Pipelines_list_shows_folder_revision_and_both_kinds()
    {
        var list = await Sim.JsonAsync($"{C}/_apis/pipelines");
        Assert.Equal(7, list.GetProperty("count").GetInt32());
        var items = list.Items();
        Assert.Equal(7, items.Length);
        Assert.All(items, p =>
        {
            Assert.True(p.GetProperty("id").GetInt32() > 0);
            Assert.False(string.IsNullOrEmpty(p.Str("name")));
            Assert.Equal(1, p.GetProperty("revision").GetInt32());
            Assert.StartsWith("\\", p.Str("folder"));
            Assert.Contains("/_apis/pipelines/", p.Str("url"));
        });
        Assert.Equal("\\Orion\\API", items.Single(p => p.GetProperty("id").GetInt32() == 2001).Str("folder"));
        Assert.Equal(items.Select(p => p.Str("name")).Order(StringComparer.OrdinalIgnoreCase), items.Select(p => p.Str("name")));
        Assert.Equal(3, (await Sim.JsonAsync("/Orion%20Platform/_apis/pipelines")).GetProperty("count").GetInt32());
        Assert.Equal(0, (await Sim.JsonAsync("/Orion%20Sandbox/_apis/pipelines?$top=0")).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Pipelines_list_pages_with_a_continuation_header_and_orders()
    {
        using var first = await Sim.GetAsync($"{C}/_apis/pipelines?$top=3&orderBy=id asc");
        Assert.Equal("3", first.Headers.GetValues("X-MS-ContinuationToken").Single());
        var ids = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.Items().Select(p => p.GetProperty("id").GetInt32()).ToList();
        using var second = await Sim.GetAsync($"{C}/_apis/pipelines?$top=3&orderBy=id asc&continuationToken=3");
        ids.AddRange(JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.Items().Select(p => p.GetProperty("id").GetInt32()));
        Assert.Equal([2001, 2002, 2003, 2004, 2005, 2006], ids);
        using var last = await Sim.GetAsync($"{C}/_apis/pipelines?$top=3&orderBy=id asc&continuationToken=6");
        Assert.False(last.Headers.Contains("X-MS-ContinuationToken"));
    }

    [Fact]
    public async Task Pipeline_detail_distinguishes_yaml_from_designer_pipelines()
    {
        var yaml = await Sim.JsonAsync($"{C}/_apis/pipelines/2001");
        Assert.Equal("yaml", yaml.GetProperty("configuration").Str("type"));
        Assert.Equal("/azure-pipelines.yml", yaml.GetProperty("configuration").Str("path"));
        var repository = yaml.GetProperty("configuration").GetProperty("repository");
        Assert.Equal("azureReposGit", repository.Str("type"));
        var repos = await Sim.JsonAsync($"{C}/_apis/git/repositories");
        Assert.Equal(repos.Items().Single(r => r.Str("name") == "orion-api").Str("id"), repository.Str("id"));
        Assert.Equal("\\Orion\\API", yaml.Str("folder"));

        var classic = await Sim.JsonAsync($"{C}/_apis/pipelines/2006");
        Assert.Equal("designerJson", classic.GetProperty("configuration").Str("type"));
        Assert.False(classic.GetProperty("configuration").TryGetProperty("path", out _));

        using var missing = await Sim.GetAsync($"{C}/_apis/pipelines/9999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var wrongProject = await Sim.GetAsync("/Orion%20Platform/_apis/pipelines/2001");
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
    }

    [Fact]
    public async Task Runs_list_is_newest_first_and_run_detail_carries_the_source_version()
    {
        var list = await Sim.JsonAsync($"{C}/_apis/pipelines/2001/runs");
        var runs = list.Items();
        Assert.Equal(5, list.GetProperty("count").GetInt32());
        Assert.Equal([5005, 5004, 5003, 5002, 5001], runs.Select(r => r.GetProperty("id").GetInt32()));
        Assert.Equal(new[] { "succeeded", "failed", "canceled" }.Order(), runs.Select(r => r.Str("result")).Distinct().Order());
        Assert.All(runs, r => Assert.Equal("completed", r.Str("state")));

        var detail = await Sim.JsonAsync($"{C}/_apis/pipelines/2001/runs/5001");
        var self = detail.GetProperty("resources").GetProperty("repositories").GetProperty("self");
        Assert.Equal("refs/heads/main", self.Str("refName"));
        var commits = await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/commits?searchCriteria.$top=10");
        // version "main~3" is the fourth commit from the tip
        Assert.Equal(commits.Items()[3].Str("commitId"), self.Str("version"));
        Assert.True(DateTimeOffset.Parse(detail.Str("finishedDate")) > DateTimeOffset.Parse(detail.Str("createdDate")));

        using var wrongRun = await Sim.GetAsync($"{C}/_apis/pipelines/2001/runs/5006");
        Assert.Equal(HttpStatusCode.NotFound, wrongRun.StatusCode);
    }

    [Fact]
    public async Task Build_definitions_expose_yaml_and_classic_processes()
    {
        var list = await Sim.JsonAsync($"{C}/_apis/build/definitions");
        Assert.Equal(7, list.GetProperty("count").GetInt32());
        Assert.Equal(2, (await Sim.JsonAsync($"{C}/_apis/build/definitions?name=orion-api*")).GetProperty("count").GetInt32());
        Assert.Single((await Sim.JsonAsync($"{C}/_apis/build/definitions?definitionIds=2006")).Items());

        var yaml = await Sim.JsonAsync($"{C}/_apis/build/definitions/2001");
        Assert.Equal(2, yaml.GetProperty("process").GetProperty("type").GetInt32());
        Assert.Equal("azure-pipelines.yml", yaml.GetProperty("process").Str("yamlFilename"));
        Assert.Equal([101, 102, 104], yaml.GetProperty("variableGroups").EnumerateArray().Select(g => g.GetProperty("id").GetInt32()));
        Assert.True(yaml.GetProperty("variables").GetProperty("SLACK_WEBHOOK").GetProperty("isSecret").GetBoolean());
        Assert.False(yaml.GetProperty("variables").GetProperty("SLACK_WEBHOOK").TryGetProperty("value", out _));
        Assert.Equal("true", yaml.GetProperty("variables").GetProperty("ENABLE_SMOKE_TESTS").Str("value"));
        Assert.Equal("TfsGit", yaml.GetProperty("repository").Str("type"));

        var classic = await Sim.JsonAsync($"{C}/_apis/build/definitions/2006");
        Assert.Equal(1, classic.GetProperty("process").GetProperty("type").GetInt32());
        var steps = classic.GetProperty("process").GetProperty("phases").EnumerateArray().Single().GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(7, steps.Length);
        Assert.Equal("71a9a2d3-a98a-4caa-96ab-affca411ecda", steps[2].GetProperty("task").Str("id"));
        Assert.Equal("Build solution", steps[2].Str("displayName"));
        Assert.Equal("$(BuildConfiguration)", steps[2].GetProperty("inputs").Str("configuration"));
        Assert.Equal("Windows Agents", classic.GetProperty("queue").Str("name"));
        Assert.Equal("refs/heads/master", classic.GetProperty("repository").Str("defaultBranch"));
        Assert.Equal(["continuousIntegration", "schedule"], classic.GetProperty("triggers").EnumerateArray().Select(t => t.Str("triggerType")));
        Assert.True(classic.GetProperty("variables").GetProperty("DeployPassword").GetProperty("isSecret").GetBoolean());
        Assert.False(classic.GetProperty("variables").GetProperty("DeployPassword").TryGetProperty("value", out _));
        Assert.DoesNotContain("process.yamlFilename", classic.GetRawText());
    }

    [Fact]
    public async Task Service_connections_are_listed_filtered_scoped_and_never_leak_credentials()
    {
        var all = await Sim.JsonAsync($"{C}/_apis/serviceendpoint/endpoints");
        var endpoints = all.Items();
        Assert.True(endpoints.Length >= 9);
        Assert.Equal(all.GetProperty("count").GetInt32(), endpoints.Length);
        var types = endpoints.Select(e => e.Str("type")).ToHashSet();
        Assert.Superset(new HashSet<string> { "azurerm", "github", "dockerregistry", "ssh", "sonarqube", "npm", "externalnugetfeed", "generic", "kubernetes", "externalnpmregistry" }, types);
        Assert.All(endpoints, e =>
        {
            Assert.True(Guid.TryParse(e.Str("id"), out _));
            Assert.False(string.IsNullOrEmpty(e.GetProperty("authorization").Str("scheme")));
            Assert.Equal("library", e.Str("owner"));
            Assert.False(string.IsNullOrEmpty(e.GetProperty("createdBy").Str("displayName")));
            Assert.True(e.GetProperty("isReady").ValueKind is JsonValueKind.True or JsonValueKind.False);
            Assert.True(e.GetProperty("isShared").ValueKind is JsonValueKind.True or JsonValueKind.False);
        });
        Assert.Contains(endpoints, e => e.Str("name") == "orion-webhook-deployer" && !e.GetProperty("isReady").GetBoolean());
        Assert.Contains(endpoints, e => e.GetProperty("isShared").GetBoolean());
        Assert.DoesNotContain(endpoints, e => e.Str("name") == "orion-azure-sandbox");

        var azure = (await Sim.JsonAsync($"{C}/_apis/serviceendpoint/endpoints?type=azurerm")).Items();
        Assert.Equal(["orion-azure-dev", "orion-azure-prod"], azure.Select(e => e.Str("name")).Order());
        var named = (await Sim.JsonAsync($"{C}/_apis/serviceendpoint/endpoints?endpointNames=orion-acr,orion-github")).Items();
        Assert.Equal(["orion-acr", "orion-github"], named.Select(e => e.Str("name")).Order());
        Assert.Equal("UsernamePassword", named.Single(e => e.Str("name") == "orion-acr").GetProperty("authorization").Str("scheme"));
        var sandbox = (await Sim.JsonAsync("/Orion%20Sandbox/_apis/serviceendpoint/endpoints")).Items();
        Assert.Equal(["orion-azure-dev", "orion-azure-sandbox", "orion-github"], sandbox.Select(e => e.Str("name")).Order());

        // Secret parameters are listed as null, exactly like a real server; no value is ever present.
        var raw = all.GetRawText();
        Assert.DoesNotMatch(new Regex("\"(password|accessToken|apitoken|serviceprincipalkey|kubeconfig)\"\\s*:\\s*\"[^\"]", RegexOptions.IgnoreCase), raw);
        Assert.Contains("\"accessToken\":null", raw);

        var one = await Sim.JsonAsync($"{C}/_apis/serviceendpoint/endpoints/{named[0].Str("id")}");
        Assert.Equal(named[0].Str("name"), one.Str("name"));
    }

    [Fact]
    public async Task Variable_groups_cover_plain_and_key_vault_backed_groups_without_secret_values()
    {
        var list = await Sim.JsonAsync($"{C}/_apis/distributedtask/variablegroups");
        var groups = list.Items();
        Assert.Equal([104, 103, 102, 101], groups.Select(g => g.GetProperty("id").GetInt32()));   // IdDescending is the default
        Assert.Equal([101, 102, 103, 104], (await Sim.JsonAsync($"{C}/_apis/distributedtask/variablegroups?queryOrder=IdAscending")).Items().Select(g => g.GetProperty("id").GetInt32()));
        using (var firstPage = await Sim.GetAsync($"{C}/_apis/distributedtask/variablegroups?$top=2&queryOrder=IdAscending"))
        {
            Assert.Equal("102", firstPage.Headers.GetValues("X-MS-ContinuationToken").Single());
            using var secondPage = await Sim.GetAsync($"{C}/_apis/distributedtask/variablegroups?$top=2&queryOrder=IdAscending&continuationToken=102");
            Assert.Equal([103, 104], JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync()).RootElement.Items().Select(g => g.GetProperty("id").GetInt32()));
            Assert.False(secondPage.Headers.Contains("X-MS-ContinuationToken"));
        }
        Assert.Equal(["Orion Platform"], (await Sim.JsonAsync("/Orion%20Platform/_apis/distributedtask/variablegroups?groupName=orion-i*")).Items()
            .SelectMany(g => g.GetProperty("variableGroupProjectReferences").EnumerateArray().Select(r => r.GetProperty("projectReference").Str("name"))));
        Assert.Single((await Sim.JsonAsync($"{C}/_apis/distributedtask/variablegroups?ids=102")).Items());

        var vault = await Sim.JsonAsync($"{C}/_apis/distributedtask/variablegroups/104");
        Assert.Equal("AzureKeyVault", vault.Str("type"));
        Assert.Equal("kv-orion-prod", vault.GetProperty("providerData").Str("vault"));
        var endpoints = (await Sim.JsonAsync($"{C}/_apis/serviceendpoint/endpoints")).Items();
        Assert.Equal(endpoints.Single(e => e.Str("name") == "orion-azure-prod").Str("id"), vault.GetProperty("providerData").Str("serviceEndpointId"));
        Assert.All(vault.GetProperty("variables").EnumerateObject(), v => Assert.True(v.Value.GetProperty("isSecret").GetBoolean()));

        var plain = await Sim.JsonAsync($"{C}/_apis/distributedtask/variablegroups/102");
        Assert.Equal("Vsts", plain.Str("type"));
        Assert.True(plain.GetProperty("variables").GetProperty("DB_CONNECTION_STRING").GetProperty("isSecret").GetBoolean());
        Assert.False(plain.GetProperty("variables").GetProperty("DB_CONNECTION_STRING").TryGetProperty("value", out _));
        Assert.Equal("https://payments.example.test/v2", plain.GetProperty("variables").GetProperty("PAYMENT_API_URL").Str("value"));

        using var hidden = await Sim.GetAsync("/Orion%20Sandbox/_apis/distributedtask/variablegroups/102");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task Environments_expose_approval_and_branch_control_checks()
    {
        var environments = (await Sim.JsonAsync($"{C}/_apis/distributedtask/environments")).Items();
        Assert.Equal(["dev", "staging", "prod"], environments.Select(e => e.Str("name")));
        var prod = await Sim.JsonAsync($"{C}/_apis/distributedtask/environments/3");
        Assert.Equal("prod", prod.Str("name"));
        Assert.Equal(["infra-dev", "infra-prod"], (await Sim.JsonAsync("/Orion%20Platform/_apis/distributedtask/environments")).Items().Select(e => e.Str("name")));
        using var foreign = await Sim.GetAsync($"{C}/_apis/distributedtask/environments/4");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var bare = (await Sim.JsonAsync($"{C}/_apis/pipelines/checks/configurations?resourceType=environment&resourceId=3", "7.0-preview.1")).Items();
        Assert.Equal(2, bare.Length);
        Assert.All(bare, c => Assert.False(c.TryGetProperty("settings", out _)));   // settings need $expand=settings, as on a real server
        var checks = (await Sim.JsonAsync($"{C}/_apis/pipelines/checks/configurations?resourceType=environment&resourceId=3&$expand=settings", "7.0-preview.1")).Items();
        Assert.Equal(["Approval", "Task Check"], checks.Select(c => c.GetProperty("type").Str("name")));
        var approval = checks[0].GetProperty("settings");
        Assert.Equal("[Orion Commerce]\\Release Managers", approval.GetProperty("approvers")[0].Str("displayName"));
        Assert.Equal("refs/heads/main", checks[1].GetProperty("settings").GetProperty("inputs").Str("allowedBranches"));
        Assert.All(checks, c => { Assert.Equal("environment", c.GetProperty("resource").Str("type")); Assert.Equal("3", c.GetProperty("resource").Str("id")); });
        Assert.Empty((await Sim.JsonAsync($"{C}/_apis/pipelines/checks/configurations?resourceType=environment&resourceId=1")).Items());
        Assert.Empty((await Sim.JsonAsync($"{C}/_apis/pipelines/checks/configurations?resourceType=queue&resourceId=3")).Items());
    }

    [Fact]
    public async Task Policy_configurations_cover_branch_policies_and_filter_by_repository_ref_and_type()
    {
        var all = (await Sim.JsonAsync($"{C}/_apis/policy/configurations")).Items();
        Assert.Equal(10, all.Length);
        var typeNames = all.Select(p => p.GetProperty("type").Str("displayName")).ToHashSet();
        Assert.Superset(new HashSet<string> { "Minimum number of reviewers", "Build", "Work item linking", "Comment requirements", "Required reviewers" }, typeNames);

        var api = (await Sim.JsonAsync($"{C}/_apis/git/repositories")).Items().Single(r => r.Str("name") == "orion-api").Str("id");
        var onApi = (await Sim.JsonAsync($"{C}/_apis/policy/configurations?repositoryId={api}&refName=refs/heads/main")).Items();
        Assert.Equal(5, onApi.Length);
        var build = onApi.Single(p => p.GetProperty("type").Str("displayName") == "Build");
        Assert.Equal(2001, build.GetProperty("settings").GetProperty("buildDefinitionId").GetInt32());
        Assert.Equal(api, build.GetProperty("settings").GetProperty("scope")[0].Str("repositoryId"));
        var min = onApi.Single(p => p.GetProperty("type").Str("displayName") == "Minimum number of reviewers");
        Assert.Equal(2, min.GetProperty("settings").GetProperty("minimumApproverCount").GetInt32());
        Assert.True(min.GetProperty("isEnabled").GetBoolean());
        Assert.False(all.Single(p => p.GetProperty("id").GetInt32() == 10).GetProperty("isEnabled").GetBoolean());
        Assert.False(all.Single(p => p.GetProperty("id").GetInt32() == 8).GetProperty("isBlocking").GetBoolean());
        var byType = (await Sim.JsonAsync($"{C}/_apis/policy/configurations?policyType=0609b952-1397-4640-95ec-e00a01b2c241")).Items();
        Assert.Equal(2, byType.Length);
    }

    [Fact]
    public async Task Service_hooks_are_listed_filtered_and_mask_secret_targets()
    {
        var list = await Sim.JsonAsync("/_apis/hooks/subscriptions");
        var hooks = list.Items();
        Assert.True(hooks.Length >= 3);
        Assert.Equal(list.GetProperty("count").GetInt32(), hooks.Length);
        Assert.Superset(new HashSet<string> { "git.push", "build.complete", "git.pullrequest.created" }, hooks.Select(h => h.Str("eventType")).ToHashSet());
        Assert.Contains(hooks, h => h.Str("consumerId") == "slack" && h.Str("consumerActionId") == "postMessageToChannel");
        Assert.Equal(2, (await Sim.JsonAsync("/_apis/hooks/subscriptions?eventType=git.push")).GetProperty("count").GetInt32());
        Assert.Equal(1, (await Sim.JsonAsync("/_apis/hooks/subscriptions?consumerId=slack")).GetProperty("count").GetInt32());
        Assert.Equal(0, (await Sim.JsonAsync("/_apis/hooks/subscriptions?publisherId=other")).GetProperty("count").GetInt32());
        Assert.Equal("********", hooks.Single(h => h.Str("consumerId") == "slack").GetProperty("consumerInputs").Str("url"));
        Assert.Equal("disabledByUser", hooks.Single(h => h.Str("status") != "enabled").Str("status"));
        var push = hooks.First(h => h.Str("eventType") == "git.push");
        Assert.Equal("refs/heads/main", push.GetProperty("publisherInputs").Str("branch"));
        Assert.Equal(push.Str("id"), (await Sim.JsonAsync("/_apis/hooks/subscriptions/" + push.Str("id"))).Str("id"));
    }

    [Fact]
    public async Task Pull_requests_filter_by_status_and_point_at_existing_refs()
    {
        var all = (await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests?searchCriteria.status=all")).Items();
        Assert.Equal([1001, 1002, 1003, 1004], all.Select(p => p.GetProperty("pullRequestId").GetInt32()));
        Assert.Equal([1001, 1004], (await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests")).Items().Select(p => p.GetProperty("pullRequestId").GetInt32()));
        Assert.Equal([1002], (await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests?searchCriteria.status=completed")).Items().Select(p => p.GetProperty("pullRequestId").GetInt32()));
        Assert.Equal([1003], (await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests?searchCriteria.status=abandoned")).Items().Select(p => p.GetProperty("pullRequestId").GetInt32()));
        Assert.Equal(2, (await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests?searchCriteria.status=all&$top=2&$skip=1")).GetProperty("count").GetInt32());
        using var bad = await Sim.GetAsync($"{C}/_apis/git/repositories/orion-api/pullrequests?searchCriteria.status=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var refs = await Sim.RefsAsync("Orion Commerce", "orion-api");
        foreach (var pr in all)
        {
            Assert.Contains(pr.Str("sourceRefName"), refs.Keys);
            Assert.Contains(pr.Str("targetRefName"), refs.Keys);
            Assert.Equal(refs[pr.Str("sourceRefName")], pr.GetProperty("lastMergeSourceCommit").Str("commitId"));
        }
        var detail = await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests/1002");
        Assert.Equal("completed", detail.Str("status"));
        Assert.True(detail.TryGetProperty("closedDate", out _));
        Assert.Equal("Dave Neri", detail.GetProperty("createdBy").Str("displayName"));
        Assert.Equal(2, detail.GetProperty("reviewers").GetArrayLength());
        Assert.True(all.Single(p => p.GetProperty("pullRequestId").GetInt32() == 1004).GetProperty("isDraft").GetBoolean());
        Assert.Equal(0, (await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests/1002/threads")).GetProperty("count").GetInt32());
        Assert.Equal(["2005", "2007"], (await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-api/pullrequests/1002/workitems")).Items().Select(w => w.Str("id")));
        using var missing = await Sim.GetAsync($"{C}/_apis/git/repositories/orion-api/pullrequests/9");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Work_items_link_commits_and_pull_requests_of_the_estate()
    {
        var items = (await Sim.JsonAsync($"{C}/_apis/wit/workitems?ids=2004,2007,2012&$expand=Relations")).Items();
        Assert.Equal(3, items.Length);
        var pr = items.Single(i => i.GetProperty("id").GetInt32() == 2004).GetProperty("relations").EnumerateArray()
            .Select(r => r.Str("url")).Single(u => u.Contains("/Git/PullRequestId/"));
        Assert.EndsWith("%2f1001", pr);
        var repos = (await Sim.JsonAsync($"{C}/_apis/git/repositories")).Items();
        var api = repos.Single(r => r.Str("name") == "orion-api").Str("id");
        var commit = items.Single(i => i.GetProperty("id").GetInt32() == 2007).GetProperty("relations").EnumerateArray()
            .Select(r => r.Str("url")).Single(u => u.Contains("/Git/Commit/"));
        var refs = await Sim.RefsAsync("Orion Commerce", "orion-api");
        Assert.EndsWith($"%2f{api}%2f{refs["refs/heads/hotfix/payment-timeout"]}", commit);
        // an annotated tag must resolve to the commit, not to the tag object
        var platformRefs = await Sim.RefsAsync("Orion Platform", "orion-templates");
        var tagLink = items.Single(i => i.GetProperty("id").GetInt32() == 2012).GetProperty("relations").EnumerateArray().Select(r => r.Str("url")).Single(u => u.Contains("/Git/Commit/"));
        Assert.DoesNotContain(platformRefs["refs/tags/v3"], tagLink);
        Assert.EndsWith(platformRefs["refs/heads/main"], tagLink);
    }

    [Fact]
    public async Task Items_keeps_the_product_call_and_adds_formats_versions_listings_and_downloads()
    {
        var web = $"{C}/_apis/git/repositories/orion-web/items";
        var legacy = await Sim.JsonAsync($"{web}?path=/azure-pipelines.yml&includeContent=true&$format=json");
        Assert.Equal("/azure-pipelines.yml", legacy.Str("path"));
        Assert.StartsWith("# Orion web storefront", legacy.Str("content"));
        Assert.Equal("blob", legacy.Str("gitObjectType"));
        Assert.False(legacy.GetProperty("isFolder").GetBoolean());
        Assert.False(legacy.Str("content").Contains('\r'));
        // omitting the formatting parameters keeps returning the content
        Assert.Equal(legacy.Str("content"), (await Sim.JsonAsync($"{web}?path=/azure-pipelines.yml")).Str("content"));
        Assert.False((await Sim.JsonAsync($"{web}?path=/azure-pipelines.yml&includeContent=false")).TryGetProperty("content", out _));

        using var text = await Sim.GetAsync($"{web}?path=/azure-pipelines.yml&$format=text");
        Assert.StartsWith("text/plain", text.Content.Headers.ContentType!.ToString());
        Assert.Equal(legacy.Str("content"), await text.Content.ReadAsStringAsync());

        var blob = await Sim.JsonAsync($"{web}?path=/data/catalog-snapshot.bin&$format=json&includeContent=true");
        Assert.False(blob.TryGetProperty("content", out _));
        using var download = await Sim.GetAsync($"{web}?path=/data/catalog-snapshot.bin&download=true");
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(1_572_864, bytes.Length);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, bytes[0]);
        var png = await (await Sim.GetAsync($"{web}?path=/public/images/hero.png&download=true")).Content.ReadAsByteArrayAsync();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);

        var commit = (await Sim.JsonAsync($"{web}?path=/README.md")).Str("commitId");
        Assert.Equal(commit, (await Sim.JsonAsync($"{web}?path=/README.md&versionDescriptor.version=refs/heads/main")).Str("commitId"));
        var tagged = await Sim.JsonAsync($"{web}?path=/README.md&versionDescriptor.version=web-3.0.0&versionDescriptor.versionType=tag");
        Assert.Equal(commit, tagged.Str("commitId"));
        var byCommit = await Sim.JsonAsync($"{web}?path=/README.md&versionDescriptor.version={commit}&versionDescriptor.versionType=commit");
        Assert.Equal(commit, byCommit.Str("commitId"));
        using var unknownVersion = await Sim.GetAsync($"{web}?path=/README.md&versionDescriptor.version=nope&versionDescriptor.versionType=branch");
        Assert.Equal(HttpStatusCode.NotFound, unknownVersion.StatusCode);
        using var optionInjection = await Sim.GetAsync($"{web}?path=/README.md&versionDescriptor.version=--help&versionDescriptor.versionType=commit");
        Assert.Equal(HttpStatusCode.NotFound, optionInjection.StatusCode);

        var one = await Sim.JsonAsync($"{web}?path=/&recursionLevel=OneLevel");
        var top = one.Items();
        Assert.Equal("/", top[0].Str("path"));
        Assert.True(top[0].GetProperty("isFolder").GetBoolean());
        Assert.Equal(["/", "/.npmrc", "/README.md", "/azure-pipelines.yml", "/data", "/docs", "/package.json", "/public", "/src", "/tsconfig.json"], top.Select(i => i.Str("path")));
        Assert.All(top, i => { Assert.Equal(40, i.Str("objectId").Length); Assert.Equal(commit, i.Str("commitId")); });
        var full = (await Sim.JsonAsync($"{web}?scopePath=/src&recursionLevel=Full")).Items();
        Assert.Equal(["/src", "/src/App.tsx", "/src/index.tsx"], full.Select(i => i.Str("path")));
        Assert.Equal("tree", (await Sim.JsonAsync($"{web}?path=/src")).Str("gitObjectType"));
        Assert.Equal("tree", (await Sim.JsonAsync($"{web}?path=/docs/history&recursionLevel=None")).Str("gitObjectType"));
        Assert.Equal(40, (await Sim.JsonAsync($"{web}?path=/docs/history&recursionLevel=OneLevel")).GetProperty("count").GetInt32() - 1);

        using var missing = await Sim.GetAsync($"{web}?path=/nothing.txt");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var noPath = await Sim.GetAsync(web);
        Assert.Equal(HttpStatusCode.NotFound, noPath.StatusCode);
        using var folderAsText = await Sim.GetAsync($"{web}?path=/src&$format=text");
        Assert.Equal(HttpStatusCode.BadRequest, folderAsText.StatusCode);
        using var emptyRepo = await Sim.GetAsync($"{C}/_apis/git/repositories/orion-docs/items?path=/&recursionLevel=OneLevel");
        Assert.Equal(HttpStatusCode.NotFound, emptyRepo.StatusCode);
    }

    [Fact]
    public async Task Commits_list_detail_and_branch_statistics_describe_the_history()
    {
        var api = $"{C}/_apis/git/repositories/orion-api";
        var page = await Sim.JsonAsync($"{api}/commits?searchCriteria.$top=5");
        Assert.Equal(5, page.GetProperty("count").GetInt32());
        var commits = page.Items();
        Assert.Equal("Commit 14 of orion-api", commits[0].Str("comment"));
        Assert.Equal("Bob Bianchi", commits[0].GetProperty("author").Str("name"));
        Assert.Equal("bob@example.test", commits[0].GetProperty("author").Str("email"));
        Assert.True(DateTimeOffset.Parse(commits[0].GetProperty("author").Str("date")) > DateTimeOffset.Parse(commits[1].GetProperty("author").Str("date")));
        var skipped = (await Sim.JsonAsync($"{api}/commits?searchCriteria.$top=2&searchCriteria.$skip=3")).Items();
        Assert.Equal(commits[3].Str("commitId"), skipped[0].Str("commitId"));
        Assert.Equal(14, (await Sim.JsonAsync($"{api}/commits")).GetProperty("count").GetInt32());
        Assert.Equal(1, (await Sim.JsonAsync($"{api}/commits?searchCriteria.itemVersion.version=feature/loyalty/points&searchCriteria.itemPath=/azure-pipelines-experimental.yml")).GetProperty("count").GetInt32());
        Assert.Equal(3, (await Sim.JsonAsync($"{api}/commits?searchCriteria.author=Carol")).GetProperty("count").GetInt32());

        var detail = await Sim.JsonAsync($"{api}/commits/{commits[0].Str("commitId")}");
        Assert.Single(detail.GetProperty("parents").EnumerateArray());
        Assert.Equal(commits[1].Str("commitId"), detail.GetProperty("parents")[0].GetString());
        Assert.Equal(40, detail.Str("treeId").Length);
        using var missing = await Sim.GetAsync($"{api}/commits/0000000000000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var notHex = await Sim.GetAsync($"{api}/commits/--version");
        Assert.Equal(HttpStatusCode.NotFound, notHex.StatusCode);

        var stats = (await Sim.JsonAsync($"{api}/stats/branches")).Items();
        Assert.Equal(7, stats.Length);
        var main = stats.Single(s => s.Str("name") == "main");
        Assert.True(main.GetProperty("isBaseVersion").GetBoolean());
        Assert.Equal(0, main.GetProperty("aheadCount").GetInt32());
        var feature = stats.Single(s => s.Str("name") == "feature/checkout-v2");
        Assert.Equal(1, feature.GetProperty("aheadCount").GetInt32());
        Assert.Equal(0, feature.GetProperty("behindCount").GetInt32());
        Assert.Equal(40, feature.GetProperty("commit").Str("commitId").Length);
        var single = (await Sim.JsonAsync($"{api}/stats/branches?name=develop&baseVersionDescriptor.version=release/2024.1")).Items().Single();
        Assert.Equal(1, single.GetProperty("aheadCount").GetInt32());
        Assert.Equal(1, single.GetProperty("behindCount").GetInt32());
    }

    [Fact]
    public async Task Repository_detail_reports_default_branch_and_disabled_state()
    {
        var repo = await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-legacy");
        Assert.Equal("refs/heads/master", repo.Str("defaultBranch"));
        Assert.Contains("/_git/orion-legacy", repo.Str("remoteUrl"));
        Assert.True((await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-archive")).GetProperty("isDisabled").GetBoolean());
        Assert.False((await Sim.JsonAsync($"{C}/_apis/git/repositories/orion-docs")).TryGetProperty("defaultBranch", out _));
        using var disabledRefs = await Sim.GetAsync($"{C}/_apis/git/repositories/orion-archive/refs");
        Assert.Equal(HttpStatusCode.Forbidden, disabledRefs.StatusCode);
    }
}
