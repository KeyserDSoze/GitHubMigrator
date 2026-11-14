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
