using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using GitHubMigrator.Infrastructure.WorkflowTools;

namespace GitHubMigrator.Domain.Tests.Ai;

// Self-hosted runner labels: read from the SOURCE pipeline's pools by the product, given to actionlint as the only labels it accepts beyond GitHub's, and never written by a model.
public sealed class AzureRunnerLabelsTests
{
    private static string Orion(params string[] parts) => File.ReadAllText(Path.Combine([OrionCorpus.RepositoryRoot(), "tools", "AdoSim", "seed", "files", .. parts]));

    [Fact]
    public void The_plain_capability_names_a_self_hosted_pool_demands_become_labels_in_order_of_appearance()
    {
        Assert.Equal(["docker", "npm"], AzureRunnerLabels.From("""
            pool:
              name: SelfHostedLinux
              demands:
              - Agent.OS -equals Linux
              - docker
              - npm
            """));
        Assert.Equal(["python3"], AzureRunnerLabels.From("pool:\n  name: Default\n  demands: python3\n")); // a single demand is a scalar
    }

    [Fact]
    public void Pools_are_found_at_any_level_and_each_label_is_listed_once_whatever_its_case()
    {
        var yaml = """
            pool:
              name: A
              demands: [docker]
            stages:
            - stage: S
              pool:
                name: B
                demands: [NPM, Docker]
              jobs:
              - job: J
                pool:
                  name: C
                  demands:
                  - terraform
              - deployment: D
                pool:
                  name: E
                  demands: dotnet-sdk
            """;
        Assert.Equal(["docker", "NPM", "terraform", "dotnet-sdk"], AzureRunnerLabels.From(yaml));
    }

    [Fact]
    public void Only_a_plain_name_is_a_label_never_an_operator_a_variable_an_expression_or_something_odd()
    {
        var hostile = new[]
        {
            "Agent.OS -equals Linux", "Agent.Name -equals build-01", "docker -equals yes", "$(runnerLabel)", "${{ parameters.label }}", "two words", "a b", "-leading-dash", ".leading-dot",
            "quote'inject", "line\\nbreak", "semi;colon", "back`tick", "über", new string('x', 51), "'; run: evil", "label: value", "- item", "#comment", "{brace}"
        };
        var yaml = "pool:\n  name: P\n  demands:\n" + string.Concat(hostile.Select(h => $"  - \"{h}\"\n")) + "  - good-one\n";
        Assert.Equal(["good-one"], AzureRunnerLabels.From(yaml));
        Assert.Equal([new string('x', 50)], AzureRunnerLabels.From($"pool:\n  name: P\n  demands: [{new string('x', 50)}]\n"));
    }

    [Fact]
    public void Labels_every_runner_or_github_already_has_and_hosted_pools_and_other_keys_add_nothing()
    {
        Assert.Empty(AzureRunnerLabels.From("pool:\n  name: P\n  demands: [self-hosted, Linux, windows, macOS, x64, ARM64, ubuntu-latest]\n"));
        // a Microsoft-hosted pool has no demands to speak of
        Assert.Empty(AzureRunnerLabels.From("pool:\n  vmImage: ubuntu-latest\n  demands: [docker]\n"));
        // only `demands` of a `pool` count: not the pool's name, not a demands key elsewhere, not a parameter
        Assert.Empty(AzureRunnerLabels.From("demands: [docker]\nparameters:\n- name: demands\n  default: npm\nsteps:\n- script: echo\n  displayName: docker\npool:\n  name: docker\n"));
    }

    [Fact]
    public void The_list_is_capped_and_an_unreadable_or_oversized_or_empty_source_has_none()
    {
        var many = "pool:\n  name: P\n  demands:\n" + string.Concat(Enumerable.Range(0, 40).Select(i => $"  - cap{i}\n"));
        var labels = AzureRunnerLabels.From(many);
        Assert.Equal(AzureRunnerLabels.MaxLabels, labels.Count);
        Assert.Equal("cap0", labels[0]);
        Assert.Empty(AzureRunnerLabels.From(null));
        Assert.Empty(AzureRunnerLabels.From("   "));
        Assert.Empty(AzureRunnerLabels.From("pool: [unclosed\n"));
        Assert.Empty(AzureRunnerLabels.From("pool:\n  name: P\n  demands: [docker]\n---\nb: c\n")); // not one document
        Assert.Empty(AzureRunnerLabels.From("pool:\n  name: P\n  demands: [docker]\n" + new string('#', 150_000)));
    }

    [Fact]
    public void The_orion_estate_demands_docker_and_npm_in_one_pipeline_and_python3_in_another_and_nothing_elsewhere()
    {
        Assert.Equal(["docker", "npm"], AzureRunnerLabels.From(Orion("orion-web", "azure-pipelines.yml")));
        Assert.Equal(["python3"], AzureRunnerLabels.From(Orion("orion-data-tools", "pipelines", "etl.yml")));
        foreach (var c in OrionCorpus.Load().Where(c => c.Name != "2003-orion-web-ci-cd"))
            Assert.Empty(AzureRunnerLabels.From(c.SourceYaml));
    }

    [Fact]
    public void Aliases_that_refer_back_to_a_pool_do_not_loop()
    {
        Assert.Equal(["docker"], AzureRunnerLabels.From("a: &p\n  name: P\n  demands: [docker]\nb:\n  pool: *p\nc:\n  pool: *p\n"));
    }
}

public sealed class RunnerLabelsInThePlanTests
{
    private static ConversionPlan Plan(params string[] labels) => new([new("NUGET_API_KEY", "pipeline variable")], [new("region", "pipeline variable")], [], ["note"], labels.Length == 0 ? null : labels);

    [Fact]
    public void A_plan_without_labels_is_what_it_always_was_and_labels_enter_the_hash_only_when_there_are_some()
    {
        var plain = Plan();
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("s:NUGET_API_KEY\nv:region")));
        Assert.Equal(expected, plain.Sha256);
        Assert.NotEqual(plain.Sha256, Plan("docker").Sha256);
        Assert.Equal(Plan("docker", "npm").Sha256, Plan("npm", "docker").Sha256); // the order they were found in is not part of the plan
        Assert.NotEqual(Plan("docker").Sha256, Plan("npm").Sha256);
        Assert.Empty(plain.Labels);
    }

    [Fact]
    public void Both_gate_contexts_carry_the_labels_so_approving_gates_the_stored_text_as_it_was_gated_when_proposed()
    {
        var plan = Plan("docker", "npm");
        Assert.Equal(["docker", "npm"], plan.ToGateContext("steps:\n- script: echo\n").RunnerLabels);
        Assert.Equal(["docker", "npm"], plan.ToGateContextFor(["UseDotNet@2"]).RunnerLabels);
        Assert.Empty(Plan().ToGateContext(null).RunnerLabels);
        Assert.Empty(WorkflowPlanContext.Empty.RunnerLabels);
    }

    [Fact]
    public void The_plan_is_stored_with_its_labels_and_a_plan_stored_before_they_existed_reads_back_with_none()
    {
        var json = JsonSerializer.Serialize(Plan("docker"));
        Assert.Contains("\"RunnerLabels\":[\"docker\"]", json);
        Assert.DoesNotContain("\"Labels\"", json); // the convenience accessor is not stored
        Assert.Equal(["docker"], JsonSerializer.Deserialize<ConversionPlan>(json)!.Labels);

        var old = JsonSerializer.Deserialize<ConversionPlan>("{\"Secrets\":[{\"Name\":\"A\",\"Origin\":\"x\"}],\"Variables\":[],\"Connections\":[],\"Notes\":[]}")!;
        Assert.Empty(old.Labels);
        Assert.Null(old.RunnerLabels);
        Assert.Equal(["A"], old.ToGateContextFor([]).AllowedSecrets);
    }

    [Fact]
    public void Building_a_plan_reads_the_labels_from_the_pipeline_and_says_so_in_the_notes_only_when_there_are_some()
    {
        var pipeline = new AzurePipelineSource(1, "p", "yaml", "azure-pipelines.yml", "repo", [], [], "available", null);
        var plain = ConversionPlanBuilder.Build(pipeline, "steps:\n- script: echo\n");
        Assert.Null(plain.RunnerLabels);
        Assert.DoesNotContain(plain.Notes, n => n.Contains("self-hosted"));

        var withPool = ConversionPlanBuilder.Build(pipeline, "pool:\n  name: P\n  demands: [docker, npm]\nsteps:\n- script: echo\n");
        Assert.Equal(["docker", "npm"], withPool.RunnerLabels);
        Assert.Contains(withPool.Notes, n => n.Contains("docker, npm") && n.Contains("the destination needs runners"));
        Assert.Equal(plain.Notes.Count + 1, withPool.Notes.Count);
    }

    [Fact]
    public void The_prompt_names_the_labels_only_when_the_source_demanded_some_and_cannot_be_given_any_other_text()
    {
        const string nonce = "0123456789abcdef";
        var policy = WorkflowGatePolicy.Default;
        var (_, plain) = ConversionPrompt.Build(Plan(), "steps:\n- script: echo\n", new HashSet<string>(), nonce, policy);
        Assert.DoesNotContain("RUNNER LABELS", plain);

        var (_, labelled) = ConversionPrompt.Build(Plan("docker", "npm"), "steps:\n- script: echo\n", new HashSet<string>(), nonce, policy);
        Assert.Contains("RUNNER LABELS the pipeline's self-hosted pools demand: docker, npm.", labelled);
        Assert.Contains("no other custom label is accepted", labelled);
        // everything else in the request is what it was
        Assert.Equal(plain, labelled.Replace(labelled.Split('\n').Single(l => l.StartsWith("RUNNER LABELS")) + "\n", ""));
    }
}

// What the gate tells the runner, and what the real actionlint then says. The runner labels are the whole point: without them the source's own demands cannot be kept.
public sealed class ActionlintRunnerLabelsTests
{
    private sealed class RecordingRunner(string tool) : IWorkflowToolRunner
    {
        public string Tool => tool;
        public List<IReadOnlyList<string>?> Seen { get; } = [];
        public Task<ToolRun> RunAsync(string workflowYaml, CancellationToken ct) { Seen.Add(null); return Task.FromResult(new ToolRun(true, "[]", null, 0)); }
        public Task<ToolRun> RunAsync(string workflowYaml, WorkflowToolOptions options, CancellationToken ct) { Seen.Add(options.RunnerLabels); return Task.FromResult(new ToolRun(true, "[]", null, 0)); }
    }

    private static WorkflowGateContext Context(params string[] labels) =>
        new("name: x\non:\n  workflow_dispatch:\njobs: {}\n", null, WorkflowPlanContext.Empty with { RunnerLabels = labels }, WorkflowGatePolicy.Default);

    [Fact]
    public async Task Only_actionlint_is_told_the_labels_and_only_when_there_are_some()
    {
        var actionlint = new RecordingRunner("actionlint");
        var zizmor = new RecordingRunner("zizmor");
        await new ActionlintGate(actionlint).EvaluateAsync(Context("docker", "npm"), default);
        await new ActionlintGate(actionlint).EvaluateAsync(Context(), default);
        await new ZizmorGate(zizmor).EvaluateAsync(Context("docker", "npm"), default);
        Assert.Equal(["docker", "npm"], actionlint.Seen[0]);
        Assert.Null(actionlint.Seen[1]); // nothing to say: the runner is called exactly as before
        Assert.Null(zizmor.Seen[0]);
    }

    [Fact]
    public async Task A_runner_that_knows_nothing_of_options_still_runs_the_workflow()
    {
        var plain = new FakeToolRunner("actionlint", new(true, "[]", null, 0));
        var result = await new ActionlintGate(plain).EvaluateAsync(Context("docker"), default);
        Assert.Equal(1, plain.Calls);
        Assert.Equal(GateStatus.Passed, result.Status);
    }

    private const string SelfHosted = """
        name: deploy
        on:
          workflow_dispatch:
        permissions:
          contents: read
        jobs:
          deploy:
            runs-on: [self-hosted, linux, docker, npm]
            steps:
              - run: echo hello
        """;

    private static ProcessWorkflowToolRunner Actionlint() => ProcessWorkflowToolRunner.Actionlint(WorkflowTools.Actionlint ?? throw new InvalidOperationException("actionlint not installed"));

    [RequiresToolsFact]
    public async Task The_real_actionlint_rejects_the_source_labels_unless_the_product_names_them_and_still_rejects_every_other()
    {
        var runner = Actionlint();
        var without = await runner.RunAsync(SelfHosted, default);
        using (var document = JsonDocument.Parse(without.Output))
            Assert.Contains("label \"docker\" is unknown", document.RootElement[0].GetProperty("message").GetString());

        async Task<string[]> Kinds(string yaml, params string[] labels)
        {
            var run = await runner.RunAsync(yaml, new(labels), default);
            Assert.True(run.Completed, run.Detail);
            using var document = JsonDocument.Parse(run.Output);
            return document.RootElement.EnumerateArray().Select(f => f.GetProperty("kind").GetString() + ":" + f.GetProperty("message").GetString()!.Split('"')[1]).ToArray();
        }
        Assert.Equal(["runner-label:docker", "runner-label:npm"], await Kinds(SelfHosted)); // every label GitHub does not know is reported
        Assert.Empty(await Kinds(SelfHosted, "docker", "npm"));
        Assert.Equal(["runner-label:npm"], await Kinds(SelfHosted, "docker"));                    // a label the source did not demand is still refused
        Assert.Equal(["runner-label:docker"], await Kinds(SelfHosted, "npm"));
        // actionlint compares the spelling exactly (measured, not assumed): the plan names the labels as the source spelled them and the prompt hands them to the model in that spelling
        Assert.Equal(["runner-label:docker", "runner-label:npm"], await Kinds(SelfHosted, "DOCKER", "Npm"));
        // labels do not make anything else acceptable
        Assert.Equal(["runner-label:evil-runner"], await Kinds(SelfHosted.Replace("[self-hosted, linux, docker, npm]", "[self-hosted, linux, docker, evil-runner]"), "docker", "npm"));
        Assert.Equal(["runner-label:docker"], await Kinds(SelfHosted.Replace("[self-hosted, linux, docker, npm]", "docker"), "npm")); // a label that is not in the list is refused wherever it appears
    }

    [RequiresToolsFact]
    public async Task A_label_that_is_not_a_plain_name_is_refused_before_anything_runs()
    {
        var runner = Actionlint();
        foreach (var bad in new[] { "x'\\n  y: z", "a b", "$(x)", "", new string('x', 51), "-dash", "a:b" })
        {
            var run = await runner.RunAsync(SelfHosted, new([bad]), default);
            Assert.False(run.Completed);
            Assert.Contains("could not be configured", run.Detail);
            Assert.Empty(run.Output);
        }
        Assert.False((await runner.RunAsync(SelfHosted, new(Enumerable.Range(0, 33).Select(i => "l" + i).ToArray()), default)).Completed); // more labels than any plan carries
        // the gate turns a runner that did not run into NotRun: a workflow is never approvable on the strength of nothing
        var result = await new ActionlintGate(runner).EvaluateAsync(new(SelfHosted, null, WorkflowPlanContext.Empty with { RunnerLabels = ["a b"] }, WorkflowGatePolicy.Default), default);
        Assert.Equal(GateStatus.NotRun, result.Status);
    }

    [RequiresToolsFact]
    public async Task With_the_labels_of_the_plan_a_workflow_that_keeps_the_sources_demands_is_approvable_with_the_warning_a_person_has_to_accept()
    {
        var plan = WorkflowPlanContext.Empty;
        var blocked = await WorkflowTools.RealPipeline().RunAsync(SelfHosted, plan, default);
        Assert.False(blocked.Approvable);
        Assert.Contains(blocked.Findings, f => f.Code == "ACTIONLINT_RUNNER_LABEL" && f.Severity == GateSeverity.Blocker);

        var allowed = await WorkflowTools.RealPipeline().RunAsync(SelfHosted, plan with { RunnerLabels = ["docker", "npm"] }, default);
        Assert.True(allowed.Approvable, string.Join("; ", allowed.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code + " " + f.Message)));
        Assert.DoesNotContain(allowed.Findings, f => f.Code == "ACTIONLINT_RUNNER_LABEL");
        Assert.Contains(allowed.Findings, f => f.Code == "SELF_HOSTED_RUNNER" && f.Severity == GateSeverity.Warning); // still a person's decision
    }
}
