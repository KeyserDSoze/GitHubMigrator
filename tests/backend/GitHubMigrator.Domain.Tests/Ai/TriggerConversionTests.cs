using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain.Tests.WorkflowGates;

namespace GitHubMigrator.Domain.Tests.Ai;

public sealed class AzureTriggerReaderTests
{
    private static string Orion(params string[] parts) =>
        File.ReadAllText(Path.Combine([OrionCorpus.RepositoryRoot(), "tools", "AdoSim", "seed", "files", .. parts]));

    [Fact]
    public void The_orion_web_pipeline_has_a_branch_and_path_trigger_a_pr_trigger_and_a_schedule_that_runs_only_on_change()
    {
        var t = AzureTriggerReader.Read(Orion("orion-web", "azure-pipelines.yml"))!;
        Assert.False(t.Ci.Disabled);
        Assert.False(t.Ci.Defaulted);
        Assert.Equal(["main", "develop", "feature/*"], t.Ci.Branches.Include);
        Assert.Empty(t.Ci.Branches.Exclude);
        Assert.Equal(["README.md", "docs/*"], t.Ci.Paths.Exclude);
        Assert.Empty(t.Ci.Paths.Include);
        Assert.False(t.Pr.Disabled);
        Assert.Equal(["main", "develop"], t.Pr.Branches.Include);
        var schedule = Assert.Single(t.Schedules);
        Assert.Equal(("0 3 * * 1-5", false), (schedule.Cron, schedule.Always));
        Assert.Equal(["main"], schedule.Branches.Include);
        // resources.repositories without a trigger is not a trigger
        Assert.DoesNotContain(t.Notes, n => n.Code == "TRIGGER_RESOURCE_TRIGGERS_NOT_MAPPED");
    }

    [Fact]
    public void The_orion_api_pipeline_batches_filters_branches_paths_and_tags_and_cancels_superseded_pr_runs()
    {
        var t = AzureTriggerReader.Read(Orion("orion-api", "azure-pipelines.yml"))!;
        Assert.True(t.Ci.Batch);
        Assert.Equal(["main", "release/*"], t.Ci.Branches.Include);
        Assert.Equal(["release/legacy-*"], t.Ci.Branches.Exclude);
        Assert.Equal(["src", "tests", "Dockerfile"], t.Ci.Paths.Include);
        Assert.Equal(["docs", "**/*.md"], t.Ci.Paths.Exclude);
        Assert.Equal(["v*"], t.Ci.Tags.Include);
        Assert.True(t.Pr.AutoCancel);
        Assert.Equal(["docs"], t.Pr.Paths.Exclude);
        Assert.Empty(t.Schedules);
    }

    [Fact]
    public void Nothing_is_automatic_when_trigger_and_pr_are_none_except_the_schedule_that_the_nightly_scan_declares()
    {
        var nightly = AzureTriggerReader.Read(Orion("orion-api", "pipelines", "nightly-scan.yml"))!;
        Assert.True(nightly.Ci.Disabled);
        Assert.True(nightly.Pr.Disabled);
        Assert.Equal(("30 1 * * *", true), (nightly.Schedules.Single().Cron, nightly.Schedules.Single().Always));

        var sandbox = AzureTriggerReader.Read(Orion("sandbox-playground", "azure-pipelines.yml"))!;
        Assert.True(sandbox.Ci.Disabled);
        Assert.True(sandbox.Pr.Disabled);
        Assert.Empty(sandbox.Schedules);
    }

    [Theory]
    [InlineData("orion-lib", "azure-pipelines.yml")]
    [InlineData("orion-templates", "azure-pipelines.yml")]
    [InlineData("orion-infra", "pipelines/terraform.yml")]
    [InlineData("orion-infra", "pipelines/bicep.yml")]
    [InlineData("orion-windows", "build/windows-ci.yml")]
    [InlineData("orion-contracts", "azure-pipelines.yml")]
    [InlineData("orion-data-tools", "pipelines/etl.yml")]
    public void Every_other_orion_pipeline_is_read_without_a_note_about_something_it_did_not_understand(string repo, string path)
    {
        var t = AzureTriggerReader.Read(Orion(repo, path.Replace('/', Path.DirectorySeparatorChar)));
        Assert.NotNull(t);
        Assert.DoesNotContain(t!.Notes, n => n.Code is "TRIGGER_SHAPE_UNSUPPORTED" or "TRIGGER_TEMPLATE_EXPRESSION");
    }

    [Fact]
    public void The_shapes_azure_allows_are_read_and_an_absent_trigger_means_every_branch()
    {
        var list = AzureTriggerReader.Read("trigger: [main, 'release/*']\npr: [main]\n")!;
        Assert.Equal(["main", "release/*"], list.Ci.Branches.Include);
        Assert.Equal(["main"], list.Pr.Branches.Include);

        var absent = AzureTriggerReader.Read("steps:\n- script: echo\n")!;
        Assert.True(absent.Ci.Defaulted);
        Assert.False(absent.Ci.Disabled);
        Assert.True(absent.Pr.Disabled); // for Azure Repos Git a missing pr: is "branch policies decide", not "every pull request"

        var batch = AzureTriggerReader.Read("trigger:\n  batch: true\n  branches:\n    include: [main]\n")!;
        Assert.True(batch.Ci.Batch);
        Assert.Equal(["main"], batch.Ci.Branches.Include);
        var off = AzureTriggerReader.Read("trigger: none\n")!;
        Assert.True(off.Ci.Disabled);
        Assert.False(off.Ci.Defaulted);
    }

    [Fact]
    public void Anything_that_is_not_understood_becomes_a_note_and_is_left_out_never_guessed()
    {
        var weird = AzureTriggerReader.Read("trigger: main\npr: 42\nschedules: nightly\n")!;
        Assert.True(weird.Ci.Disabled);   // a shape that is not read converts to "no automatic trigger"
        Assert.True(weird.Pr.Disabled);
        Assert.Empty(weird.Schedules);
        Assert.Contains(weird.Notes, n => n.Code == "TRIGGER_SHAPE_UNSUPPORTED");

        var computed = AzureTriggerReader.Read("trigger:\n  branches:\n    include:\n    - main\n    - ${{ parameters.branch }}\n    - $(Build.SourceBranch)\n    - 7\n")!;
        Assert.Equal(["main", "7"], computed.Ci.Branches.Include); // numbers are YAML scalars: the name "7" is a branch name
        Assert.Contains(computed.Notes, n => n.Code == "TRIGGER_TEMPLATE_EXPRESSION");

        var schedule = AzureTriggerReader.Read("schedules:\n- cron: $(cron)\n- displayName: no cron\n- cron: '0 1 * * *'\n")!;
        Assert.Equal(["0 1 * * *"], schedule.Schedules.Select(s => s.Cron));
        Assert.Contains(schedule.Notes, n => n.Code == "TRIGGER_SHAPE_UNSUPPORTED");

        var resources = AzureTriggerReader.Read("resources:\n  pipelines:\n  - pipeline: build\n    source: other\n    trigger: true\n")!;
        Assert.Contains(resources.Notes, n => n.Code == "TRIGGER_RESOURCE_TRIGGERS_NOT_MAPPED");
    }

    [Fact]
    public void Input_that_is_not_a_single_readable_mapping_or_is_huge_is_not_read_at_all()
    {
        Assert.Null(AzureTriggerReader.Read(null));
        Assert.Null(AzureTriggerReader.Read("  \n"));
        Assert.Null(AzureTriggerReader.Read("- a\n- b\n"));
        Assert.Null(AzureTriggerReader.Read("a: b\n---\nc: d\n"));
        Assert.Null(AzureTriggerReader.Read("trigger: [unclosed\n"));
        Assert.Null(AzureTriggerReader.Read("trigger: none\n" + new string('#', AzureTriggerReader.MaxChars)));
    }
}

public sealed class GitHubTriggerMapperTests
{
    private static GitHubTriggers MapOrion(params string[] parts) => GitHubTriggerMapper.Map(AzureTriggerReader.Read(
        File.ReadAllText(Path.Combine([OrionCorpus.RepositoryRoot(), "tools", "AdoSim", "seed", "files", .. parts])))!);

    private static string[] Codes(GitHubTriggers t) => t.Differences.Select(d => d.Code).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Orion_web_becomes_push_and_pull_request_with_ignored_paths_and_a_schedule_with_the_differences_named()
    {
        var t = MapOrion("orion-web", "azure-pipelines.yml");
        Assert.Equal(["main", "develop", "feature/**"], t.Push!.Branches.Patterns);
        Assert.Empty(t.Push.Branches.Ignore);
        Assert.Empty(t.Push.Paths.Patterns);
        Assert.Equal(["README.md", "README.md/**", "docs/**"], t.Push.Paths.Ignore); // exclude only: paths-ignore
        Assert.Equal(["main", "develop"], t.PullRequest!.Branches.Patterns);
        Assert.Equal(["0 3 * * 1-5"], t.Schedules);
        Assert.Equal(["TRIGGER_PATH_PREFIX", "TRIGGER_PR_AZURE_REPOS_IGNORED", "TRIGGER_SCHEDULE_ALWAYS_FALSE", "TRIGGER_SCHEDULE_BRANCHES", "TRIGGER_TIME_ZONE", "TRIGGER_WILDCARD_SEMANTICS"], Codes(t));
        Assert.Equal(["push — branches: main, develop, feature/**; paths: ignore README.md, ignore README.md/**, ignore docs/**", "pull_request — branches: main, develop", "schedule: 0 3 * * 1-5"], t.Summary());
    }

    [Fact]
    public void Orion_api_keeps_branch_exclusions_as_negations_maps_paths_by_prefix_and_names_what_has_no_equivalent()
    {
        var t = MapOrion("orion-api", "azure-pipelines.yml");
        Assert.Equal(["main", "release/**", "!release/legacy-**"], t.Push!.Branches.Patterns);
        Assert.Equal(["v**"], t.Push.Tags.Patterns);
        Assert.Equal(["src", "src/**", "tests", "tests/**", "Dockerfile", "Dockerfile/**", "!docs", "!docs/**", "!**/*.md"], t.Push.Paths.Patterns);
        Assert.Equal(["main", "release/**"], t.PullRequest!.Branches.Patterns);
        Assert.Equal(["docs", "docs/**"], t.PullRequest.Paths.Ignore);
        Assert.Empty(t.Schedules);
        Assert.Equal(["TRIGGER_AUTOCANCEL_NOT_MAPPED", "TRIGGER_BATCH_NOT_MAPPED", "TRIGGER_PATH_PREFIX", "TRIGGER_PR_AZURE_REPOS_IGNORED", "TRIGGER_WILDCARD_SEMANTICS"], Codes(t));
    }

    [Fact]
    public void A_nightly_schedule_alone_is_a_schedule_and_nothing_else()
    {
        var t = MapOrion("orion-api", "pipelines", "nightly-scan.yml");
        Assert.Null(t.Push);
        Assert.Null(t.PullRequest);
        Assert.Equal(["30 1 * * *"], t.Schedules);
        Assert.Equal(["TRIGGER_SCHEDULE_BRANCHES", "TRIGGER_TIME_ZONE"], Codes(t));
        Assert.False(t.IsEmpty);

        var none = MapOrion("sandbox-playground", "azure-pipelines.yml");
        Assert.True(none.IsEmpty);
        Assert.Empty(none.Differences);
    }

    [Fact]
    public void No_trigger_section_is_every_push_and_says_so()
    {
        var t = GitHubTriggerMapper.Map(AzureTriggerReader.Read("steps:\n- script: echo\n")!);
        Assert.NotNull(t.Push);
        Assert.True(t.Push!.Branches.IsEmpty && t.Push.Tags.IsEmpty && t.Push.Paths.IsEmpty);
        Assert.Null(t.PullRequest);
        Assert.Equal(["TRIGGER_DEFAULT_CI_ALL_BRANCHES"], Codes(t));
        Assert.Equal(["push — no filter (every branch and tag)"], t.Summary());
    }

    [Fact]
    public void Refs_and_paths_are_normalised_and_what_cannot_be_carried_over_is_dropped_with_a_difference()
    {
        var t = GitHubTriggerMapper.Map(AzureTriggerReader.Read("""
            trigger:
              branches:
                include:
                - refs/heads/main
                - refs/tags/should-be-a-branch
                - 'feature/?'
                - 'has space'
                - 'a$b'
                - 'ok/**'
              paths:
                include:
                - /src/app
                - 'x?y'
                - 'src/*.cs'
            """)!);
        Assert.Equal(["main", "should-be-a-branch", "ok/**"], t.Push!.Branches.Patterns);
        Assert.Equal(["src/app", "src/app/**", "src/**.cs"], t.Push.Paths.Patterns);
        Assert.Contains("TRIGGER_PATTERN_UNSUPPORTED", Codes(t));

        // duplicates collapse; an exclusion with nothing to include becomes an ignore list
        var dup = GitHubTriggerMapper.Map(AzureTriggerReader.Read("trigger:\n  branches:\n    include: [main, main]\n    exclude: [old/*]\n  tags:\n    exclude: [beta*]\n")!);
        Assert.Equal(["main", "!old/**"], dup.Push!.Branches.Patterns);
        Assert.Equal(["beta**"], dup.Push.Tags.Ignore);
    }

    [Fact]
    public void Cron_expressions_are_checked_and_the_five_minute_minimum_is_a_difference()
    {
        var t = GitHubTriggerMapper.Map(AzureTriggerReader.Read("trigger: none\nschedules:\n- cron: '*/2 * * * *'\n  always: true\n- cron: '0 1 * * *'\n  always: true\n- cron: 'every day'\n- cron: '0 1 * * * *'\n- cron: '0 1 * * MON-FRI'\n  always: true\n- cron: '0 1 * * *'\n  always: true\n")!);
        Assert.Equal(["*/2 * * * *", "0 1 * * *", "0 1 * * MON-FRI"], t.Schedules);
        Assert.Contains("TRIGGER_CRON_INTERVAL", Codes(t));
        Assert.Contains("TRIGGER_SCHEDULE_UNSUPPORTED", Codes(t));
        Assert.DoesNotContain("TRIGGER_SCHEDULE_ALWAYS_FALSE", Codes(t));
    }

    [Fact]
    public void Pull_request_runs_are_never_presented_as_something_azure_repos_would_have_run()
    {
        var t = GitHubTriggerMapper.Map(AzureTriggerReader.Read("trigger: none\npr:\n  drafts: false\n  autoCancel: true\n  branches:\n    include: [main]\n")!);
        Assert.Equal(["TRIGGER_AUTOCANCEL_NOT_MAPPED", "TRIGGER_DRAFTS_NOT_MAPPED", "TRIGGER_PR_AZURE_REPOS_IGNORED"], Codes(t));
        Assert.Null(t.Push);
        Assert.Equal(["main"], t.PullRequest!.Branches.Patterns);
    }
}

public sealed class WorkflowTriggerEditorTests
{
    private const string ManualOnly = """
        # azure-tasks: converted=UseDotNet@2 manual=none
        name: demo
        # why this is manual
        on:
          workflow_dispatch:
            inputs:
              environment:
                description: where to deploy
                required: true
                default: dev
        permissions:
          contents: read
        # the jobs
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo hi
        """;

    private static AzureTriggers Azure(string yaml) => AzureTriggerReader.Read(yaml)!;
    private const string Both = "trigger:\n  branches:\n    include: [main, 'release/*']\n  paths:\n    exclude: [docs]\npr:\n  branches:\n    include: [main]\nschedules:\n- cron: '0 3 * * 1-5'\n  always: true\n";

    [Fact]
    public void It_rewrites_the_on_entry_keeps_workflow_dispatch_with_its_inputs_and_returns_every_other_line_byte_for_byte()
    {
        var derived = WorkflowTriggerEditor.Derive(ManualOnly, Azure(Both));
        Assert.True(derived.Ok, derived.Error);
        var before = ManualOnly.Split('\n');
        var after = derived.Yaml!.Split('\n');

        // everything before `on:` and from `permissions:` on is unchanged, comments included
        var onAt = Array.IndexOf(before, "on:");
        Assert.Equal(before.Take(onAt), after.Take(onAt));
        Assert.Equal(before.SkipWhile(l => !l.StartsWith("permissions:")), after.SkipWhile(l => !l.StartsWith("permissions:")));
        Assert.Contains("# why this is manual", after);
        Assert.Contains("# the jobs", after);

        const string expectedOn = """
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    description: where to deploy
                    required: true
                    default: dev
              push:
                branches:
                  - 'main'
                  - 'release/**'
                paths-ignore:
                  - 'docs'
                  - 'docs/**'
              pull_request:
                branches:
                  - 'main'
              schedule:
                - cron: '0 3 * * 1-5'
            """;
        Assert.Equal(expectedOn.Replace("\r\n", "\n"), string.Join('\n', after.Skip(onAt).TakeWhile(l => !l.StartsWith("permissions:"))));
        Assert.Equal(derived.Yaml, WorkflowTriggerEditor.Derive(ManualOnly, Azure(Both)).Yaml); // deterministic
    }

    [Fact]
    public void The_derived_workflow_is_valid_yaml_with_exactly_the_triggers_that_were_mapped_and_passes_the_structure_gate()
    {
        var derived = WorkflowTriggerEditor.Derive(ManualOnly, Azure(Both));
        var parsed = ParsedWorkflow.TryParse(derived.Yaml!, out var error);
        Assert.NotNull(parsed);
        Assert.Null(error);
        Assert.Equal(["workflow_dispatch", "push", "pull_request", "schedule"], parsed!.TriggerNames().Select(t => t.Name));
        Assert.Equal(["jobs", "name", "on", "permissions"], parsed.Root.Children.Keys.OfType<YamlDotNet.RepresentationModel.YamlScalarNode>().Select(k => k.Value!).Order());
    }

    [Fact]
    public void Other_shapes_of_a_manual_only_on_entry_are_accepted_and_automatic_ones_are_not()
    {
        var bare = "name: x\non: workflow_dispatch\njobs: {}\n";
        foreach (var on in new[] { "on: workflow_dispatch", "on: [workflow_dispatch]", "on: {workflow_dispatch: {}}", "'on': workflow_dispatch" })
        {
            var derived = WorkflowTriggerEditor.Derive(bare.Replace("on: workflow_dispatch", on), Azure("trigger:\n  branches:\n    include: [main]\n"));
            Assert.True(derived.Ok, on + " -> " + derived.Error);
            Assert.Contains("\non:\n  workflow_dispatch:\n  push:\n    branches:\n      - 'main'\n", "\n" + derived.Yaml);
        }
        Assert.Equal("WorkflowHasOtherTriggers", WorkflowTriggerEditor.Derive("name: x\non:\n  push:\n  workflow_dispatch:\njobs: {}\n", Azure(Both)).Error);
        Assert.Equal("WorkflowHasOtherTriggers", WorkflowTriggerEditor.Derive("name: x\non:\n  pull_request_target:\njobs: {}\n", Azure(Both)).Error);
        Assert.Equal("TriggerShapeUnsupported", WorkflowTriggerEditor.Derive("name: x\non: push\njobs: {}\n", Azure(Both)).Error);
        Assert.Equal("TriggerShapeUnsupported", WorkflowTriggerEditor.Derive("name: x\non: [push, workflow_dispatch]\njobs: {}\n", Azure(Both)).Error);
        Assert.Equal("OnSectionNotFound", WorkflowTriggerEditor.Derive("name: x\njobs: {}\n", Azure(Both)).Error);
        Assert.Equal("WorkflowNotReadable", WorkflowTriggerEditor.Derive("name: [unclosed\n", Azure(Both)).Error);
        Assert.Equal("WorkflowNotReadable", WorkflowTriggerEditor.Derive("- a\n", Azure(Both)).Error);
    }

    [Fact]
    public void Nothing_is_derived_when_azure_has_no_automatic_trigger()
    {
        var derived = WorkflowTriggerEditor.Derive(ManualOnly, Azure("trigger: none\npr: none\n"));
        Assert.False(derived.Ok);
        Assert.Equal("NoAutomaticTriggers", derived.Error);
        Assert.Null(derived.Yaml);
    }

    [Fact]
    public void A_workflow_that_signs_in_or_uses_secrets_gets_the_difference_that_says_it_will_do_so_on_every_event()
    {
        var plain = WorkflowTriggerEditor.Derive(ManualOnly, Azure(Both));
        Assert.DoesNotContain(plain.Differences, d => d.Code == "TRIGGER_STARTS_PRIVILEGED_JOBS");

        foreach (var privileged in new[]
        {
            ManualOnly.Replace("- run: echo hi", "- uses: azure/login@abc"),
            ManualOnly.Replace("- run: echo hi", "- run: echo\n        env:\n          K: ${{ secrets.DEPLOY_KEY }}"),
            ManualOnly.Replace("contents: read", "contents: read\n  id-token: write")
        })
            Assert.Contains(WorkflowTriggerEditor.Derive(privileged, Azure(Both)).Differences, d => d.Code == "TRIGGER_STARTS_PRIVILEGED_JOBS");
    }

    [Fact]
    public void Windows_line_endings_are_normalised_and_the_result_is_still_one_valid_document()
    {
        var derived = WorkflowTriggerEditor.Derive(ManualOnly.Replace("\n", "\r\n"), Azure(Both));
        Assert.True(derived.Ok, derived.Error);
        Assert.DoesNotContain('\r', derived.Yaml!);
        Assert.Equal(WorkflowTriggerEditor.Derive(ManualOnly, Azure(Both)).Yaml, derived.Yaml);
    }

    [Fact]
    public void A_pattern_cannot_break_out_of_its_quotes_or_inject_yaml()
    {
        var hostile = Azure("trigger:\n  branches:\n    include:\n    - \"main'\\n  evil: true\"\n    - \"x\\n- cron: 1\"\n    - 'ok'\n");
        var derived = WorkflowTriggerEditor.Derive(ManualOnly, hostile);
        Assert.True(derived.Ok);
        Assert.Contains("- 'ok'", derived.Yaml);
        Assert.DoesNotContain("evil", derived.Yaml);
        Assert.DoesNotContain("cron: 1", derived.Yaml);
        Assert.Contains(derived.Differences, d => d.Code == "TRIGGER_PATTERN_UNSUPPORTED");
    }

    [Fact]
    public void The_trigger_policy_lifts_manual_only_for_push_pull_request_and_schedule_and_for_nothing_else()
    {
        var manual = WorkflowGatePolicy.Default;
        var triggers = manual.WithAutomaticTriggers();
        Assert.True(manual.RequireDisabledTriggers);
        Assert.False(triggers.RequireDisabledTriggers);
        Assert.EndsWith("+triggers", triggers.Version);
        Assert.Equal(manual.AllowedActionOwners, triggers.AllowedActionOwners); // everything else is the same policy

        var derived = WorkflowTriggerEditor.Derive(ManualOnly, Azure(Both)).Yaml!;
        Assert.True(Run(manual, derived).Has("TRIGGERS_NOT_DISABLED"));
        var allowed = Run(triggers, derived);
        Assert.False(allowed.Has("TRIGGERS_NOT_DISABLED"));
        Assert.False(allowed.Has("TRIGGER_NOT_PERMITTED"));

        foreach (var other in new[] { "release", "workflow_run", "issues", "pull_request_target", "repository_dispatch" })
        {
            var yaml = ManualOnly.Replace("on:\n  workflow_dispatch:", $"on:\n  {other}:\n  workflow_dispatch:");
            Assert.True(Run(triggers, yaml).Has("TRIGGER_NOT_PERMITTED"), other);
        }
        Assert.True(Run(triggers, ManualOnly.Replace("on:\n  workflow_dispatch:", "on:\n  pull_request_target:\n  workflow_dispatch:")).Has("DANGEROUS_TRIGGER"));
    }

    private static WorkflowGateReport Run(WorkflowGatePolicy policy, string yaml) =>
        new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate()], policy).RunAsync(yaml, WorkflowPlanContext.Empty, default).Result;
}

// The mapped triggers against the real analysers, on the ten Orion pipelines: every golden workflow gets the triggers of its own Azure source. actionlint
// is the oracle for the syntax of GitHub's trigger filters (it knows what GitHub accepts); what the filters MEAN is not something it can say.
public sealed class OrionTriggerDerivationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [WorkflowGates.RequiresToolsFact]
    public async Task Every_orion_workflow_with_automatic_azure_triggers_gets_them_in_a_form_the_real_analysers_accept()
    {
        var derivedCount = 0;
        foreach (var c in OrionCorpus.Load())
        {
            var azure = AzureTriggerReader.Read(c.SourceYaml);
            if (azure is null) continue; // the classic pipeline has no YAML
            var derived = WorkflowTriggerEditor.Derive(c.GoldenYaml, azure);
            if (!derived.Ok) { Assert.Equal("NoAutomaticTriggers", derived.Error); output.WriteLine($"{c.Name,-34} nothing to derive"); continue; }
            derivedCount++;
            var report = await WorkflowGates.WorkflowTools.RealPipeline(c.Policy().WithAutomaticTriggers(), [new TaskAccountingGate()]).RunAsync(derived.Yaml!, c.Plan(), default);
            output.WriteLine($"{c.Name,-34} {string.Join(" | ", derived.Triggers!.Summary())}");
            Assert.True(report.Approvable, c.Name + ": " + string.Join("; ", report.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Gate + ":" + f.Code + "@" + f.Line + " " + f.Message)));
            Assert.False(report.Has("TRIGGERS_NOT_DISABLED"));
            // the manual-only version of the same text is still manual-only: deriving changed a copy
            Assert.True((await WorkflowGates.WorkflowTools.RealPipeline(c.Policy(), [new TaskAccountingGate()]).RunAsync(c.GoldenYaml, c.Plan(), default)).Approvable);
        }
        Assert.True(derivedCount >= 5, $"only {derivedCount} Orion workflows had automatic triggers to derive");
    }
}
