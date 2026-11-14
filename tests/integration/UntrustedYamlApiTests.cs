using System.Net;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// How deeply a document nests must not decide whether the API process survives: the YAML parser's recursion overflows the stack at tens of thousands of levels, and nothing
// can catch that. Here a model answers with such a workflow, and the source pipeline itself is such a document; every route answers and the API keeps answering.
public sealed class UntrustedYamlModelAnswerApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    private static string Flow(int depth) => new string('[', depth) + new string(']', depth);

    [RequiresAnalysersFact]
    public async Task A_workflow_a_model_wrote_that_nests_too_deeply_is_blocked_and_the_api_keeps_answering()
    {
        // a profile that allows a long answer: a model answering with brackets can fill 140 KB with far fewer tokens than characters
        using var created = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: new
        {
            name = "Long answers", provider = "OpenAICompatible", endpoint = Origin + "/v1", model = "gpt-sim", auth = "ApiKey", apiKey = Key, maxOutputTokens = 64_000
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ai = (await JsonOf(created)).GetProperty("id").GetGuid();
        using var allowed = await SendAsync(HttpMethod.Put, $"/api/ai-connections/{ai}/source-consent", Who.Superadmin, new { sendSourceContent = true });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        // about 140 KB: under every size limit the product has, and far past what the parser's recursion survives
        var deepAnswer = "# azure-tasks: converted=UseDotNet@2 manual=none\nname: x\non:\n  workflow_dispatch:\njobs: " + Flow(70_000) + "\n";

        var conversion = await ConvertAsync(ai, deepAnswer, maxRepairs: 0);
        Assert.False(conversion.GetProperty("approvable").GetBoolean());
        Assert.Equal("Blocked", conversion.GetProperty("status").GetString());
        Assert.True(conversion.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("code").GetString() == "YAML_TOO_DEEP"), conversion.GetProperty("findings").ToString() + " reason=" + conversion.GetProperty("reason"));
        var finding = conversion.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("code").GetString() == "YAML_TOO_DEEP");
        Assert.Equal("Blocker", finding.GetProperty("severity").GetString());
        Assert.DoesNotContain("[[[[", finding.GetProperty("message").GetString());

        // alive: the same route answers again, with an ordinary workflow, in the same process
        var again = await ConvertAsync(ai, GoodWorkflow);
        Assert.True(again.GetProperty("approvable").GetBoolean());
    }
}

public sealed class UntrustedYamlSourceApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    // the Azure stub's pipeline YAML begins with a variables entry that nests 30,000 levels deep (about 60 KB: under the redactor's limit, over what the parser's recursion survives)
    protected override string AzureYaml => "variables: " + new string('[', 30_000) + new string(']', 30_000) + "\n";

    [RequiresAnalysersFact]
    public async Task A_source_pipeline_that_nests_too_deeply_is_reported_as_unreadable_by_the_preview_the_conversion_and_the_triggers()
    {
        // the deterministic preview: the analysis failed, and it says so as a blocker instead of listing tasks it could not see
        using var preview = await SendAsync(HttpMethod.Post, "/api/workloads/pipelines/preview", Who.Admin,
            new { sourceConnectionId = Api.SourceConnectionId, destinationConnectionId = Api.DestinationConnectionId, projectId = "project", pipelineId = 42, targetRepository = "migration-target" });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var shown = await JsonOf(preview);
        Assert.Contains(shown.GetProperty("issues").EnumerateArray(), w => w.GetProperty("code").GetString() == "SourceYamlAnalysisFailed" && w.GetProperty("severity").GetString() == "blocker");

        // the conversion still reads the source (its task list comes from the text scan too) and reaches the model; nothing about the document ended the process
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), GoodWorkflow);
        Assert.Equal("ReadyForReview", conversion.GetProperty("status").GetString());
        Assert.Empty(conversion.GetProperty("plan").GetProperty("runnerLabels").EnumerateArray());

        // the triggers cannot be read from it, and the answer says that
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Reviewed." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var derived = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{conversion.GetProperty("proposalId").GetGuid()}/derive-triggers", Who.Admin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, derived.StatusCode);
        Assert.Equal("SourceYamlUnreadable", (await JsonOf(derived)).GetProperty("error").GetString());
    }
}
