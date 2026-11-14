using System.Text.Json;
using AdoSim;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.AzureDevOps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The "orion" estate (real pipeline YAML, templates, integrations) served by tools/AdoSim in-process.
public sealed class OrionSimFixture : IAsyncLifetime
{
    public const string Pat = "sim-pat";
    private string _dir = "";
    public WebApplication App { get; private set; } = null!;
    public string ServerRoot { get; private set; } = "";
    public JsonElement Expected { get; private set; }
    public HttpClient Http { get; } = new();

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adosim-orion-" + Guid.NewGuid().ToString("N"));
        App = await AdoSimApp.StartAsync(new SimOptions(SeedSpec.Load("orion"), _dir, Pat, "7.0", 50, "http://127.0.0.1:0"));
        var origin = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        ServerRoot = origin + "/tfs";
        Http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + Pat)));
        Expected = JsonDocument.Parse(await Http.GetStringAsync(origin + "/_sim/expected")).RootElement.Clone();
    }

    public async Task DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch (IOException) { /* temp data only */ }
    }

    public async Task<JsonElement> GetAsync(string path) =>
        JsonDocument.Parse(await Http.GetStringAsync(ServerRoot + "/DefaultCollection" + path)).RootElement.Clone();
}

public sealed class OrionPipelinesReadByTheProductTests(OrionSimFixture sim) : IClassFixture<OrionSimFixture>
{
    private static readonly ServerEndpointPolicy Policy = new(["127.0.0.1"], allowHttp: true);

    private async Task<List<AzurePipelineSource>> ReadAllPipelinesAsync()
    {
        var reader = (IAzureWorkloadReader)((IServerAwareWorkloadReader)new AzureDevOpsWorkloadReader(new HttpClient(), Policy)).ForServer(sim.ServerRoot);
        var all = new List<AzurePipelineSource>();
        foreach (var project in (await sim.GetAsync("/_apis/projects")).GetProperty("value").EnumerateArray())
        {
            var projectId = project.GetProperty("id").GetString()!;
            foreach (var pipeline in (await sim.GetAsync($"/{projectId}/_apis/pipelines")).GetProperty("value").EnumerateArray())
                all.Add(await reader.GetPipelineAsync("DefaultCollection", projectId, pipeline.GetProperty("id").GetInt32(), OrionSimFixture.Pat, default));
        }
        return all;
    }

    [Fact]
    public async Task The_converters_yaml_read_returns_the_source_of_every_yaml_pipeline_and_nothing_for_classic_ones()
    {
        var reader = (IPipelineYamlReader)((IServerAwareWorkloadReader)new AzureDevOpsWorkloadReader(new HttpClient(), Policy)).ForServer(sim.ServerRoot);
        var read = 0;
        foreach (var pipeline in await ReadAllPipelinesAsync())
        {
            var project = (await sim.GetAsync("/_apis/projects")).GetProperty("value").EnumerateArray()
                .Single(p => sim.GetAsync($"/{p.GetProperty("id").GetString()}/_apis/pipelines").Result.GetProperty("value").EnumerateArray().Any(x => x.GetProperty("id").GetInt32() == pipeline.Id))
                .GetProperty("id").GetString()!;
            var yaml = await reader.ReadPipelineYamlAsync("DefaultCollection", project, pipeline.Id, OrionSimFixture.Pat, default);
            Assert.False(yaml.TooLarge);
            if (pipeline.Kind != "yaml") { Assert.Null(yaml.Content); continue; }
            Assert.False(string.IsNullOrWhiteSpace(yaml.Content));
            // the very text the preview analysed: same tasks, in the same order
            Assert.Equal(pipeline.Structure!.Tasks, AzureYamlTaskAnalyzer.Analyze(yaml.Content).Tasks);
            read++;
        }
        Assert.Equal(sim.Expected.GetProperty("yamlPipelines").GetInt32(), read);
    }

    // The estate's own pipelines include templates of their repository and of orion-templates (another project of the organization): they are read through the product's reader from the simulator, with the
    // very paths the pipelines wrote. (A simulator is not an oracle: that the real Azure DevOps answers the same way is what the live smoke test checks.)
    [Fact]
    public async Task The_templates_the_orion_pipelines_include_are_found_and_read_from_their_own_repository_and_from_the_templates_repository()
    {
        var server = (IServerAwareWorkloadReader)new AzureDevOpsWorkloadReader(new HttpClient(), Policy);
        var yamlReader = (IPipelineYamlReader)server.ForServer(sim.ServerRoot);
        var files = (IRepositoryFileReader)server.ForServer(sim.ServerRoot);
        var found = new Dictionary<string, ResolvedTemplates>();
        foreach (var project in (await sim.GetAsync("/_apis/projects")).GetProperty("value").EnumerateArray())
        {
            var projectId = project.GetProperty("id").GetString()!;
            foreach (var pipeline in (await sim.GetAsync($"/{projectId}/_apis/pipelines")).GetProperty("value").EnumerateArray())
            {
                var id = pipeline.GetProperty("id").GetInt32();
                var yaml = await yamlReader.ReadPipelineYamlAsync("DefaultCollection", projectId, id, OrionSimFixture.Pat, default);
                if (yaml.Content is null) continue;
                Assert.False(string.IsNullOrEmpty(yaml.RepositoryId)); // where the pipeline's own file is
                Assert.False(string.IsNullOrEmpty(yaml.Path));
                var resolved = await PipelineTemplates.ResolveAsync(yaml.Content, new(projectId, yaml.RepositoryId, yaml.Path), files, "DefaultCollection", OrionSimFixture.Pat, new(), default);
                if (!resolved.IsEmpty) found[$"{projectId}/{pipeline.GetProperty("name").GetString()}"] = resolved;
            }
        }

        // the estate: orion-api includes two templates of its own repository (relative to its file) and one of orion-templates, orion-web one of orion-templates, orion-infra one of a repository named
        // without a project, which is a repository of its own project
        Assert.Equal(3, found.Count);
        var api = found.Single(f => f.Key.EndsWith("/orion-api CI-CD")).Value;
        Assert.Equal([("self", "/pipelines/templates/variables.yml", (string?)null), ("self", "/pipelines/templates/dotnet-quality.yml", null), ("Orion Platform/orion-templates", "/steps/security-scan.yml", "refs/heads/main")],
            api.Files.Select(f => (f.Repository, f.Path, f.Ref)));
        var web = found.Single(f => f.Key.EndsWith("/orion-web CI-CD")).Value;
        Assert.Equal([("Orion Platform/orion-templates", "/variables/common.yml", (string?)"refs/heads/main")], web.Files.Select(f => (f.Repository, f.Path, f.Ref)));
        var infra = found.Single(f => f.Key.EndsWith("/orion-infra Terraform"));
        var infraFile = Assert.Single(infra.Value.Files);
        Assert.Equal((infra.Key.Split('/')[0] + "/orion-templates", "/steps/terraform-plan.yml"), (infraFile.Repository, infraFile.Path));
        Assert.All(found.Values, r => Assert.Empty(r.Unresolved)); // everything the estate includes is readable
        Assert.All(found.Values.SelectMany(r => r.Files), f => { Assert.False(string.IsNullOrWhiteSpace(f.Text)); Assert.Matches("^[0-9a-f]{64}$", f.Sha256); });
        Assert.Contains(api.Files.SelectMany(f => f.Tasks).Concat(found.Values.SelectMany(r => r.Files).SelectMany(f => f.Tasks)), t => t.Contains('@')); // the templates carry tasks of their own
    }

    [Fact]
    public async Task Every_orion_pipeline_is_read_and_yaml_pipelines_are_structurally_analysed()
    {
        var pipelines = await ReadAllPipelinesAsync();
        var expectedYaml = sim.Expected.GetProperty("yamlPipelines").GetInt32();
        var expectedClassic = sim.Expected.GetProperty("classicPipelines").GetInt32();
        Assert.Equal(expectedYaml + expectedClassic, pipelines.Count);
        Assert.Equal(expectedClassic, pipelines.Count(p => p.Kind != "yaml"));

        var yaml = pipelines.Where(p => p.Kind == "yaml").ToArray();
        Assert.Equal(expectedYaml, yaml.Length);
        Assert.All(yaml, p =>
        {
            Assert.NotNull(p.SourceYaml);
            Assert.NotNull(p.Structure);
            Assert.False(p.Structure!.ParseFailed, $"{p.Name}: YAML analysis failed");
            Assert.False(p.Structure.Truncated, $"{p.Name}: analysis truncated");
        });

        // the estate covers the conversion difficulty range: dotnet/docker/node/terraform tasks, scripts, templates
        var kinds = yaml.SelectMany(p => p.Structure!.Tasks).Select(t => t.Kind + ":" + t.Identifier).ToHashSet();
        foreach (var expected in new[] { "task:DotNetCoreCLI@2", "task:Docker@2", "task:NodeTool@0", "task:AzureCLI@2", "script:script", "checkout:checkout" })
            Assert.Contains(expected, kinds);
        Assert.Contains(kinds, k => k.StartsWith("template:"));
    }

    [Fact]
    public async Task Variable_groups_and_service_connections_reach_the_preview_input()
    {
        var pipelines = await ReadAllPipelinesAsync();
        var api = pipelines.Single(p => p.Name == "orion-api CI-CD");
        // build definitions list variable groups as objects: the product must resolve them by id
        Assert.Contains(api.Variables, v => v.Group != "pipeline");
        Assert.Contains(api.Variables, v => v.IsSecret);
        Assert.NotEmpty(api.ServiceConnections);
        Assert.Contains(api.ServiceConnections, c => c.Kind == "dockerregistry");
        // the YAML itself never travels with secret values
        Assert.DoesNotContain("sim-value", api.SourceYaml ?? "");
    }
}
