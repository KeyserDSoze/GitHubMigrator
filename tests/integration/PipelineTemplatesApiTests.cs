using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// A pipeline that includes templates, converted on the real API: with the person's say-so the templates are read from where the pipeline points (its own repository, and a git repository
// resource of the same organization), redacted, sent to the model apart from the pipeline, their tasks accounted for by the real gates, and what was sent is kept with the proposal as names and
// hashes. Without it nothing is read and the model is told it cannot see them, as it always was.
public sealed class PipelineTemplatesApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    private const string Secret = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    protected override string AzureYaml =>
        "resources:\n  repositories:\n  - repository: shared\n    type: git\n    name: Platform/shared-templates\n    ref: refs/heads/main\n" +
        "variables:\n- template: vars/local.yml\n- template: jobs/build.yml@shared\n";

    protected override IReadOnlyDictionary<string, string> AzureTemplateFiles => new Dictionary<string, string>
    {
        ["project|source-git|/vars/local.yml"] = $"variables:\n- name: region\n  value: westeurope\n- name: leaked\n  value: {Secret}\n",
        ["Platform|shared-templates|/jobs/build.yml"] = "jobs:\n- job: quality\n  steps:\n  - task: DotNetCoreCLI@2\n    inputs:\n      command: test\n  - template: steps/notify.yml\n",
        ["Platform|shared-templates|/jobs/steps/notify.yml"] = "steps:\n- script: echo notify the team\n"
    };

    private static string Accounting(string workflow, string tasks) => workflow.Replace("converted=UseDotNet@2 manual=none", $"converted={tasks} manual=none");

    private async Task<string> ModelRequestsAsync() => await Control.GetStringAsync(Origin + "/_sim/requests");

    private string[] AzureFileReads() => Api.Calls.Where(c => c.Url.AbsolutePath.EndsWith("/items", StringComparison.Ordinal))
        .Select(c => Uri.UnescapeDataString(c.Url.AbsolutePath) + Uri.UnescapeDataString(c.Url.Query).Replace("&includeContent=true", "").Replace("&$format=json", "")).ToArray();

    [RequiresAnalysersFact]
    public async Task With_the_persons_say_so_the_templates_are_read_redacted_sent_apart_and_their_tasks_accounted_for_and_the_proposal_keeps_names_and_hashes_only()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), Accounting(GoodWorkflow, "UseDotNet@2,DotNetCoreCLI@2"), includeTemplates: true);

        Assert.True(conversion.GetProperty("approvable").GetBoolean(), conversion.GetProperty("findings").ToString());
        var provenance = conversion.GetProperty("provenance");
        var sent = provenance.GetProperty("templates").EnumerateArray().Select(t => (t.GetProperty("repository").GetString(), t.GetProperty("path").GetString(), t.TryGetProperty("ref", out var r) ? r.GetString() : null)).ToArray();
        Assert.Equal([("self", "/vars/local.yml", (string?)null), ("Platform/shared-templates", "/jobs/build.yml", "refs/heads/main"), ("Platform/shared-templates", "/jobs/steps/notify.yml", "refs/heads/main")], sent);
        Assert.All(provenance.GetProperty("templates").EnumerateArray(), t => Assert.Matches("^[0-9a-f]{64}$", t.GetProperty("sha256").GetString()!));
        Assert.Empty(provenance.GetProperty("unresolvedTemplates").EnumerateArray());

        // only the files the pipeline pointed at were read, from where it pointed (and the pipeline's own file)
        var reads = AzureFileReads().Where(r => !r.Contains("path=/azure-pipelines.yml")).ToArray(); // the pipeline's own file is read to analyse and to convert it
        Assert.Contains(reads, r => r.StartsWith("/az-org/project/_apis/git/repositories/source-git/items?path=/vars/local.yml"));
        Assert.Contains(reads, r => r.StartsWith("/az-org/Platform/_apis/git/repositories/shared-templates/items?path=/jobs/build.yml") && r.Contains("versionDescriptor.version=main&versionDescriptor.versionType=branch"));
        Assert.Contains(reads, r => r.StartsWith("/az-org/Platform/_apis/git/repositories/shared-templates/items?path=/jobs/steps/notify.yml"));
        Assert.Equal(3, reads.Length);

        // the model saw them as data apart from the pipeline, and never saw what was redacted
        var request = await ModelRequestsAsync();
        Assert.Contains("[[TEMPLATE-", request);
        Assert.Contains("DotNetCoreCLI@2", request);
        Assert.Contains("echo notify the team", request);
        Assert.Contains("westeurope", request);
        Assert.DoesNotContain(Secret, request);
        Assert.Contains("TEMPLATES shown after the source", request);
        Assert.DoesNotContain("TEMPLATES you cannot see", request);
        Assert.Contains("<REDACTED:", request);

        // kept with the proposal: names, sizes and hashes, never the text of a template
        var id = conversion.GetProperty("proposalId").GetGuid();
        var row = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.Contains("/jobs/build.yml", row.Evidence);
        Assert.DoesNotContain("echo notify the team", row.Evidence);
        Assert.DoesNotContain(Secret, row.Evidence);
        Assert.DoesNotContain(Secret, conversion.GetRawText());

        // approving gates the kept text against the same tasks (the template's included), and so does deriving the triggers
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Read with its templates." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var derived = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{id}/derive-triggers", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, derived.StatusCode);
    }

    [RequiresAnalysersFact]
    public async Task A_task_that_only_a_template_uses_has_to_be_accounted_for_by_the_real_gates()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), GoodWorkflow, maxRepairs: 0, includeTemplates: true);

        Assert.False(conversion.GetProperty("approvable").GetBoolean());
        var finding = Assert.Single(conversion.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "TASK_NOT_ACCOUNTED");
        Assert.Contains("DotNetCoreCLI@2", finding.GetProperty("message").GetString());
    }

    [RequiresAnalysersFact]
    public async Task Without_the_persons_say_so_no_template_is_read_and_the_model_is_told_it_cannot_see_them()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), GoodWorkflow);

        Assert.True(conversion.GetProperty("approvable").GetBoolean(), conversion.GetProperty("findings").ToString());
        Assert.Empty(conversion.GetProperty("provenance").GetProperty("templates").EnumerateArray());
        Assert.All(AzureFileReads(), r => Assert.Contains("path=/azure-pipelines.yml", r)); // the pipeline's own file and nothing else
        var request = await ModelRequestsAsync();
        Assert.DoesNotContain("[[TEMPLATE-", request);
        Assert.Contains("TEMPLATES you cannot see", request);
        Assert.DoesNotContain("echo notify the team", request);
    }
}

// The same pipeline, but the shared repository does not have the file: the local template is read, the other is reported, and the conversion goes on without it.
public sealed class PipelineTemplatesUnreadableApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    protected override string AzureYaml =>
        "resources:\n  repositories:\n  - repository: shared\n    type: git\n    name: Platform/shared-templates\n" +
        "variables:\n- template: vars/local.yml\n- template: jobs/build.yml@shared\n- template: ${{ variables.extra }}.yml\n- template: ../../outside.yml\n";

    protected override IReadOnlyDictionary<string, string> AzureTemplateFiles => new Dictionary<string, string> { ["project|source-git|/vars/local.yml"] = "variables:\n- name: region\n  value: westeurope\n" };

    [RequiresAnalysersFact]
    public async Task A_template_that_cannot_be_read_or_must_not_be_is_reported_with_its_reason_and_the_model_is_told_it_cannot_see_it()
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), GoodWorkflow, includeTemplates: true);

        Assert.True(conversion.GetProperty("approvable").GetBoolean(), conversion.GetProperty("findings").ToString());
        var provenance = conversion.GetProperty("provenance");
        Assert.Equal(["/vars/local.yml"], provenance.GetProperty("templates").EnumerateArray().Select(t => t.GetProperty("path").GetString()));
        Assert.Equal([("jobs/build.yml@shared", "NotReadable"), ("${{ variables.extra }}.yml", "ExpressionPath"), ("../../outside.yml", "EscapesRepository")],
            provenance.GetProperty("unresolvedTemplates").EnumerateArray().Select(u => (u.GetProperty("reference").GetString(), u.GetProperty("reason").GetString())));
        var request = await Control.GetStringAsync(Origin + "/_sim/requests");
        Assert.Contains("jobs/build.yml@shared (NotReadable)", request);
        Assert.Contains("../../outside.yml (EscapesRepository)", request);
        // nothing outside the repository or computed was asked for
        Assert.DoesNotContain(Api.Calls, c => c.Url.AbsolutePath.Contains("outside"));
    }
}
