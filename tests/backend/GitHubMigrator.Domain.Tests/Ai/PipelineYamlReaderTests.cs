using System.Net;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.AzureDevOps;

namespace GitHubMigrator.Domain.Tests.Ai;

public sealed class PipelineYamlReaderTests
{
    private static AzureDevOpsWorkloadReader Reader(StubHandler handler) => new(new HttpClient(handler));

    private static HttpResponseMessage Pipeline(string type = "yaml", bool withPath = true) => StubHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        id = 42, name = "p",
        configuration = withPath ? (object)new { type, path = "/azure-pipelines.yml", repository = new { id = "repo-1" } } : new { type }
    }));

    private static HttpResponseMessage Item(string content) => StubHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { content }));

    [Fact]
    public async Task It_reads_the_file_the_pipeline_points_at_and_only_that_file()
    {
        var handler = new StubHandler().Then(() => Pipeline()).Then(() => Item("steps:\n- script: echo hi\n"));
        var yaml = await Reader(handler).ReadPipelineYamlAsync("my-org", "proj", 42, "pat", default);
        Assert.Equal("steps:\n- script: echo hi\n", yaml.Content);
        Assert.False(yaml.TooLarge);
        Assert.Equal(2, handler.Sent.Count);
        Assert.Contains("/my-org/proj/_apis/pipelines/42", handler.Sent[0].Uri.AbsolutePath);
        Assert.Contains("/_apis/git/repositories/repo-1/items", handler.Sent[1].Uri.AbsolutePath);
        Assert.Contains("includeContent=true", handler.Sent[1].Uri.Query);
        Assert.Contains("path=%2Fazure-pipelines.yml", handler.Sent[1].Uri.Query);
    }

    [Fact]
    public async Task A_classic_pipeline_or_a_missing_file_gives_nothing_and_an_oversized_file_is_refused_not_cut()
    {
        var classic = await Reader(new StubHandler().Then(() => Pipeline("designerJson"))).ReadPipelineYamlAsync("o", "p", 1, "pat", default);
        Assert.Null(classic.Content);

        var missing = await Reader(new StubHandler().Then(() => Pipeline()).Then(() => new HttpResponseMessage(HttpStatusCode.NotFound)))
            .ReadPipelineYamlAsync("o", "p", 1, "pat", default);
        Assert.Null(missing.Content);
        Assert.False(missing.TooLarge);

        var huge = await Reader(new StubHandler().Then(() => Pipeline()).Then(() => Item(new string('x', AzureDevOpsWorkloadReader.MaxPipelineYamlChars + 1))))
            .ReadPipelineYamlAsync("o", "p", 1, "pat", default);
        Assert.Null(huge.Content);
        Assert.True(huge.TooLarge);

        var atLimit = await Reader(new StubHandler().Then(() => Pipeline()).Then(() => Item(new string('x', AzureDevOpsWorkloadReader.MaxPipelineYamlChars))))
            .ReadPipelineYamlAsync("o", "p", 1, "pat", default);
        Assert.NotNull(atLimit.Content);
    }

    [Fact]
    public async Task When_the_pipeline_lacks_a_path_the_build_definition_supplies_it()
    {
        var handler = new StubHandler().Then(() => Pipeline(withPath: false))
            .Then(() => StubHandler.Json(HttpStatusCode.OK, """{"process":{"yamlFilename":"build/ci.yml"},"repository":{"id":"repo-9"}}"""))
            .Then(() => Item("steps: []\n"));
        var yaml = await Reader(handler).ReadPipelineYamlAsync("o", "p", 7, "pat", default);
        Assert.Equal("steps: []\n", yaml.Content);
        Assert.Contains("/_apis/build/definitions/7", handler.Sent[1].Uri.AbsolutePath);
        Assert.Contains("/repositories/repo-9/items", handler.Sent[2].Uri.AbsolutePath);
        Assert.Contains("path=build%2Fci.yml", handler.Sent[2].Uri.Query);
    }

    [Fact]
    public async Task The_credential_is_sent_as_basic_auth_to_the_azure_host_and_nowhere_else()
    {
        var handler = new StubHandler().Then(() => Pipeline()).Then(() => Item("a: b"));
        await Reader(handler).ReadPipelineYamlAsync("o", "p", 1, "my-pat-value", default);
        Assert.All(handler.Sent, r =>
        {
            Assert.Equal("dev.azure.com", r.Uri.Host);
            Assert.StartsWith("Basic ", r.Headers["Authorization"]);
            Assert.DoesNotContain("my-pat-value", r.Uri.ToString());
        });
    }
}
