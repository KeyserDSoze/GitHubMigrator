using GitHubMigrator.Business.WorkflowGates;
using static GitHubMigrator.Domain.Tests.WorkflowGates.WorkflowSamples;

namespace GitHubMigrator.Domain.Tests.WorkflowGates;

public sealed class TaskAccountingGateTests
{
    private static WorkflowPlanContext Plan(params string[] tasks) =>
        WorkflowPlanContext.Of([], []) with { SourceTasks = new HashSet<string>(tasks, StringComparer.OrdinalIgnoreCase) };

    private static async Task<WorkflowGateReport> RunAsync(string yaml, params string[] sourceTasks) =>
        await new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate(), new TaskAccountingGate()])
            .RunAsync(yaml, Plan(sourceTasks), default);

    private static string[] Codes(WorkflowGateReport report) =>
        report.Findings.Where(f => f.Gate == TaskAccountingGate.GateName).Select(f => f.Code).Order().ToArray();

    [Fact]
    public async Task A_workflow_with_no_source_tasks_needs_no_header()
    {
        Assert.Empty(Codes(await RunAsync(Good)));
    }

    [Fact]
    public async Task Source_tasks_without_any_header_are_one_blocker_not_a_wall_of_them()
    {
        var report = await RunAsync(Good, "UseDotNet@2", "Docker@2");
        Assert.Equal(["TASK_ACCOUNTING_MISSING"], Codes(report));
        Assert.True(report.HasBlockers);
    }

    [Fact]
    public async Task Every_source_task_must_be_converted_or_marked_manual()
    {
        var yaml = "# azure-tasks: converted=UseDotNet@2 manual=none\n" + Good;
        Assert.Empty(Codes(await RunAsync(yaml, "UseDotNet@2")));
        var report = await RunAsync(yaml, "usedotnet@2", "Docker@2", "AzureCLI@2");
        Assert.Equal(["TASK_NOT_ACCOUNTED", "TASK_NOT_ACCOUNTED"], Codes(report));
        Assert.Contains(report.Findings, f => f.Message.Contains("Docker@2"));
    }

    [Fact]
    public async Task A_manual_task_must_carry_the_marker_next_to_its_name()
    {
        var withoutMarker = "# azure-tasks: converted=none manual=PublishTestResults@2\n" + Good;
        Assert.Equal(["MANUAL_MARKER_MISSING"], Codes(await RunAsync(withoutMarker, "PublishTestResults@2")));

        var withMarker = "# azure-tasks: converted=none manual=PublishTestResults@2\n" + Good.Replace("run: dotnet build --configuration Release",
            "run: echo \"::error::MANUAL PORT REQUIRED: PublishTestResults@2\" && exit 1");
        Assert.Empty(Codes(await RunAsync(withMarker, "PublishTestResults@2")));

        // the marker for another task does not count, and neither does the name without the marker
        var wrong = "# azure-tasks: converted=none manual=Docker@2,PublishTestResults@2\n# MANUAL PORT REQUIRED: Docker@2\n# PublishTestResults@2 is hard\n" + Good;
        Assert.Equal(["MANUAL_MARKER_MISSING"], Codes(await RunAsync(wrong, "Docker@2", "PublishTestResults@2")));
    }

    [Fact]
    public async Task Listed_tasks_that_are_not_in_the_source_and_double_listing_are_warnings()
    {
        var yaml = "# azure-tasks: converted=UseDotNet@2,Invented@9 manual=UseDotNet@2\n# MANUAL PORT REQUIRED: UseDotNet@2\n" + Good;
        var report = await RunAsync(yaml, "UseDotNet@2");
        Assert.Equal(["TASK_ACCOUNTING_AMBIGUOUS", "TASK_ACCOUNTING_UNKNOWN_TASK"], Codes(report));
        Assert.All(report.Findings.Where(f => f.Gate == TaskAccountingGate.GateName), f => Assert.Equal(GateSeverity.Warning, f.Severity));
    }

    [Fact]
    public async Task The_header_may_be_split_over_several_lines_and_none_means_empty()
    {
        var yaml = "# azure-tasks: converted=UseDotNet@2\n# azure-tasks: manual=none converted=Docker@2\n" + Good;
        Assert.Empty(Codes(await RunAsync(yaml, "UseDotNet@2", "Docker@2")));
    }

    [Fact]
    public async Task A_header_written_as_a_workflow_value_instead_of_a_comment_does_not_count()
    {
        var yaml = Good.Replace("name: orion-api build", "name: \"# azure-tasks: converted=UseDotNet@2 manual=none\"");
        Assert.Equal(["TASK_ACCOUNTING_MISSING"], Codes(await RunAsync(yaml, "UseDotNet@2")));
    }

    [Fact]
    public void The_inventory_finds_tasks_the_product_analyser_misses_inside_conditional_insertions()
    {
        const string source = """
            steps:
            - task: TerraformInstaller@1
            - ${{ if eq(parameters.env, 'prod') }}:
              - task: AzureCLI@2
                displayName: apply
            - script: echo hi
            - task: Kubernetes@1 # roll
            """;
        Assert.Equal(["AzureCLI@2", "Kubernetes@1", "TerraformInstaller@1"], SourceTaskInventory.From(source).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(SourceTaskInventory.From(null));
        Assert.Empty(SourceTaskInventory.From("not: a pipeline\n"));
    }
}
