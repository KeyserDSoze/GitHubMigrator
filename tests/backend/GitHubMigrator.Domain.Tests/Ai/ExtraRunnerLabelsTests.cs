using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Domain.Tests.Ai;

// Labels the pipeline did not demand: the operator's, for every conversion, and the ones a person gives for one conversion. They are what the destination's runners are SAID to have. They let a workflow
// pass the one check that knows only GitHub's labels; they create no runner, and a label that is not a plain name is refused, never dropped.
public sealed class ExtraRunnerLabelsTests
{
    private static readonly AzurePipelineSource Pipeline = new(1, "p", "yaml", "azure-pipelines.yml", "repo", [], [], "available", null);
    private const string Yaml = "steps:\n- script: echo\n";

    [Fact]
    public void Named_labels_are_trimmed_deduplicated_and_the_ones_everybody_has_are_not_needed_and_not_an_error()
    {
        var parsed = AzureRunnerLabels.Parse([" gpu ", "GPU", "linux-x64", "", "  ", null, "self-hosted", "Linux", "ubuntu-latest", "python3.12"]);
        Assert.Equal(["gpu", "linux-x64", "python3.12"], parsed.Labels);
        Assert.Empty(parsed.Rejected);
        Assert.Empty(AzureRunnerLabels.Parse(null).Labels);
    }

    [Fact]
    public void A_label_that_is_not_a_plain_name_is_rejected_by_name_and_never_silently_dropped()
    {
        var parsed = AzureRunnerLabels.Parse(["gpu", "Agent.OS -equals Linux", "$(label)", "${{ vars.RUNNER }}", "a b", "x;rm", new string('x', 51), new string('y', 90), "ok"]);
        Assert.Equal(["gpu", "ok"], parsed.Labels);
        Assert.Equal(7, parsed.Rejected.Count);
        Assert.Contains("a b", parsed.Rejected);
        Assert.Equal(60, parsed.Rejected.Single(r => r.StartsWith("yyyy", StringComparison.Ordinal)).Length); // what is reported back is bounded
        Assert.DoesNotContain(parsed.Labels, l => l.Contains(' ') || l.Contains('$') || l.Contains(';'));
    }

    [Fact]
    public void There_is_a_limit_and_what_is_beyond_it_is_rejected_not_cut_off_quietly()
    {
        var many = Enumerable.Range(1, AzureRunnerLabels.MaxLabels + 3).Select(i => $"label{i}").ToArray();
        var parsed = AzureRunnerLabels.Parse(many);
        Assert.Equal(AzureRunnerLabels.MaxLabels, parsed.Labels.Count);
        Assert.Equal(many.Skip(AzureRunnerLabels.MaxLabels), parsed.Rejected);
    }

    [Fact]
    public void The_operator_configuration_is_split_on_commas_semicolons_and_spaces_and_reports_what_it_ignored()
    {
        var configured = OperatorRunnerLabels.From("gpu, linux-x64;orion-build  bad$label");
        Assert.Equal(["gpu", "linux-x64", "orion-build"], configured.Labels);
        Assert.Equal(["bad$label"], configured.Rejected);
        Assert.Empty(OperatorRunnerLabels.From(null).Labels);
        Assert.Empty(OperatorRunnerLabels.From("").Labels);
    }

    [Fact]
    public void The_plan_has_the_demanded_labels_and_the_extra_ones_apart_each_with_its_own_note()
    {
        var plan = ConversionPlanBuilder.Build(Pipeline, "pool:\n  name: P\n  demands: [docker]\nsteps:\n- script: echo\n", ["gpu", "docker"], ["orion-build", "GPU"]);
        Assert.Equal(["docker"], plan.RunnerLabels);
        Assert.Equal(["gpu", "orion-build"], plan.ExtraRunnerLabels); // docker was demanded, GPU is gpu
        Assert.Equal(["docker", "gpu", "orion-build"], plan.GateLabels);
        Assert.Contains(plan.Notes, n => n.Contains("allowed by the operator for every conversion: gpu."));
        Assert.Contains(plan.Notes, n => n.Contains("given for this conversion: orion-build.") && n.Contains("the destination needs runners that have them"));
    }

    [Fact]
    public void Without_extra_labels_the_plan_is_what_it_was_and_extras_change_its_hash()
    {
        var plain = ConversionPlanBuilder.Build(Pipeline, Yaml);
        Assert.Null(plain.ExtraRunnerLabels);
        Assert.Empty(plain.ExtraLabels);
        Assert.Equal(plain.Sha256, ConversionPlanBuilder.Build(Pipeline, Yaml, [], []).Sha256);
        var withExtra = ConversionPlanBuilder.Build(Pipeline, Yaml, null, ["gpu"]);
        Assert.NotEqual(plain.Sha256, withExtra.Sha256);
        Assert.Equal(ConversionPlanBuilder.Build(Pipeline, Yaml, ["gpu"], null).Sha256, withExtra.Sha256); // who gave it is not part of what the plan allows
        Assert.Equal(withExtra.Sha256, ConversionPlanBuilder.Build(Pipeline, Yaml, null, ["gpu"]).Sha256);
        // a label that is demanded is not the same plan as one that is only said to exist
        var demanded = ConversionPlanBuilder.Build(Pipeline, "pool:\n  name: P\n  demands: [gpu]\nsteps:\n- script: echo\n");
        Assert.NotEqual(withExtra.Sha256, demanded.Sha256);
    }

    [Fact]
    public void Extra_labels_are_added_only_for_a_pipeline_converted_from_its_yaml_and_never_beyond_the_limit()
    {
        Assert.Null(ConversionPlanBuilder.Build(Pipeline, null, ["gpu"], ["x"]).ExtraRunnerLabels); // nothing is converted: nothing to allow
        Assert.Null(ConversionPlanBuilder.Build(Pipeline, "  ", ["gpu"], ["x"]).ExtraRunnerLabels);

        var demanded = string.Join(", ", Enumerable.Range(1, 15).Select(i => $"d{i}"));
        var plan = ConversionPlanBuilder.Build(Pipeline, $"pool:\n  name: P\n  demands: [{demanded}]\nsteps:\n- script: echo\n", ["op1", "op2"], ["req1"]);
        Assert.Equal(15, plan.RunnerLabels!.Count);
        Assert.Equal(["op1"], plan.ExtraRunnerLabels); // the pool's own demands come first; the 16th is the last
        Assert.Equal(AzureRunnerLabels.MaxLabels, plan.GateLabels.Count);
    }

    [Fact]
    public void Both_gate_contexts_carry_every_label_so_approving_gates_the_stored_text_as_it_was_gated_when_proposed()
    {
        var plan = ConversionPlanBuilder.Build(Pipeline, "pool:\n  name: P\n  demands: [docker]\nsteps:\n- script: echo\n", ["gpu"], ["orion-build"]);
        Assert.Equal(["docker", "gpu", "orion-build"], plan.ToGateContext(Yaml).RunnerLabels);
        Assert.Equal(["docker", "gpu", "orion-build"], plan.ToGateContextFor(["UseDotNet@2"]).RunnerLabels);
        Assert.Equal(["gpu"], ConversionPlanBuilder.Build(Pipeline, Yaml, ["gpu"]).ToGateContextFor([]).RunnerLabels);
    }

    [Fact]
    public void The_plan_is_stored_with_its_extra_labels_and_a_plan_stored_before_they_existed_reads_back_with_none()
    {
        var plan = ConversionPlanBuilder.Build(Pipeline, Yaml, ["gpu"], ["orion-build"]);
        var json = JsonSerializer.Serialize(plan);
        Assert.Contains("\"ExtraRunnerLabels\":[\"gpu\",\"orion-build\"]", json);
        Assert.DoesNotContain("\"ExtraLabels\"", json);
        Assert.DoesNotContain("\"GateLabels\"", json);
        Assert.Equal(["gpu", "orion-build"], JsonSerializer.Deserialize<ConversionPlan>(json)!.ExtraLabels);

        var old = JsonSerializer.Deserialize<ConversionPlan>("{\"Secrets\":[],\"Variables\":[],\"Connections\":[],\"Notes\":[],\"RunnerLabels\":[\"docker\"]}")!;
        Assert.Null(old.ExtraRunnerLabels);
        Assert.Equal(["docker"], old.GateLabels);
    }

    [Fact]
    public void The_prompt_names_the_extra_labels_apart_from_the_demanded_ones_and_only_when_there_are_some()
    {
        const string nonce = "0123456789abcdef";
        var policy = WorkflowGatePolicy.Default;
        var (_, plain) = ConversionPrompt.Build(ConversionPlanBuilder.Build(Pipeline, Yaml), Yaml, new HashSet<string>(), nonce, policy);
        Assert.DoesNotContain("RUNNER LABELS", plain);

        var extraOnly = ConversionPlanBuilder.Build(Pipeline, Yaml, ["gpu"], null);
        var (_, prompt) = ConversionPrompt.Build(extraOnly, Yaml, new HashSet<string>(), nonce, policy);
        Assert.Contains("ADDITIONAL RUNNER LABELS the destination's runners are said to have", prompt);
        Assert.Contains("not demanded by the pipeline): gpu.", prompt);
        Assert.DoesNotContain("self-hosted pools demand", prompt); // nothing was demanded

        var both = ConversionPlanBuilder.Build(Pipeline, "pool:\n  name: P\n  demands: [docker]\nsteps:\n- script: echo\n", ["gpu"], null);
        var (_, bothPrompt) = ConversionPrompt.Build(both, Yaml, new HashSet<string>(), nonce, policy);
        Assert.Contains("RUNNER LABELS the pipeline's self-hosted pools demand: docker.", bothPrompt);
        Assert.Contains("not demanded by the pipeline): gpu.", bothPrompt);
        Assert.Contains("no other custom label is accepted", prompt);
        // everything else in the request is what it was
        Assert.Equal(plain, prompt.Replace(prompt.Split('\n').Single(l => l.StartsWith("ADDITIONAL RUNNER LABELS")) + "\n", ""));
    }
}
