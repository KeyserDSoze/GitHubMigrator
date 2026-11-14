using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// A classic (designer) pipeline has no file, so what the model reads is the product's rendering of its definition. On the real API with PostgreSQL, the real analysers and the simulators: the rendering is what is
// sent (redacted, with the service connection named and credential-named inputs withheld), the tasks in it must be accounted for by the real gates, the proposal and the pull request say what the source was, and the
// triggers of a classic pipeline are never derived.
public sealed class ClassicPipelinesApiTests() : ProposalApiTestBase(singleReviewer: false, githubSim: true)
{
    private const string Connection = "7a1c2d3e-4b5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string Secret = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    protected override string? AzureClassicDefinition => """
        {
          "id": 42, "name": "Orion Legacy", "type": "build", "buildNumberFormat": "$(date:yyyyMMdd)$(rev:.r)",
          "queue": { "id": 7, "name": "Windows Agents", "pool": { "id": 7, "name": "Windows Agents", "isHosted": false } },
          "repository": { "id": "source-git", "type": "TfsGit", "name": "source-git", "clean": "true" },
          "variableGroups": [ { "id": 7, "name": "prod" } ],
          "variables": { "BUILD_REGION": { "value": "westeurope", "isSecret": false }, "DEPLOY_PASSWORD": { "isSecret": true } },
          "triggers": [ { "triggerType": "continuousIntegration", "branchFilters": [ "+refs/heads/main" ], "batchChanges": false } ],
          "options": [],
          "process": { "type": 1, "phases": [ { "name": "Agent job 1", "refName": "Phase_1", "target": { "type": 1 }, "steps": [
            { "enabled": true, "displayName": "Use .NET", "condition": "succeeded()", "task": { "id": "b0ce7256-7898-45d3-9cb5-176b752bfea6", "versionSpec": "2.*", "definitionType": "task" }, "inputs": { "packageType": "sdk", "version": "8.x" } },
            { "enabled": true, "displayName": "Deploy", "condition": "succeeded()", "task": { "id": "e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "versionSpec": "2.*", "definitionType": "task" },
              "inputs": { "targetType": "inline", "script": "curl -H 'Authorization: token @SECRET@' https://example.invalid\necho deploying to $(BUILD_REGION)", "azureSubscription": "@CONNECTION@", "deployPassword": "hunter2-never-shown", "template": "steps/never-read.yml" },
              "environment": { "SIGN_TOKEN": "env-secret-never-shown" } } ] } ] }
        }
        """.Replace("@SECRET@", Secret).Replace("@CONNECTION@", Connection);

    protected override string AzureClassicEndpoints => """{"value":[{"id":"@CONNECTION@","name":"Azure subscription","type":"azurerm"}]}""".Replace("@CONNECTION@", Connection);

    private static string Accounting(string workflow, string tasks) => workflow.Replace("converted=UseDotNet@2 manual=none", $"converted={tasks} manual=none");

    private async Task<string> ModelRequestsAsync() => await Control.GetStringAsync(Origin + "/_sim/requests");

    private async Task<JsonElement> ConvertedAsync() => await ConvertAsync(await AiConnectionAsync(consent: true), Accounting(GoodWorkflow, "UseDotNet@2,PowerShell@2"));

    private async Task<string> RepoAsync(string prefix)
    {
        var name = await GitHub!.CreateRepoAsync(prefix);
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{name}/contents/README.md", new { message = "init", content = Convert.ToBase64String("# r\n"u8.ToArray()) });
        return name;
    }

    [RequiresAnalysersFact]
    public async Task The_model_reads_a_rendering_of_the_definition_with_nothing_secret_in_it_and_the_proposal_says_what_the_source_was()
    {
        var conversion = await ConvertedAsync();

        Assert.True(conversion.GetProperty("approvable").GetBoolean(), conversion.GetProperty("findings").ToString());
        Assert.Equal("ReadyForReview", conversion.GetProperty("status").GetString());
        var provenance = conversion.GetProperty("provenance");
        Assert.Equal("classic-definition", provenance.GetProperty("sourceOrigin").GetString());
        var notes = provenance.GetProperty("sourceNotes").EnumerateArray().Select(n => n.GetString()!).ToArray();
        Assert.Contains(notes, n => n.StartsWith("Continuous integration trigger, not carried over: branches [+refs/heads/main]"));
        Assert.Contains("2 step input(s) are not shown because their name looks like a credential.", notes);
        Assert.Contains("1 step input(s) held a service connection's id and are shown as its name.", notes);
        Assert.Empty(provenance.GetProperty("templates").EnumerateArray());

        var request = await ModelRequestsAsync();
        Assert.Contains("THE SOURCE IS A CLASSIC (DESIGNER) BUILD DEFINITION", request);
        Assert.Contains("Rendered by GitHubMigrator from a classic (designer) build definition", request);
        Assert.Contains("- task: UseDotNet@2", request);
        Assert.Contains("- task: PowerShell@2", request);
        Assert.Contains("azureSubscription: 'Azure subscription'", request); // the connection by name, so the plan could map it
        Assert.Contains("echo deploying to $(BUILD_REGION)", request);
        Assert.Contains("<REDACTED:", request);                                // the token inside the script, redacted on top of everything else
        foreach (var never in new[] { Secret, "hunter2-never-shown", "env-secret-never-shown", Connection, "DONT_SHOW_GROUP_TOKEN" }) Assert.DoesNotContain(never, request);
        Assert.DoesNotContain("[[TEMPLATE-", request);       // a classic pipeline includes no templates

        // what was read from Azure: the pipeline, its definition, the group and the service connections; never a file, never the task catalogue (every task was a built-in one)
        var paths = Api.Calls.Select(c => c.Url.AbsolutePath).ToArray();
        Assert.Contains(paths, p => p.EndsWith("/_apis/build/definitions/42"));
        Assert.Contains(paths, p => p.EndsWith("/_apis/serviceendpoint/endpoints"));
        Assert.DoesNotContain(paths, p => p.EndsWith("/items"));
        Assert.DoesNotContain(paths, p => p.Contains("/_apis/distributedtask/tasks/"));

        // kept with the proposal: the origin and the notes, never the rendering or what it was made of
        var id = conversion.GetProperty("proposalId").GetGuid();
        var row = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.Contains("classic-definition", row.Evidence);
        Assert.DoesNotContain("echo deploying", row.Evidence);
        Assert.DoesNotContain(Secret, row.Evidence);
        Assert.DoesNotContain("hunter2-never-shown", conversion.GetRawText());
    }

    [RequiresAnalysersFact]
    public async Task An_input_that_happens_to_be_called_template_is_not_a_template_to_read_even_when_templates_were_asked_for()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), Accounting(GoodWorkflow, "UseDotNet@2,PowerShell@2"), includeTemplates: true);

        Assert.True(conversion.GetProperty("approvable").GetBoolean(), conversion.GetProperty("findings").ToString());
        var provenance = conversion.GetProperty("provenance");
        Assert.Empty(provenance.GetProperty("templates").EnumerateArray());
        Assert.Empty(provenance.GetProperty("unresolvedTemplates").EnumerateArray());
        Assert.DoesNotContain(Api.Calls, c => c.Url.AbsolutePath.EndsWith("/items")); // no file of any repository was asked for
        Assert.Contains("template: 'steps/never-read.yml'", await ModelRequestsAsync()); // it is just an input of the task, shown as one
    }

    [RequiresAnalysersFact]
    public async Task A_task_only_the_definition_has_must_be_accounted_for_like_any_other()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), GoodWorkflow, maxRepairs: 0); // accounts for UseDotNet@2 only

        Assert.False(conversion.GetProperty("approvable").GetBoolean());
        var finding = Assert.Single(conversion.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "TASK_NOT_ACCOUNTED");
        Assert.Contains("PowerShell@2", finding.GetProperty("message").GetString());
    }

    [RequiresAnalysersFact]
    public async Task The_pull_request_says_the_source_was_a_classic_definition_and_its_triggers_are_never_derived()
    {
        var conversion = await ConvertedAsync();
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Read it against the definition." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var repo = await RepoAsync("classic");
        using var plan = await SendAsync(HttpMethod.Post, Url(conversion, "/publication-plan"), Who.Superadmin, new { destinationConnectionId = Api.DestinationConnectionId, repository = repo });
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var body = JsonDocument.Parse(await plan.Content.ReadAsStringAsync()).RootElement.GetProperty("pullRequestBody").GetString()!;
        Assert.Contains("| Source | a **classic (designer) build definition**, rendered as text by this product: there was no YAML file to read |", body);
        Assert.Contains("It is **not** proven equivalent to the Azure pipeline.", body);

        // triggers: refused before anything is read
        var reads = Api.Calls.Count;
        using var derived = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{conversion.GetProperty("proposalId").GetGuid()}/derive-triggers", Who.Admin);
        Assert.Equal(HttpStatusCode.Conflict, derived.StatusCode);
        Assert.Equal("ClassicTriggersNotSupported", JsonDocument.Parse(await derived.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        Assert.Equal(reads, Api.Calls.Count);
    }
}

// A definition that is not a designer one (here a build of another kind), or that cannot be read: nothing is rendered, nothing is sent, and the refusal is the one a classic pipeline always had.
public sealed class ClassicPipelineNotRenderableApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    protected override string? AzureClassicDefinition => """{"id":42,"type":"xaml","process":{"type":3}}""";

    [RequiresAnalysersFact]
    public async Task A_definition_that_cannot_be_rendered_is_refused_as_a_classic_pipeline_always_was_and_nothing_reaches_the_model()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), GoodWorkflow);

        Assert.Equal("NotAttempted", conversion.GetProperty("status").GetString());
        Assert.Equal("ClassicPipelineUnsupported", conversion.GetProperty("reason").GetString());
        Assert.False(conversion.GetProperty("saved").GetBoolean());
        Assert.DoesNotContain("CLASSIC", await Control.GetStringAsync(Origin + "/_sim/requests"));
        Assert.DoesNotContain("task:", await Control.GetStringAsync(Origin + "/_sim/requests"));
    }
}
