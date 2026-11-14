using System.Net;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.AzureDevOps;

namespace GitHubMigrator.Domain.Tests.Ai;

// A classic (designer) pipeline has no file: the reader reads its definition with GET, asks the organization for the names of what the definition only holds by id (service connections and, for tasks the built-in
// table does not know, the task catalogue, an internal endpoint) and renders it as text. What it asks, and what happens when the organization does not answer, is what is pinned here.
public sealed class AzureClassicPipelineReaderTests
{
    private static HttpResponseMessage Pipeline(string type = "designerJson") => StubHandler.Json(HttpStatusCode.OK, "{\"id\":1,\"name\":\"legacy\",\"configuration\":{\"type\":\"" + type + "\"}}");

    private static HttpResponseMessage Json(string body) => StubHandler.Json(HttpStatusCode.OK, body);

    private static HttpResponseMessage Gone() => new(HttpStatusCode.NotFound);

    private static AzureDevOpsWorkloadReader Reader(StubHandler handler) => new(new HttpClient(handler));

    private static string Definition(string steps, string extra = "") =>
        "{" + (extra.Length > 0 ? extra + "," : "") + "\"repository\": { \"id\": \"repo-1\", \"type\": \"TfsGit\" }, \"process\": { \"type\": 1, \"phases\": [ { \"name\": \"Agent job 1\", \"steps\": " + steps + " } ] } }";

    private static string Step(string id, string spec = "2.*", string inputs = "{}") =>
        "{ \"displayName\": \"s\", \"enabled\": true, \"task\": { \"id\": \"" + id + "\", \"versionSpec\": \"" + spec + "\", \"definitionType\": \"task\" }, \"inputs\": " + inputs + " }";

    private const string PowerShell = "e213ff0f-5d5c-4791-802d-52ea3e7be1f1";

    [Fact]
    public async Task A_classic_pipeline_is_read_from_its_definition_and_the_service_connections_it_names_by_id_are_named_for_the_text()
    {
        var connection = "11111111-2222-3333-4444-555555555555";
        var handler = new StubHandler()
            .Then(() => Pipeline())
            .Then(() => Json(Definition("[" + Step(PowerShell, inputs: $"{{ \"azureSubscription\": \"{connection}\", \"script\": \"echo hi\" }}") + "]")))
            .Then(() => Json("{\"count\":2,\"value\":[{\"id\":\"" + connection + "\",\"name\":\"Orion-Azure\",\"type\":\"azurerm\"},{\"id\":\"99999999-0000-0000-0000-000000000000\",\"name\":\"Other\",\"type\":\"github\"}]}"));

        var read = await Reader(handler).ReadPipelineYamlAsync("contoso", "Orion Commerce", 7, "ado-pat", default);

        Assert.False(read.TooLarge);
        Assert.Equal(PipelineSourceOrigins.ClassicDefinition, read.Origin);
        Assert.Equal("repo-1", read.RepositoryId);
        Assert.Null(read.Path); // there is no file
        Assert.Contains("- task: PowerShell@2", read.Content);
        Assert.Contains("azureSubscription: 'Orion-Azure'", read.Content);
        Assert.DoesNotContain(connection, read.Content);
        Assert.NotEmpty(read.Notes!);
        Assert.Contains(read.Notes!, n => n.StartsWith("1 step input(s) held a service connection's id"));

        // only reads, of exactly these: the pipeline (to see it is classic), its definition and the project's service connections
        Assert.All(handler.Sent, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(["/contoso/Orion%20Commerce/_apis/pipelines/7", "/contoso/Orion%20Commerce/_apis/build/definitions/7", "/contoso/Orion%20Commerce/_apis/serviceendpoint/endpoints"],
            handler.Sent.Select(r => r.Uri.AbsolutePath));
        Assert.All(handler.Sent, r => Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":ado-pat")), r.Headers["Authorization"]));
    }

    [Fact]
    public async Task A_task_the_table_does_not_know_is_looked_up_in_the_organizations_catalogue_with_one_read_per_id_and_the_newest_versions_name_is_used()
    {
        var custom = "aaaaaaaa-0000-0000-0000-000000000001";
        var asked = "aaaaaaaa-0000-0000-0000-000000000002";
        var handler = new StubHandler()
            .Then(() => Pipeline())
            .Then(() => Json(Definition("[" + Step(custom, "3.*") + "," + Step(custom, "3.*") + "," + Step(asked, "1.*") + "," + Step(PowerShell) + "]")))
            .Then(() => Json("""{"value":[]}""")) // service connections: none
            .Then(() => Json("{\"count\":2,\"value\":[{\"id\":\"" + custom + "\",\"name\":\"CustomDeployOld\",\"version\":{\"major\":2,\"minor\":0,\"patch\":1}},{\"id\":\"" + custom + "\",\"name\":\"CustomDeploy\",\"version\":{\"major\":3,\"minor\":1,\"patch\":0}}]}"))
            .Then(() => Json("[{\"id\":\"" + asked + "\",\"name\":\"Scanner\",\"version\":{\"major\":1}}]"));

        var read = await Reader(handler).ReadPipelineYamlAsync("contoso", "p", 7, "pat", default);

        Assert.Contains("- task: CustomDeploy@3", read.Content);
        Assert.Contains("- task: Scanner@1", read.Content);
        Assert.Contains("- task: PowerShell@2", read.Content);
        Assert.Equal([$"/contoso/_apis/distributedtask/tasks/{custom}", $"/contoso/_apis/distributedtask/tasks/{asked}"], handler.Sent.Skip(3).Select(r => r.Uri.AbsolutePath)); // organization level, once per id, not for a known task
        Assert.Equal(5, handler.Sent.Count);
    }

    [Fact]
    public async Task When_the_organization_does_not_answer_the_ids_stay_in_the_text_and_the_conversion_goes_on()
    {
        var unknown = "aaaaaaaa-0000-0000-0000-000000000001";
        var connection = "11111111-2222-3333-4444-555555555555";
        var handler = new StubHandler()
            .Then(() => Pipeline())
            .Then(() => Json(Definition("[" + Step(unknown, "2.*", $"{{ \"azureSubscription\": \"{connection}\" }}") + "]")))
            .Then(() => new HttpResponseMessage(HttpStatusCode.Forbidden))    // service connections refused
            .Then(() => Json("""{"unexpected": true}"""));                      // a catalogue answer of another shape

        var read = await Reader(handler).ReadPipelineYamlAsync("contoso", "p", 7, "pat", default);

        Assert.Contains($"- task: {unknown}@2", read.Content);
        Assert.Contains($"azureSubscription: '{connection}'", read.Content);
        Assert.Contains(read.Notes!, n => n.StartsWith("1 task(s) could not be named"));
    }

    [Fact]
    public async Task At_most_twenty_unknown_tasks_are_looked_up()
    {
        var steps = string.Join(",", Enumerable.Range(1, 30).Select(i => Step($"bbbbbbbb-0000-0000-0000-{i:D12}")));
        var handler = new StubHandler().Then(() => Pipeline()).Then(() => Json(Definition("[" + steps + "]"))).Then(() => Json("""{"value":[]}"""));
        for (var i = 0; i < ClassicPipelineRenderer.MaxLookups; i++) handler.Then(() => Gone());

        var read = await Reader(handler).ReadPipelineYamlAsync("contoso", "p", 7, "pat", default);

        Assert.NotNull(read.Content);
        Assert.Equal(3 + ClassicPipelineRenderer.MaxLookups, handler.Sent.Count);
    }

    [Fact]
    public async Task A_definition_that_cannot_be_read_or_is_not_a_designer_one_gives_nothing_and_asks_for_nothing_more()
    {
        var unreadable = new StubHandler().Then(() => Pipeline()).Then(() => Gone());
        var none = await Reader(unreadable).ReadPipelineYamlAsync("o", "p", 1, "pat", default);
        Assert.Equal((null, false, null), (none.Content, none.TooLarge, none.Origin));
        Assert.Equal(2, unreadable.Sent.Count);

        foreach (var definition in new[] { """{"process":{"type":2,"yamlFilename":"a.yml"}}""", """{"process":{"type":1,"phases":[]}}""", """{"type":"xaml"}""", "[]" })
        {
            var handler = new StubHandler().Then(() => Pipeline("unknown")).Then(() => Json(definition));
            var read = await Reader(handler).ReadPipelineYamlAsync("o", "p", 1, "pat", default);
            Assert.Null(read.Content);
            Assert.Null(read.Origin);
            Assert.Equal(2, handler.Sent.Count); // not the connections, not the catalogue
        }
    }

    [Fact]
    public async Task A_definition_too_large_to_render_is_refused_not_cut()
    {
        var steps = string.Join(",", Enumerable.Repeat(Step(PowerShell), ClassicPipelineRenderer.MaxStepsPerPhase + 1));
        var handler = new StubHandler().Then(() => Pipeline()).Then(() => Json(Definition("[" + steps + "]"))).Then(() => Json("""{"value":[]}"""));

        var read = await Reader(handler).ReadPipelineYamlAsync("o", "p", 1, "pat", default);

        Assert.Equal((null, true, PipelineSourceOrigins.ClassicDefinition), (read.Content, read.TooLarge, read.Origin));
    }

    [Fact]
    public async Task A_yaml_pipeline_is_not_touched_by_any_of_this()
    {
        var handler = new StubHandler().Then(() => Json("""{"id":1,"configuration":{"type":"yaml","path":"/a.yml","repository":{"id":"r"}}}""")).Then(() => Json("""{"content":"steps: []\n"}"""));
        var read = await Reader(handler).ReadPipelineYamlAsync("o", "p", 1, "pat", default);
        Assert.Equal(("steps: []\n", null), (read.Content, read.Origin));
        Assert.Null(read.Notes);
        Assert.DoesNotContain(handler.Sent, r => r.Uri.AbsolutePath.Contains("/build/definitions/") || r.Uri.AbsolutePath.Contains("/serviceendpoint/") || r.Uri.AbsolutePath.Contains("/distributedtask/tasks"));
    }
}
