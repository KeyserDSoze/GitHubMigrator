using System.Text.RegularExpressions;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using Xunit.Abstractions;

namespace GitHubMigrator.Domain.Tests.WorkflowGates;

// Ten Orion pipelines converted by hand, and what has to hold for each conversion. These workflows have been linted
// and gate-checked; they have NOT been executed on GitHub, and nothing here claims behavioural equivalence.
public sealed class OrionGoldenCorpusTests(ITestOutputHelper output)
{
    private static readonly Regex Secret = new(@"\bsecrets\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex Variable = new(@"\bvars\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex Expression = new(@"\$\{\{(.*?)\}\}", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly string[] Levels = ["full", "partial", "manual"];

    public static IEnumerable<object[]> Names() => OrionCorpus.Names();

    private static string[] Referenced(Regex regex, string yaml) => Expression.Matches(yaml).SelectMany(e => regex.Matches(e.Groups[1].Value))
        .Select(m => m.Groups[1].Value).Where(n => n != "GITHUB_TOKEN").Distinct().Order(StringComparer.Ordinal).ToArray();

    private static string[] Blockers(WorkflowGateReport report) =>
        report.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Gate + ":" + f.Code).Distinct().Order().ToArray();

    private static void AssertWarningsAreAccepted(OrionCase c, WorkflowGateReport report)
    {
        var accepted = c.Sidecar.AcceptedWarnings.Select(w => w.Code).ToHashSet();
        var warnings = report.Findings.Where(f => f.Severity == GateSeverity.Warning).Select(f => f.Code).Distinct().ToHashSet();
        Assert.True(warnings.IsSubsetOf(accepted), $"{c.Name}: unaccepted warnings [{string.Join(", ", warnings.Except(accepted))}]");
    }

    [Fact]
    public void The_corpus_covers_every_yaml_pipeline_of_the_estate()
    {
        var ids = OrionCorpus.Load().Select(c => c.Sidecar.PipelineId).Order().ToArray();
        Assert.Equal([2001, 2002, 2003, 2004, 2005, 2007, 3001, 3002, 3003, 4001], ids);
        Assert.All(OrionCorpus.Load(), c => Assert.StartsWith(c.Sidecar.PipelineId + "-", c.Name));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task The_golden_workflow_passes_the_policy_gates_without_unaccepted_warnings(string name)
    {
        var c = OrionCorpus.Get(name);
        var report = await c.PolicyGates().RunAsync(c.GoldenYaml, c.Plan(), default);
        Assert.True(report.Gates.All(g => g.Status == GateStatus.Passed), name + ": " + string.Join("; ", report.Findings.Select(f => f.Code + "@" + f.Line + " " + f.Message)));
        Assert.Empty(Blockers(report));
        AssertWarningsAreAccepted(c, report);
    }

    [RequiresToolsFact]
    public async Task The_golden_workflows_are_approvable_with_the_real_analysers()
    {
        foreach (var c in OrionCorpus.Load())
        {
            var report = await WorkflowTools.RealPipeline(c.Policy(), [new TaskAccountingGate()]).RunAsync(c.GoldenYaml, c.Plan(), default);
            Assert.True(report.Approvable, c.Name + ": " + string.Join("; ", report.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code + "@" + f.Line + " " + f.Message)));
            AssertWarningsAreAccepted(c, report);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_accepted_warning_is_still_raised_and_explained(string name)
    {
        var c = OrionCorpus.Load().Single(x => x.Name == name);
        var report = c.PolicyGates().RunAsync(c.GoldenYaml, c.Plan(), default).Result;
        foreach (var accepted in c.Sidecar.AcceptedWarnings.Where(w => !w.Code.StartsWith("ZIZMOR_") && !w.Code.StartsWith("ACTIONLINT_")))
        {
            Assert.True(report.Has(accepted.Code), $"{name}: accepted warning {accepted.Code} is no longer raised: remove it");
            Assert.False(string.IsNullOrWhiteSpace(accepted.Why));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_secrets_and_variables_the_workflow_uses_are_exactly_the_plan_the_sidecar_declares(string name)
    {
        var c = OrionCorpus.Get(name);
        Assert.Equal(c.Sidecar.Plan.Secrets.Order(StringComparer.Ordinal), Referenced(Secret, c.GoldenYaml));
        Assert.Equal(c.Sidecar.Plan.Variables.Order(StringComparer.Ordinal), Referenced(Variable, c.GoldenYaml));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_task_and_template_of_the_source_is_converted_or_declared_unsupported(string name)
    {
        var c = OrionCorpus.Get(name);
        var structure = AzureYamlTaskAnalyzer.Analyze(c.SourceYaml);
        Assert.False(structure.ParseFailed || structure.Truncated, name + ": the analyser could not read the source");

        var declared = c.Sidecar.TaskMap.Select(t => t.Azure).Concat(c.Sidecar.Unsupported.Select(u => u.Azure)).ToArray();
        Assert.Equal(declared.Length, declared.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var task in structure.Tasks.Where(t => t.Kind == "task").Select(t => t.Identifier).Distinct(StringComparer.OrdinalIgnoreCase))
            Assert.True(declared.Contains(task, StringComparer.OrdinalIgnoreCase), $"{name}: task {task} is neither mapped nor declared unsupported");

        // The analyser does not look inside conditional insertions (${{ if }}:), so the source text is scanned too: a task it
        // misses must still be mapped or declared unsupported (see GATE_FINDINGS.md).
        foreach (var task in Regex.Matches(c.SourceYaml, @"^\s*(?:-\s+)?task:\s*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase))
            Assert.True(declared.Contains(task, StringComparer.OrdinalIgnoreCase), $"{name}: task {task} appears in the source text but is neither mapped nor declared unsupported");

        Assert.Equal(structure.Tasks.Count(t => t.Kind == "template"), c.Sidecar.Templates.Length);
        Assert.All(c.Sidecar.TaskMap, t => Assert.False(string.IsNullOrWhiteSpace(t.Github)));
        Assert.All(c.Sidecar.Unsupported, u => Assert.False(string.IsNullOrWhiteSpace(u.Reason) || string.IsNullOrWhiteSpace(u.Suggestion)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task The_accounting_header_matches_the_sidecar_and_the_task_gate_passes_without_findings(string name)
    {
        var c = OrionCorpus.Get(name);
        var report = await c.PolicyGates().RunAsync(c.GoldenYaml, c.Plan(), default);
        Assert.DoesNotContain(report.Findings, f => f.Gate == TaskAccountingGate.GateName);
        var manual = c.Sidecar.Unsupported.Select(u => u.Azure).Where(a => !a.StartsWith("##", StringComparison.Ordinal)).Order().ToArray();
        var header = Regex.Match(c.GoldenYaml, @"^# azure-tasks: converted=(\S+) manual=(\S+)$", RegexOptions.Multiline);
        Assert.True(header.Success, name + ": no accounting header");
        Assert.Equal(manual, header.Groups[2].Value == "none" ? [] : header.Groups[2].Value.Split(',').Order().ToArray());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Nothing_is_claimed_that_was_not_converted(string name)
    {
        var c = OrionCorpus.Get(name);
        Assert.Contains(c.Sidecar.Equivalence, Levels);
        if (c.Sidecar.Equivalence == "full") Assert.Empty(c.Sidecar.Unsupported);
        if (c.Sidecar.Equivalence == "manual") Assert.NotEmpty(c.Sidecar.Unsupported);
        // an unconverted task must leave a visible, failing marker in the workflow rather than vanish
        foreach (var unsupported in c.Sidecar.Unsupported)
            Assert.True(c.GoldenYaml.Contains("MANUAL PORT REQUIRED", StringComparison.Ordinal) || c.GoldenYaml.Contains(unsupported.Azure, StringComparison.OrdinalIgnoreCase),
                $"{name}: {unsupported.Azure} is unsupported but the workflow leaves no marker for it");
        Assert.NotEmpty(c.Sidecar.OriginalTriggers);
        Assert.False(Regex.IsMatch(c.GoldenYaml, @"(?i)\b(fully|exactly|functionally|strictly) equivalent\b|\bidentical\b") && c.Sidecar.Equivalence != "full",
            name + ": the workflow text must not assert equivalence the sidecar does not claim");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_workflow_is_delivered_manual_only_and_keeps_the_original_triggers_out_of_the_file(string name)
    {
        var c = OrionCorpus.Get(name);
        var parsed = ParsedWorkflow.TryParse(c.GoldenYaml, out _)!;
        Assert.Equal(["workflow_dispatch"], parsed.TriggerNames().Select(t => t.Name).ToArray());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_source_is_redacted_exactly_as_the_sidecar_expects_and_references_survive(string name)
    {
        var c = OrionCorpus.Get(name);
        var result = SourceRedactor.Redact(c.SourceYaml);
        Assert.True(result.SafeToSend);
        Assert.Equal(c.Sidecar.Redaction.Findings, result.Findings.Select(f => f.Kind).ToArray());
        foreach (var reference in Regex.Matches(c.SourceYaml, @"\$\([A-Za-z0-9_.]+\)|\$\{\{[^}]*\}\}").Select(m => m.Value).Distinct())
            Assert.Contains(reference, result.Text);
    }

    [Fact]
    public async Task Summary_of_the_corpus()
    {
        foreach (var c in OrionCorpus.Load())
        {
            var gates = WorkflowTools.Available
                ? (await WorkflowTools.RealPipeline(c.Policy(), [new TaskAccountingGate()]).RunAsync(c.GoldenYaml, c.Plan(), default)).Approvable ? "approvable" : "BLOCKED"
                : "tools missing";
            output.WriteLine($"{c.Name,-34} equivalence={c.Sidecar.Equivalence,-8} tasks mapped={c.Sidecar.TaskMap.Length,2} unsupported={c.Sidecar.Unsupported.Length} " +
                $"secrets={c.Sidecar.Plan.Secrets.Length,2} vars={c.Sidecar.Plan.Variables.Length,2} gates={gates}");
        }
    }
}
