using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The source pipeline's pool demands `docker`: the product reads that from the YAML it fetches, tells the model, hands it to the real actionlint as the only extra label it
// accepts, stores it with the plan, and uses it again when a person approves and when the Azure triggers are derived. A model cannot add a label of its own.
public sealed class RunnerLabelsApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    protected override string AzureYaml => "pool:\n  name: SelfHostedLinux\n  demands:\n  - Agent.OS -equals Linux\n  - docker\n";

    private static string OnSelfHosted(string labels) => GoodWorkflow.Replace("runs-on: ubuntu-latest", $"runs-on: [{labels}]");

    private async Task<string> ModelRequestsAsync() => await Control.GetStringAsync(Origin + "/_sim/requests");

    [RequiresAnalysersFact]
    public async Task A_workflow_that_keeps_the_label_the_pool_demanded_is_approvable_and_the_plan_the_model_and_the_ledger_all_say_which_label()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), OnSelfHosted("self-hosted, linux, docker"));

        Assert.True(conversion.GetProperty("approvable").GetBoolean(), conversion.GetProperty("findings").ToString());
        Assert.Equal(["docker"], conversion.GetProperty("plan").GetProperty("runnerLabels").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains(conversion.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "SELF_HOSTED_RUNNER" && f.GetProperty("severity").GetString() == "Warning");
        Assert.DoesNotContain(conversion.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "ACTIONLINT_RUNNER_LABEL");

        // the model was told which label it may use, and only that one
        var sent = await ModelRequestsAsync();
        Assert.Contains("RUNNER LABELS the pipeline's self-hosted pools demand: docker.", sent);

        // kept with the proposal: the stored plan carries the label
        var id = conversion.GetProperty("proposalId").GetGuid();
        var row = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id));
        using var evidence = JsonDocument.Parse(row.Evidence);
        var labels = evidence.RootElement.GetProperty("plan").EnumerateObject().Single(p => p.Name.Equals("runnerLabels", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Equal(["docker"], labels.EnumerateArray().Select(l => l.GetString()));

        // approving runs the gates again on the kept text with the kept plan: it still passes, the warning is the person's to accept
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Runners with the docker label exist." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Approved", (await JsonOf(approved)).GetProperty("state").GetString());

        // and so do the Azure triggers derived from it: the derived workflow keeps the self-hosted runner and is gated with the same label
        using var derived = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{id}/derive-triggers", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, derived.StatusCode);
        var triggers = await JsonOf(derived);
        Assert.Equal("Proposed", triggers.GetProperty("state").GetString());
        Assert.Contains("runs-on: [self-hosted, linux, docker]", triggers.GetProperty("workflowYaml").GetString());
        Assert.DoesNotContain(triggers.GetProperty("findings").EnumerateArray(), f => f.GetProperty("severity").GetString() == "Blocker");
    }

    [RequiresAnalysersFact]
    public async Task A_label_the_pool_did_not_demand_is_refused_by_the_real_actionlint_whatever_the_model_writes()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), OnSelfHosted("self-hosted, linux, docker, my-own-label"), maxRepairs: 0);

        Assert.False(conversion.GetProperty("approvable").GetBoolean());
        var blockers = conversion.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("severity").GetString() == "Blocker").ToArray();
        var label = Assert.Single(blockers, f => f.GetProperty("code").GetString() == "ACTIONLINT_RUNNER_LABEL");
        Assert.Contains("my-own-label", label.GetProperty("message").GetString());
        Assert.DoesNotContain(blockers, f => f.GetProperty("message").GetString()!.Contains("\"docker\""));
    }
}

// Without a demand nothing changes: no label in the plan, none in the prompt, and a custom label is refused as it always was.
public sealed class NoRunnerLabelsApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    [RequiresAnalysersFact]
    public async Task A_pipeline_without_demands_has_no_labels_and_a_custom_label_stays_refused()
    {
        var ai = await AiConnectionAsync(consent: true);
        var plain = await ConvertAsync(ai, GoodWorkflow);
        Assert.True(plain.GetProperty("approvable").GetBoolean());
        Assert.Empty(plain.GetProperty("plan").GetProperty("runnerLabels").EnumerateArray());
        Assert.DoesNotContain("RUNNER LABELS", await Control.GetStringAsync(Origin + "/_sim/requests"));

        var custom = await ConvertAsync(ai, GoodWorkflow.Replace("runs-on: ubuntu-latest", "runs-on: [self-hosted, linux, docker]"), maxRepairs: 0);
        Assert.False(custom.GetProperty("approvable").GetBoolean());
        Assert.Contains(custom.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "ACTIONLINT_RUNNER_LABEL");
    }
}

// Labels the pipeline did not demand: given by the person for one conversion, or allowed by the operator for all of them. The product tells the model, hands them to the real actionlint, stores them with the plan
// and uses them again when a person approves. A label that is not a plain name is refused before anything is read or sent.
public sealed class ExtraRunnerLabelsApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    protected override string OperatorRunnerLabels => "orion-build";

    private static string OnSelfHosted(string labels) => GoodWorkflow.Replace("runs-on: ubuntu-latest", $"runs-on: [{labels}]");

    private async Task<string> ModelRequestsAsync() => await Control.GetStringAsync(Origin + "/_sim/requests");

    [RequiresAnalysersFact]
    public async Task Labels_the_person_gives_and_the_operator_allows_are_accepted_told_to_the_model_stored_and_used_again_at_approval()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), OnSelfHosted("self-hosted, linux, gpu, orion-build"), runnerLabels: ["gpu"]);

        Assert.True(conversion.GetProperty("approvable").GetBoolean(), conversion.GetProperty("findings").ToString());
        var plan = conversion.GetProperty("plan");
        Assert.Empty(plan.GetProperty("runnerLabels").EnumerateArray()); // the pipeline demanded nothing
        Assert.Equal(["orion-build", "gpu"], plan.GetProperty("extraRunnerLabels").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains(conversion.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "SELF_HOSTED_RUNNER" && f.GetProperty("severity").GetString() == "Warning");
        Assert.DoesNotContain(conversion.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "ACTIONLINT_RUNNER_LABEL");
        Assert.Contains(plan.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("allowed by the operator for every conversion: orion-build."));
        Assert.Contains(plan.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("given for this conversion: gpu."));

        // the model was told, apart from anything the pipeline demanded
        var sent = await ModelRequestsAsync();
        Assert.Contains("ADDITIONAL RUNNER LABELS the destination's runners are said to have", sent);
        Assert.Contains("not demanded by the pipeline): orion-build, gpu.", sent);
        Assert.DoesNotContain("self-hosted pools demand", sent);

        // kept with the proposal
        var id = conversion.GetProperty("proposalId").GetGuid();
        var row = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id));
        using var evidence = JsonDocument.Parse(row.Evidence);
        var stored = evidence.RootElement.GetProperty("plan").EnumerateObject().Single(p => p.Name.Equals("extraRunnerLabels", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Equal(["orion-build", "gpu"], stored.EnumerateArray().Select(l => l.GetString()));

        // approving gates the kept text again with the kept labels: it still passes
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Runners with these labels exist." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [RequiresAnalysersFact]
    public async Task A_label_nobody_gave_is_still_refused_by_the_real_actionlint_and_an_operator_label_is_for_every_conversion()
    {
        var ai = await AiConnectionAsync(consent: true);
        // nothing given for this conversion: only the operator's label is known
        var refused = await ConvertAsync(ai, OnSelfHosted("self-hosted, linux, gpu"), maxRepairs: 0);
        Assert.False(refused.GetProperty("approvable").GetBoolean());
        var blocker = Assert.Single(refused.GetProperty("findings").EnumerateArray(), f => f.GetProperty("severity").GetString() == "Blocker" && f.GetProperty("code").GetString() == "ACTIONLINT_RUNNER_LABEL");
        Assert.Contains("gpu", blocker.GetProperty("message").GetString());
        Assert.DoesNotContain("orion-build", blocker.GetProperty("message").GetString());

        var allowed = await ConvertAsync(ai, OnSelfHosted("self-hosted, linux, orion-build"), maxRepairs: 0);
        Assert.True(allowed.GetProperty("approvable").GetBoolean(), allowed.GetProperty("findings").ToString());
        Assert.Empty(allowed.GetProperty("plan").GetProperty("runnerLabels").EnumerateArray());
    }

    [RequiresAnalysersFact]
    public async Task A_label_that_is_not_a_plain_name_or_too_many_are_refused_before_anything_is_read_or_sent()
    {
        var ai = await AiConnectionAsync(consent: true);
        await ScriptAsync(new { content = Fenced(GoodWorkflow) });
        var before = await ModelRequestsAsync();
        var kept = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().CountAsync()); // the table is shared by the tests of the run: what matters is that it did not change

        foreach (var bad in new[] { new[] { "Agent.OS -equals Linux" }, new[] { "$(label)" }, new[] { "a b" }, new[] { "ok", "x;rm" }, Enumerable.Range(1, 16).Select(i => $"label{i}").ToArray() })
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/workloads/pipelines/convert", Who.Admin,
                new { sourceConnectionId = Api.SourceConnectionId, projectId = "project", pipelineId = 42, aiConnectionId = ai, runnerLabels = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("InvalidRunnerLabel", (await JsonOf(response)).GetProperty("error").GetString());
        }
        Assert.Equal(before, await ModelRequestsAsync()); // the model was not asked
        Assert.Equal(kept, await InDbAsync(db => db.WorkflowProposals.AsNoTracking().CountAsync()));
    }
}
