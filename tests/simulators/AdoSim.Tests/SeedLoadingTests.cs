using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace AdoSim.Tests;

public sealed class SeedLoadingTests
{
    [Fact]
    public void Profiles_load_by_name_and_null_means_default()
    {
        var byNull = SeedSpec.Load(null);
        Assert.Equal(["Alpha Platform", "Beta Ünïcode", "Gamma Legacy", "Delta Mixed", "Epsilon Empty"], byNull.Projects.Select(p => p.Name));
        Assert.Equal(byNull.ComputeHash(), SeedSpec.Load("default").ComputeHash());
        Assert.Equal(byNull.ComputeHash(), SeedSpec.Load("  DEFAULT ").ComputeHash());
        Assert.Equal(byNull.ComputeHash(), SeedSpec.Load("").ComputeHash());
        var orion = SeedSpec.Load("orion");
        Assert.Equal(["Orion Commerce", "Orion Platform", "Orion Sandbox"], orion.Projects.Select(p => p.Name));
        Assert.NotEqual(byNull.ComputeHash(), orion.ComputeHash());
        Assert.Equal(orion.ComputeHash(), SeedSpec.Load("Orion").ComputeHash());
        Assert.Equal(64, orion.ComputeHash().Length);
    }

    [Fact]
    public void An_unknown_profile_or_file_is_an_error_that_names_the_built_ins()
    {
        var error = Assert.Throws<FileNotFoundException>(() => SeedSpec.Load("does-not-exist"));
        Assert.Contains("default", error.Message);
        Assert.Contains("orion", error.Message);
    }

    [Fact]
    public async Task A_seed_file_can_ship_its_own_files_folder_next_to_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "adosim-seedfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "files", "mine", ".azuredevops"));
        await File.WriteAllTextAsync(Path.Combine(dir, "files", "mine", ".azuredevops", "ci.yml"), "steps:\r\n- script: echo hi\r\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "files", "mine", "hello.txt"), "hello\n");
        var seedPath = Path.Combine(dir, "my-seed.json");
        await File.WriteAllTextAsync(seedPath, """
            { "collection": "DefaultCollection", "users": [], "groups": [],
              "projects": [ { "name": "Mine", "sourceControl": "Git", "repos": [
                { "name": "app", "commits": 2, "filesFrom": "mine", "files": { "inline.txt": "inline\n" },
                  "branches": ["feature/x"], "branchFiles": { "feature/x": { "only-here.txt": "branch\n" } } } ] } ] }
            """);
        try
        {
            await using var sim = await SimHost.StartAsync(seedPath);
            var yaml = await sim.JsonAsync("/Mine/_apis/git/repositories/app/items?path=/.azuredevops/ci.yml&$format=json&includeContent=true");
            Assert.Equal("steps:\n- script: echo hi\n", yaml.Str("content"));
            Assert.Equal("hello\n", (await sim.JsonAsync("/Mine/_apis/git/repositories/app/items?path=/hello.txt")).Str("content"));
            Assert.Equal("inline\n", (await sim.JsonAsync("/Mine/_apis/git/repositories/app/items?path=/inline.txt")).Str("content"));
            Assert.Equal("branch\n", (await sim.JsonAsync("/Mine/_apis/git/repositories/app/items?path=/only-here.txt&versionDescriptor.version=feature/x")).Str("content"));
            using var onMain = await sim.GetAsync("/Mine/_apis/git/repositories/app/items?path=/only-here.txt");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, onMain.StatusCode);
            var expected = await sim.ExpectedAsync();
            Assert.Equal(1, expected.GetProperty("gitRepositories").GetInt32());
            Assert.Equal(2, expected.GetProperty("refs").GetInt32());
        }
        finally { SimHost.DeleteDir(dir); }
    }

    [Fact]
    public void The_container_entry_point_reads_the_seed_and_bulk_environment_variables()
    {
        var env = new Dictionary<string, string>
        {
            ["ADO_SIM_SEED"] = "orion", ["ADO_SIM_BULK"] = "2,1,1", ["ADO_SIM_DATA"] = "/data", ["ADO_SIM_PAT"] = "other",
            ["ADO_SIM_MAX_API"] = "6.0", ["ADO_SIM_REFS_PAGE"] = "7", ["ASPNETCORE_URLS"] = "http://+:9"
        };
        var options = SimEntry.OptionsFrom(name => env.GetValueOrDefault(name));
        Assert.Equal(["Orion Commerce", "Orion Platform", "Orion Sandbox", "Bulk 001", "Bulk 002"], options.Seed.Projects.Select(p => p.Name).Take(5));
        Assert.Equal(5, options.Seed.Projects.Count);
        Assert.Equal(("/data", "other", "6.0", 7, "http://+:9"), (options.DataDir, options.Pat, options.MaxApiVersion, options.RefsPageSize, options.Urls));

        // docker compose passes empty strings when the variables are not set
        var defaults = SimEntry.OptionsFrom(name => name is "ADO_SIM_SEED" or "ADO_SIM_BULK" ? "" : null);
        Assert.Equal(SeedSpec.Load("default").ComputeHash(), defaults.Seed.ComputeHash());
        Assert.Equal(("sim-pat", "7.0", 1000, "http://0.0.0.0:8080"), (defaults.Pat, defaults.MaxApiVersion, defaults.RefsPageSize, defaults.Urls));
        Assert.Equal(SeedSpec.Load("default").ComputeHash(), SimEntry.OptionsFrom(name => name == "ADO_SIM_SEED" ? "default" : null).Seed.ComputeHash());
    }

    [Theory]
    [InlineData("3,2,1", 3, 2, 1, 3)]
    [InlineData(" 10 , 4 , 2 , 7 ", 10, 4, 2, 7)]
    [InlineData("5", 5, 1, 1, 3)]
    public void Bulk_settings_parse(string text, int projects, int repos, int pipelines, int commits) =>
        Assert.Equal(new SeedGenerate(projects, repos, pipelines, commits), SeedGenerate.Parse(text));

    [Theory]
    [InlineData("a,b")]
    [InlineData("1,2,3,4,5")]
    [InlineData("-1,2")]
    [InlineData(",,")]
    public void Invalid_bulk_settings_are_rejected(string text) =>
        Assert.Throws<ArgumentException>(() => SeedGenerate.Parse(text));

    [Fact]
    public void Generate_expands_deterministically_into_projects_repos_and_pipelines()
    {
        var a = SeedSpec.Load("default", "3,2,2");
        var b = SeedSpec.Load("default", "3,2,2");
        Assert.Equal(JsonSerializer.Serialize(a, SeedSpec.JsonOptions), JsonSerializer.Serialize(b, SeedSpec.JsonOptions));
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(SeedSpec.Load("default").ComputeHash(), a.ComputeHash());
        Assert.Null(a.Generate);
        Assert.Equal(SeedSpec.Load("default").Projects.Count + 3, a.Projects.Count);
        var bulk = a.Projects.Where(p => p.Name.StartsWith("Bulk ")).ToList();
        Assert.Equal(["Bulk 001", "Bulk 002", "Bulk 003"], bulk.Select(p => p.Name));
        Assert.Equal(["bulk-001-repo-01", "bulk-001-repo-02"], bulk[0].Repos!.Select(r => r.Name));
        Assert.Equal(4, bulk[0].Pipelines!.Count);
        Assert.All(bulk.SelectMany(p => p.Pipelines!), p => Assert.True(p.Id > SeedSpec.BulkPipelineIdBase));
        Assert.Equal(bulk.SelectMany(p => p.Pipelines!).Count(), bulk.SelectMany(p => p.Pipelines!).Select(p => p.Id).Distinct().Count());
        Assert.Equal(12, bulk.SelectMany(p => p.Pipelines!).Count());
        // a second Expand is a no-op
        Assert.Equal(a.Projects.Count, a.Expand().Projects.Count);
        foreach (var repo in bulk.SelectMany(p => p.Repos!))
            foreach (var yaml in repo.Files!.Values) Assert.NotNull(SeedYamlTests.Parse(yaml));
        // widths grow with the count, names stay sortable
        Assert.Contains(SeedSpec.Load("default", "1000,1,1").Projects, p => p.Name == "Bulk 1000");
        Assert.Contains(SeedSpec.Load("default", "1000,1,1").Projects, p => p.Name == "Bulk 0001");
    }

    [Fact]
    public async Task A_generate_block_in_the_seed_file_is_overridden_by_the_bulk_setting()
    {
        var path = Path.Combine(Path.GetTempPath(), "adosim-generate-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            { "collection": "DefaultCollection", "users": [], "groups": [], "projects": [],
              "generate": { "projects": 2, "reposPerProject": 1, "pipelinesPerRepo": 1, "commitsPerRepo": 4 } }
            """);
        try
        {
            var fromFile = SeedSpec.Load(path);
            Assert.Equal(2, fromFile.Projects.Count);
            Assert.Equal(4, fromFile.Projects[0].Repos![0].Commits);
            var overridden = SeedSpec.Load(path, "3,1,1");
            Assert.Equal(3, overridden.Projects.Count);
            Assert.Equal(3, overridden.Projects[0].Repos![0].Commits);
            Assert.Throws<ArgumentException>(() => SeedSpec.Load(path, "100000,1000,1"));
        }
        finally { File.Delete(path); }
    }
}

public sealed class BulkEstateTests
{
    [Fact]
    public async Task Bulk_estate_serves_generated_projects_repositories_and_pipelines()
    {
        await using var sim = await SimHost.StartAsync("default", "3,2,2,5");
        var expected = await sim.ExpectedAsync();
        Assert.Equal(5 + 3, expected.GetProperty("projects").GetInt32());
        Assert.Equal(9 + 6, expected.GetProperty("gitRepositories").GetInt32());
        Assert.Equal(1 + 12, expected.GetProperty("yamlPipelines").GetInt32());
        var projects = (await sim.JsonAsync("/_apis/projects?$top=100")).Items();
        Assert.Contains(projects, p => p.Str("name") == "Bulk 002");
        var pipelines = (await sim.JsonAsync("/Bulk%20002/_apis/pipelines")).Items();
        Assert.Equal(4, pipelines.Length);
        var detail = await sim.JsonAsync($"/Bulk%20002/_apis/pipelines/{pipelines[0].GetProperty("id").GetInt32()}");
        var path = detail.GetProperty("configuration").Str("path");
        var item = await sim.JsonAsync($"/Bulk%20002/_apis/git/repositories/{detail.GetProperty("configuration").GetProperty("repository").Str("id")}/items?path={Uri.EscapeDataString(path)}&$format=json&includeContent=true");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(item.Str("content")));
        Assert.Single(yaml.Documents);
        var repo = await sim.CloneAsync("Bulk 002", "bulk-002-repo-02");
        Assert.Equal("5", await Git.MustAsync(repo, null, "rev-list", "--count", "HEAD"));
        Assert.True(File.Exists(Path.Combine(repo, "pipelines", "ci-2.yml")));
    }
}

[Collection("orion")]
public sealed class OrionExpectedTests(OrionSim orion)
{
    [Fact]
    public Task Expected_totals_match_what_the_rest_api_serves_for_the_orion_estate() => ExpectedMatches.AssertAsync(orion.Host);
}

[Collection("default")]
public sealed class DefaultExpectedTests(DefaultSim sim)
{
    [Fact]
    public Task Expected_totals_match_what_the_rest_api_serves_for_the_default_estate() => ExpectedMatches.AssertAsync(sim.Host);

    [Fact]
    public async Task The_default_seed_keeps_its_original_shape_and_the_dangling_pull_request_is_fixed()
    {
        var expected = await sim.Host.ExpectedAsync();
        Assert.Equal(5, expected.GetProperty("projects").GetInt32());
        Assert.Equal(9, expected.GetProperty("gitRepositories").GetInt32());
        Assert.Equal(4, expected.GetProperty("tfvcRoots").GetInt32());
        Assert.Equal(171, expected.GetProperty("refs").GetInt32());
        Assert.Equal(7, expected.GetProperty("users").GetInt32());
        Assert.Equal(4, expected.GetProperty("groups").GetInt32());
        Assert.Equal(8, expected.GetProperty("memberships").GetInt32());
        Assert.Equal(9, expected.GetProperty("aclEntries").GetInt32());
        Assert.Equal(4, expected.GetProperty("distinctAclEntries").GetInt32());
        Assert.Equal(1, expected.GetProperty("disabledRepositories").GetInt32());
        Assert.Equal(1, expected.GetProperty("pullRequests").GetInt32());

        // the bug 105 pull request link now resolves
        var link = (await sim.Host.JsonAsync("/Alpha%20Platform/_apis/wit/workitems?ids=105")).Items()[0].GetProperty("relations").EnumerateArray()
            .Select(r => r.Str("url")).Single(u => u.Contains("/Git/PullRequestId/"));
        var id = int.Parse(link[(link.LastIndexOf("%2f") + 3)..]);
        var pr = await sim.Host.JsonAsync($"/Alpha%20Platform/_apis/git/repositories/alpha-api/pullrequests/{id}");
        Assert.Equal("completed", pr.Str("status"));

        // legacy shapes still hold: the product reads these exact properties
        var pipeline = await sim.Host.JsonAsync("/Alpha%20Platform/_apis/pipelines/11");
        Assert.Equal("yaml", pipeline.GetProperty("configuration").Str("type"));
        Assert.Equal("/azure-pipelines.yml", pipeline.GetProperty("configuration").Str("path"));
        var definition = await sim.Host.JsonAsync("/Alpha%20Platform/_apis/build/definitions/11");
        Assert.Equal([3], definition.GetProperty("variableGroups").EnumerateArray().Select(g => g.GetProperty("id").GetInt32()));
        Assert.Equal("prod-secrets", definition.GetProperty("variableGroups")[0].Str("name"));
        Assert.Equal("sim-value", definition.GetProperty("variables").GetProperty("BUILD_CONFIG").Str("value"));
        Assert.True(definition.GetProperty("variables").GetProperty("DEPLOY_PASSWORD").GetProperty("isSecret").GetBoolean());
        var group = await sim.Host.JsonAsync("/Alpha%20Platform/_apis/distributedtask/variablegroups/3");
        Assert.Equal("prod-secrets", group.Str("name"));
        Assert.True(group.GetProperty("variables").GetProperty("APP_TOKEN").GetProperty("isSecret").GetBoolean());
        var endpoints = (await sim.Host.JsonAsync("/Alpha%20Platform/_apis/serviceendpoint/endpoints")).Items();
        Assert.Equal(["azure-prod", "npm-feed"], endpoints.Select(e => e.Str("name")));
        Assert.Equal(["azurerm", "externalnpmregistry"], endpoints.Select(e => e.Str("type")));
        var repoId = (await sim.Host.JsonAsync("/Alpha%20Platform/_apis/git/repositories")).Items().Single(r => r.Str("name") == "alpha-api").Str("id");
        var content = (await sim.Host.JsonAsync($"/Alpha%20Platform/_apis/git/repositories/{repoId}/items?path=/azure-pipelines.yml&includeContent=true&$format=json")).Str("content");
        Assert.StartsWith("trigger:\n- main\n", content);
    }
}

public static class ExpectedMatches
{
    public static async Task AssertAsync(SimHost sim)
    {
        var expected = await sim.ExpectedAsync();
        var projects = (await sim.JsonAsync("/_apis/projects?$top=1000")).Items();
        Assert.Equal(expected.GetProperty("projects").GetInt32(), projects.Length);

        var gitProjects = new List<(string Name, JsonElement[] Repos)>();
        foreach (var project in projects)
        {
            var detail = await sim.JsonAsync($"/_apis/projects/{project.Str("id")}");
            if (detail.GetProperty("capabilities").GetProperty("versioncontrol").Str("sourceControlType") == "Git")
                gitProjects.Add((project.Str("name"), (await sim.JsonAsync($"/{SimHost.Esc(project.Str("name"))}/_apis/git/repositories")).Items()));
        }
        Assert.Equal(expected.GetProperty("gitRepositories").GetInt32(), gitProjects.Sum(p => p.Repos.Length));
        Assert.Equal(expected.GetProperty("disabledRepositories").GetInt32(), gitProjects.Sum(p => p.Repos.Count(r => r.GetProperty("isDisabled").GetBoolean())));

        var refs = 0; var pullRequests = 0; var runs = 0; var yaml = 0; var classic = 0; var environments = 0; var policies = 0;
        var endpoints = new HashSet<string>(); var groups = new HashSet<int>();
        foreach (var (name, repos) in gitProjects)
        {
            var project = $"/{SimHost.Esc(name)}";
            foreach (var repo in repos)
            {
                if (!repo.GetProperty("isDisabled").GetBoolean()) refs += (await sim.RefsAsync(name, repo.Str("name"))).Count;
                pullRequests += (await sim.JsonAsync($"{project}/_apis/git/repositories/{SimHost.Esc(repo.Str("name"))}/pullrequests?searchCriteria.status=all&$top=1000")).GetProperty("count").GetInt32();
            }
            foreach (var definition in (await sim.JsonAsync($"{project}/_apis/build/definitions?includeAllProperties=true&$top=1000")).Items())
            {
                if (definition.GetProperty("process").GetProperty("type").GetInt32() == 2) yaml++; else classic++;
                runs += (await sim.JsonAsync($"{project}/_apis/pipelines/{definition.GetProperty("id").GetInt32()}/runs?$top=1000")).GetProperty("count").GetInt32();
            }
            foreach (var endpoint in (await sim.JsonAsync($"{project}/_apis/serviceendpoint/endpoints")).Items()) endpoints.Add(endpoint.Str("id"));
            foreach (var group in (await sim.JsonAsync($"{project}/_apis/distributedtask/variablegroups?$top=1000")).Items()) groups.Add(group.GetProperty("id").GetInt32());
            environments += (await sim.JsonAsync($"{project}/_apis/distributedtask/environments?$top=1000")).GetProperty("count").GetInt32();
            policies += (await sim.JsonAsync($"{project}/_apis/policy/configurations?$top=1000")).GetProperty("count").GetInt32();
        }
        Assert.Equal(expected.GetProperty("refs").GetInt32(), refs);
        Assert.Equal(expected.GetProperty("pullRequests").GetInt32(), pullRequests);
        Assert.Equal(expected.GetProperty("runs").GetInt32(), runs);
        Assert.Equal(expected.GetProperty("yamlPipelines").GetInt32(), yaml);
        Assert.Equal(expected.GetProperty("classicPipelines").GetInt32(), classic);
        Assert.Equal(expected.GetProperty("serviceEndpoints").GetInt32(), endpoints.Count);
        Assert.Equal(expected.GetProperty("variableGroups").GetInt32(), groups.Count);
        Assert.Equal(expected.GetProperty("environments").GetInt32(), environments);
        Assert.Equal(expected.GetProperty("policies").GetInt32(), policies);
        Assert.Equal(expected.GetProperty("hooks").GetInt32(), (await sim.JsonAsync("/_apis/hooks/subscriptions")).GetProperty("count").GetInt32());

        var identities = (await sim.JsonAsync("/_apis/identities?queryMembership=Direct")).Items();
        Assert.Equal(expected.GetProperty("users").GetInt32(), identities.Count(i => !i.GetProperty("isContainer").GetBoolean()));
        Assert.Equal(expected.GetProperty("groups").GetInt32(), identities.Count(i => i.GetProperty("isContainer").GetBoolean()));
        Assert.Equal(expected.GetProperty("memberships").GetInt32(), identities.Sum(i => i.GetProperty("members").GetArrayLength()));
    }
}
