using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Infrastructure.WorkflowTools;
using static GitHubMigrator.Domain.Tests.WorkflowGates.WorkflowSamples;

namespace GitHubMigrator.Domain.Tests.WorkflowGates;

// Where the analysers live: GITHUBMIGRATOR_ACTIONLINT / GITHUBMIGRATOR_ZIZMOR, otherwise PATH. Without them the
// real-tool tests are skipped locally; CI sets GITHUBMIGRATOR_REQUIRE_TOOLS=true so a missing tool fails the build
// instead of silently removing the safety net.
public static class WorkflowTools
{
    public static string? Actionlint =>
        ProcessWorkflowToolRunner.Locate("actionlint", Environment.GetEnvironmentVariable("GITHUBMIGRATOR_ACTIONLINT"));
    public static string? Zizmor =>
        ProcessWorkflowToolRunner.Locate("zizmor", Environment.GetEnvironmentVariable("GITHUBMIGRATOR_ZIZMOR"));
    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUBMIGRATOR_REQUIRE_TOOLS"), "true", StringComparison.OrdinalIgnoreCase);
    public static bool Available => Actionlint is not null && Zizmor is not null;

    public static WorkflowGatePipeline RealPipeline(WorkflowGatePolicy? policy = null, IEnumerable<IWorkflowGate>? extraGates = null) => WorkflowGatePipeline.Create(
        ProcessWorkflowToolRunner.Actionlint(Actionlint ?? throw new InvalidOperationException("actionlint not installed")),
        ProcessWorkflowToolRunner.Zizmor(Zizmor ?? throw new InvalidOperationException("zizmor not installed")), policy, extraGates);
}

public sealed class RequiresToolsFactAttribute : FactAttribute
{
    public RequiresToolsFactAttribute()
    {
        if (!WorkflowTools.Available && !WorkflowTools.Required)
            Skip = "actionlint/zizmor not installed (set GITHUBMIGRATOR_ACTIONLINT / GITHUBMIGRATOR_ZIZMOR or put them on PATH).";
    }
}

internal sealed class FakeToolRunner(string tool, ToolRun run) : IWorkflowToolRunner
{
    public int Calls { get; private set; }
    public string Tool => tool;
    public Task<ToolRun> RunAsync(string workflowYaml, CancellationToken ct) { Calls++; return Task.FromResult(run); }
}

internal sealed class CrashingToolRunner(string tool) : IWorkflowToolRunner
{
    public string Tool => tool;
    public Task<ToolRun> RunAsync(string workflowYaml, CancellationToken ct) => throw new InvalidOperationException("boom");
}

public sealed class ExternalToolGateMappingTests
{
    private static WorkflowGateContext Context(WorkflowGatePolicy? policy = null) =>
        new(Good, null, WorkflowPlanContext.Empty, policy ?? WorkflowGatePolicy.Default);

    private static Task<GateResult> Actionlint(ToolRun run, WorkflowGatePolicy? policy = null) =>
        new ActionlintGate(new FakeToolRunner("actionlint", run)).EvaluateAsync(Context(policy), default);

    private static Task<GateResult> Zizmor(ToolRun run, WorkflowGatePolicy? policy = null) =>
        new ZizmorGate(new FakeToolRunner("zizmor", run)).EvaluateAsync(Context(policy), default);

    private static string ZizmorFinding(string ident, string severity, int row, bool ignored = false) => """
        {"ident":"@IDENT@","desc":"description of @IDENT@","determinations":{"confidence":"High","severity":"@SEV@","persona":"Regular"},
         "locations":[{"concrete":{"location":{"start_point":{"row":@ROW@,"column":4}}}}],"ignored":@IGN@}
        """.Replace("@IDENT@", ident).Replace("@SEV@", severity).Replace("@ROW@", row.ToString()).Replace("@IGN@", ignored ? "true" : "false");

    [Fact]
    public async Task Actionlint_findings_become_blockers_with_kind_code_and_line()
    {
        var result = await Actionlint(new(true,
            """[{"message":"label \"ubuntu-latst\" is unknown.\nsecond line","line":9,"column":14,"kind":"runner-label"},{"message":"bad needs","line":17,"column":3,"kind":"job-needs"}]""",
            null, 1));
        Assert.Equal(GateStatus.Failed, result.Status);
        Assert.Collection(result.Findings,
            f => { Assert.Equal("ACTIONLINT_RUNNER_LABEL", f.Code); Assert.Equal(9, f.Line); Assert.Equal(GateSeverity.Blocker, f.Severity); Assert.DoesNotContain('\n', f.Message); },
            f => { Assert.Equal("ACTIONLINT_JOB_NEEDS", f.Code); Assert.Equal(17, f.Line); });
    }

    [Fact]
    public async Task A_clean_actionlint_report_passes_and_policy_may_ignore_a_kind()
    {
        Assert.Equal(GateStatus.Passed, (await Actionlint(new(true, "[]", null, 0))).Status);
        var policy = new WorkflowGatePolicy { IgnoredActionlintKinds = new HashSet<string> { "shellcheck" } };
        var result = await Actionlint(new(true, """[{"message":"SC2086","line":3,"kind":"shellcheck"}]""", null, 1), policy);
        Assert.Equal(GateStatus.Passed, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Tool_messages_are_bounded_because_they_come_from_analysing_untrusted_input()
    {
        var message = new string('x', 5000) + "\\nsecond";
        var result = await Actionlint(new(true, $$"""[{"message":"{{message}}","line":1,"kind":"expression"}]""", null, 1));
        Assert.True(result.Findings.Single().Message.Length <= 300);
    }

    [Theory]
    [InlineData(false, "", 0)]            // could not run
    [InlineData(true, "", 0)]             // silence is not a clean bill of health
    [InlineData(true, "   \n", 1)]
    [InlineData(true, "[]", 3)]           // fatal exit code, even with plausible output
    [InlineData(true, "[]", 2)]
    [InlineData(true, "not json", 1)]
    [InlineData(true, "{\"kind\":\"x\"}", 1)]
    public async Task Actionlint_that_did_not_analyse_the_file_is_not_a_pass(bool completed, string output, int exitCode)
    {
        var result = await Actionlint(new(completed, output, completed ? null : "could not start", exitCode));
        Assert.Equal(GateStatus.NotRun, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Zizmor_severity_maps_to_policy_and_rows_are_converted_to_one_based_lines()
    {
        var json = "[" + string.Join(",", ZizmorFinding("unpinned-uses", "High", 6), ZizmorFinding("excessive-permissions", "Medium", 0),
            ZizmorFinding("obfuscation", "Low", 3), ZizmorFinding("anonymous-definition", "Informational", 1)) + "]";
        var result = await Zizmor(new(true, json, null, 14));
        var byCode = result.Findings.ToDictionary(f => f.Code);
        Assert.Equal(GateSeverity.Blocker, byCode["ZIZMOR_UNPINNED_USES"].Severity);
        Assert.Equal(7, byCode["ZIZMOR_UNPINNED_USES"].Line);
        Assert.Equal(GateSeverity.Warning, byCode["ZIZMOR_EXCESSIVE_PERMISSIONS"].Severity);
        Assert.Equal(1, byCode["ZIZMOR_EXCESSIVE_PERMISSIONS"].Line);
        Assert.Equal(GateSeverity.Info, byCode["ZIZMOR_OBFUSCATION"].Severity);
        Assert.Equal(GateSeverity.Info, byCode["ZIZMOR_ANONYMOUS_DEFINITION"].Severity);
        Assert.Equal(GateStatus.Failed, result.Status);

        var strict = new WorkflowGatePolicy { ZizmorMediumIs = GateSeverity.Blocker };
        var strictResult = await Zizmor(new(true, "[" + ZizmorFinding("excessive-permissions", "Medium", 0) + "]", null, 13), strict);
        Assert.Equal(GateStatus.Failed, strictResult.Status);
    }

    [Fact]
    public async Task A_finding_muted_in_the_workflow_itself_still_counts()
    {
        var result = await Zizmor(new(true, "[" + ZizmorFinding("template-injection", "High", 9, ignored: true) + "]", null, 14));
        Assert.Equal(GateStatus.Failed, result.Status);
        Assert.Equal("ZIZMOR_TEMPLATE_INJECTION", result.Findings.Single().Code);
    }

    [Fact]
    public async Task Only_the_operator_policy_can_ignore_a_zizmor_audit()
    {
        var policy = new WorkflowGatePolicy { IgnoredZizmorIdents = new HashSet<string> { "obfuscation" } };
        var result = await Zizmor(new(true, "[" + ZizmorFinding("obfuscation", "High", 1) + "]", null, 14), policy);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(0, "[]", GateStatus.Passed)]
    [InlineData(10, "[]", GateStatus.Passed)]
    [InlineData(14, "[]", GateStatus.Passed)]
    [InlineData(1, "[]", GateStatus.NotRun)]
    [InlineData(3, "", GateStatus.NotRun)]       // "no auditable input", e.g. YAML zizmor cannot parse
    [InlineData(0, "", GateStatus.NotRun)]
    [InlineData(15, "[]", GateStatus.NotRun)]
    [InlineData(0, "{}", GateStatus.NotRun)]
    [InlineData(0, "garbage", GateStatus.NotRun)]
    public async Task Zizmor_exit_codes_and_output_decide_whether_it_actually_ran(int exitCode, string output, GateStatus expected) =>
        Assert.Equal(expected, (await Zizmor(new(true, output, null, exitCode))).Status);
}

public sealed class WorkflowGatePipelineToolTests
{
    private static readonly WorkflowPlanContext Plan = WorkflowPlanContext.Of(["NUGET_API_KEY"], []);

    private static FakeToolRunner Clean(string tool) => new(tool, new(true, "[]", null, 0));

    [Fact]
    public async Task A_workflow_is_approvable_only_when_every_required_gate_ran_and_none_blocked()
    {
        var report = await WorkflowGatePipeline.Create(Clean("actionlint"), Clean("zizmor")).RunAsync(Good, Plan, default);
        Assert.True(report.AllRequiredGatesRan);
        Assert.False(report.HasBlockers);
        Assert.True(report.Approvable);
        Assert.Equal(["structure", "policy", "suppressions", "actionlint", "zizmor"], report.RequiredGates);
        Assert.Equal(WorkflowGatePolicy.Default.Version, report.PolicyVersion);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_missing_analyser_makes_the_workflow_unapprovable(bool noActionlint, bool noZizmor)
    {
        var report = await WorkflowGatePipeline.Create(noActionlint ? null : Clean("actionlint"), noZizmor ? null : Clean("zizmor"))
            .RunAsync(Good, Plan, default);
        Assert.False(report.HasBlockers);
        Assert.False(report.AllRequiredGatesRan);
        Assert.False(report.Approvable);
        Assert.Equal(noActionlint, report.Gates.Single(g => g.Gate == "actionlint").Status == GateStatus.NotRun);
        Assert.Equal(noZizmor, report.Gates.Single(g => g.Gate == "zizmor").Status == GateStatus.NotRun);
    }

    [Fact]
    public async Task A_crashing_gate_fails_closed_instead_of_passing()
    {
        var pipeline = new WorkflowGatePipeline(
            [new WorkflowPolicyGate(), new LinterSuppressionGate(), new ActionlintGate(new CrashingToolRunner("actionlint")), new ZizmorGate(Clean("zizmor"))]);
        var report = await pipeline.RunAsync(Good, Plan, default);
        var actionlint = report.Gates.Single(g => g.Gate == "actionlint");
        Assert.Equal(GateStatus.NotRun, actionlint.Status);
        Assert.Contains("InvalidOperationException", actionlint.Detail);
        Assert.DoesNotContain("boom", actionlint.Detail);
        Assert.False(report.Approvable);
    }

    [Fact]
    public async Task A_tool_finding_blocks_even_when_policy_is_clean_and_warnings_do_not()
    {
        var finding = new FakeToolRunner("zizmor", new(true,
            """[{"ident":"unpinned-uses","desc":"unpinned","determinations":{"severity":"High"},"locations":[]}]""", null, 14));
        var blocked = await WorkflowGatePipeline.Create(Clean("actionlint"), finding).RunAsync(Good, Plan, default);
        Assert.True(blocked.Has("ZIZMOR_UNPINNED_USES"));
        Assert.False(blocked.Approvable);

        var medium = new FakeToolRunner("zizmor", new(true,
            """[{"ident":"excessive-permissions","desc":"broad","determinations":{"severity":"Medium"},"locations":[]}]""", null, 13));
        var warned = await WorkflowGatePipeline.Create(Clean("actionlint"), medium).RunAsync(Good, Plan, default);
        Assert.True(warned.Has("ZIZMOR_EXCESSIVE_PERMISSIONS"));
        Assert.True(warned.Approvable);
    }

    [Fact]
    public async Task A_structurally_broken_workflow_never_reaches_the_external_tools_but_suppressions_are_still_reported()
    {
        var actionlint = Clean("actionlint"); var zizmor = Clean("zizmor");
        var report = await WorkflowGatePipeline.Create(actionlint, zizmor).RunAsync(BrokenYaml + "# zizmor: ignore[all]\n", Plan, default);
        Assert.Equal(0, actionlint.Calls);
        Assert.Equal(0, zizmor.Calls);
        Assert.Equal(GateStatus.NotRun, report.Gates.Single(g => g.Gate == "actionlint").Status);
        Assert.True(report.Has("YAML_PARSE_ERROR"));
        Assert.True(report.Has("LINTER_SUPPRESSION_COMMENT"));
        Assert.False(report.Approvable);
    }

    [Fact]
    public async Task The_operator_may_relax_the_required_gates_but_the_default_requires_both()
    {
        var relaxed = new WorkflowGatePolicy { RequiredExternalGates = new HashSet<string>() };
        var report = await WorkflowGatePipeline.Create(null, null, relaxed).RunAsync(Good, Plan, default);
        Assert.True(report.Approvable);
        Assert.False((await WorkflowGatePipeline.Create(null, null).RunAsync(Good, Plan, default)).Approvable);
    }
}

[Collection("workflow-tool-processes")]
public sealed class ProcessWorkflowToolRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "runner-test-" + Guid.NewGuid().ToString("N"));

    public ProcessWorkflowToolRunnerTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    private string Script(string body)
    {
        var path = Path.Combine(_directory, "tool-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task The_exit_code_and_output_are_returned_and_the_tool_gets_the_documented_arguments()
    {
        var run = await ProcessWorkflowToolRunner.Actionlint(Script("printf '%s|' \"$@\"; exit 1")).RunAsync(Good, default);
        Assert.True(run.Completed);
        Assert.Equal(1, run.ExitCode);
        Assert.StartsWith("-format|{{json .}}|-no-color|", run.Output);
        Assert.EndsWith(Path.Combine(".github", "workflows", "workflow.yml") + "|", run.Output);

        var zizmor = await ProcessWorkflowToolRunner.Zizmor(Script("printf '%s|' \"$@\"; exit 13")).RunAsync(Good, default);
        Assert.Equal(13, zizmor.ExitCode);
        Assert.StartsWith("--offline|--no-progress|--no-config|--no-ignores|--persona|auditor|--format|json|", zizmor.Output);
    }

    [Fact]
    public async Task The_tool_sees_only_the_workflow_in_a_private_directory_with_an_empty_environment_which_is_removed_afterwards()
    {
        Environment.SetEnvironmentVariable("GITHUBMIGRATOR_TEST_SECRET", "must-not-leak");
        try
        {
            var script = Script("pwd; find . -type f; echo \"secret=[$GITHUBMIGRATOR_TEST_SECRET]\"; cat .github/workflows/workflow.yml | head -1");
            var run = await ProcessWorkflowToolRunner.Actionlint(script).RunAsync(Good, default);
            var lines = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("./.github/workflows/workflow.yml", lines[1]);
            Assert.Equal(4, lines.Length);
            Assert.Equal("secret=[]", lines[2]);
            Assert.StartsWith("name: orion-api build", lines[3]);
            Assert.False(Directory.Exists(lines[0]), "the temporary directory must be deleted");
        }
        finally { Environment.SetEnvironmentVariable("GITHUBMIGRATOR_TEST_SECRET", null); }
    }

    [Fact]
    public async Task The_tool_cannot_read_the_parents_standard_input()
    {
        var run = await ProcessWorkflowToolRunner.Actionlint(Script("if read -r line; then echo \"got:$line\"; else echo eof; fi")).RunAsync(Good, default);
        Assert.Equal("eof", run.Output.Trim());
    }

    [Fact]
    public async Task A_tool_that_hangs_is_killed_at_the_deadline_and_reported_as_not_run()
    {
        var script = Script("sleep 30 & wait");
        var started = DateTime.UtcNow;
        var run = await ProcessWorkflowToolRunner.Actionlint(script, TimeSpan.FromMilliseconds(600)).RunAsync(Good, default);
        Assert.False(run.Completed);
        Assert.Contains("time limit", run.Detail);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_tool_that_floods_its_output_is_cut_off()
    {
        var run = await ProcessWorkflowToolRunner.Actionlint(Script("yes xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")).RunAsync(Good, default);
        Assert.False(run.Completed);
        Assert.Contains("more output", run.Detail);
    }

    [Fact]
    public async Task A_missing_or_unstartable_tool_is_not_run_and_locating_it_is_explicit()
    {
        var run = await ProcessWorkflowToolRunner.Actionlint(Path.Combine(_directory, "does-not-exist")).RunAsync(Good, default);
        Assert.False(run.Completed);
        Assert.Contains("could not be started", run.Detail);

        Assert.Null(ProcessWorkflowToolRunner.Locate("actionlint", Path.Combine(_directory, "nope")));
        var present = Script("exit 0");
        Assert.Equal(present, ProcessWorkflowToolRunner.Locate("actionlint", present));
        Assert.Null(ProcessWorkflowToolRunner.Locate("definitely-not-a-real-tool-name", null));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_tool_timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessWorkflowToolRunner.Actionlint(Script("sleep 30")).RunAsync(Good, cts.Token));
    }
}

[Collection("workflow-tool-processes")]
public sealed class RealToolGateTests
{
    private static async Task<WorkflowGateReport> RunAsync(string yaml, WorkflowPlanContext? plan = null, WorkflowGatePolicy? policy = null) =>
        await WorkflowTools.RealPipeline(policy).RunAsync(yaml, plan ?? WorkflowPlanContext.Of(["NUGET_API_KEY", "DEPLOY_PASSWORD"], ["REGION"]), default);

    [Fact]
    public void The_analysers_are_installed_where_the_build_requires_them()
    {
        if (!WorkflowTools.Required) return;
        Assert.NotNull(WorkflowTools.Actionlint);
        Assert.NotNull(WorkflowTools.Zizmor);
    }

    [RequiresToolsFact]
    public async Task A_pinned_least_privilege_manual_workflow_is_approvable_with_the_real_analysers()
    {
        var report = await RunAsync(Good);
        Assert.True(report.Approvable, string.Join("; ", report.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code + ":" + f.Message)));
        Assert.All(report.Gates, g => Assert.Equal(GateStatus.Passed, g.Status));
    }

    [RequiresToolsFact]
    public async Task What_actionlint_catches_that_the_policy_gate_does_not()
    {
        const string yaml = """
            name: x
            on:
              workflow_dispatch:
            permissions:
              contents: read
            jobs:
              build:
                runs-on: ubuntu-latst
                steps:
                  - run: echo ${{ steps.nothing.outputs.x }}
              deploy:
                needs: nonexistent-job
                runs-on: ubuntu-latest
                steps:
                  - run: echo hi
            """;
        var report = await RunAsync(yaml);
        Assert.True(report.Has("ACTIONLINT_RUNNER_LABEL"));
        Assert.True(report.Has("ACTIONLINT_JOB_NEEDS"));
        Assert.True(report.Has("ACTIONLINT_EXPRESSION"));
        Assert.False(report.Approvable);
        Assert.All(report.Findings.Where(f => f.Gate == "actionlint"), f => Assert.NotNull(f.Line));
    }

    [RequiresToolsFact]
    public async Task What_zizmor_catches_that_actionlint_does_not()
    {
        var unpinned = await RunAsync(UnpinnedMain);
        Assert.True(unpinned.Has("ZIZMOR_UNPINNED_USES"));
        Assert.False(unpinned.Has("ACTIONLINT_EXPRESSION"));
        Assert.True((await RunAsync(PullRequestTarget)).Has("ZIZMOR_DANGEROUS_TRIGGERS"));
        Assert.True((await RunAsync(ScriptInjection)).Has("ZIZMOR_TEMPLATE_INJECTION"));
        Assert.True((await RunAsync(WriteAll)).Has("ZIZMOR_EXCESSIVE_PERMISSIONS"));
    }

    [RequiresToolsFact]
    public async Task A_suppression_comment_does_not_silence_the_real_zizmor_and_is_blocked_on_its_own()
    {
        var report = await RunAsync(Suppressed);
        Assert.True(report.Has("ZIZMOR_TEMPLATE_INJECTION"), string.Join(",", report.Findings.Select(f => f.Code)));
        Assert.True(report.Has("LINTER_SUPPRESSION_COMMENT"));
        Assert.False(report.Approvable);
    }

    [RequiresToolsFact]
    public async Task Hostile_workflows_are_never_approvable()
    {
        foreach (var (name, yaml) in new[]
        {
            ("pipe-to-shell", PipeToShell), ("pull_request_target", PullRequestTarget), ("unpinned", UnpinnedMain),
            ("literal-secret", LiteralSecret), ("exfiltration", SecretExfiltration), ("injection", ScriptInjection),
            ("placeholder", RedactionPlaceholderKept), ("write-all", WriteAll), ("no-permissions", NoPermissions)
        })
        {
            var report = await RunAsync(yaml);
            Assert.False(report.Approvable, name);
            Assert.True(report.AllRequiredGatesRan, name + ": the tools must have run on these parseable workflows");
        }
    }

    [RequiresToolsFact]
    public async Task Yaml_the_tools_cannot_parse_is_stopped_by_the_structure_gate_and_never_reads_as_clean()
    {
        var report = await RunAsync(BrokenYaml);
        Assert.True(report.Has("YAML_PARSE_ERROR"));
        Assert.False(report.Approvable);

        // zizmor alone, handed the same broken file, exits with "no auditable input" and must be NotRun, not Passed
        var runner = ProcessWorkflowToolRunner.Zizmor(WorkflowTools.Zizmor!);
        var result = await new ZizmorGate(runner).EvaluateAsync(
            new(BrokenYaml, null, WorkflowPlanContext.Empty, WorkflowGatePolicy.Default), default);
        Assert.Equal(GateStatus.NotRun, result.Status);
    }

    // The product's TODO scaffold (WorkloadPreviews) is a placeholder, not a conversion. Its constant 'if: ${{ false }}'
    // is rejected by actionlint, so it can never be mistaken for an approvable workflow.
    [RequiresToolsFact]
    public async Task The_todo_scaffold_is_never_approvable()
    {
        const string scaffold = """
            # MIGRATION DRAFT — F-009. Review and replace TODOs before enabling.
            name: 'Migrated Azure pipeline 42'
            on:
              workflow_dispatch:
            permissions:
              contents: read
            jobs:
              migration-review:
                if: ${{ false }} # deliberate safety gate
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - name: Manual migration required
                    run: echo 'TODO review Azure Pipelines tasks, agents, conditions and service connections'
                  - name: 'REVIEW 1: UseDotNet@2'
                    run: echo 'TODO translate Azure task task 1'
            """;
        var report = await RunAsync(scaffold);
        Assert.True(report.Has("ACTIONLINT_IF_COND"));
        Assert.False(report.Approvable);
    }
}
