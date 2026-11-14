using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureYamlTaskAnalyzerTests
{
    [Fact]
    public void Extracts_only_structural_step_names_not_script_contents_or_inline_credentials()
    {
        var yaml = """
            trigger:
              - main
            pool:
              vmImage: ubuntu-latest
            stages:
            - stage: Build
              jobs:
              - job: Compile
                steps:
                - task: UseDotNet@2
                  inputs:
                    version: '8.x'
                - script: |
                    echo MY_SECRET_VALUE_123
                    curl https://secret.invalid?token=private-value
                  displayName: Danger
                - task: DotNetCoreCLI@2
                  inputs:
                    command: build
                    arguments: '--password=super-secret'
            """;
        var result = AzureYamlTaskAnalyzer.Analyze(yaml);
        Assert.False(result.ParseFailed);
        Assert.False(result.Truncated);
        Assert.Equal(3, result.Tasks.Count);
        Assert.Equal("UseDotNet@2", result.Tasks[0].Identifier);
        Assert.Equal("script", result.Tasks[1].Identifier);
        Assert.Equal("DotNetCoreCLI@2", result.Tasks[2].Identifier);
        var serial = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("MY_SECRET_VALUE", serial);
        Assert.DoesNotContain("super-secret", serial);
        Assert.DoesNotContain("private-value", serial);
        Assert.DoesNotContain("Danger", serial);
    }

    [Fact]
    public void External_templates_remain_explicit_and_invalid_task_tokens_cannot_enter_generated_yaml()
    {
        var yaml = """
            steps:
              - template: some.yml@templates
              - task: "'; run: dangerous"
                inputs:
                  endpointPassword: should-never-appear
            """;
        var structure = AzureYamlTaskAnalyzer.Analyze(yaml);
        Assert.Equal("external-template", structure.Tasks[0].Identifier);
        Assert.Equal("unrecognized-task", structure.Tasks[1].Identifier);
        Assert.DoesNotContain("should-never-appear", System.Text.Json.JsonSerializer.Serialize(structure));
    }

    [Fact]
    public void Oversized_and_invalid_yaml_mark_analysis_unavailable_instead_of_claiming_complete_tasks()
    {
        var oversized = AzureYamlTaskAnalyzer.Analyze(new string('x', 151_000));
        Assert.True(oversized.ParseFailed);
        Assert.True(oversized.Truncated);
        Assert.Empty(oversized.Tasks);
        var malformed = AzureYamlTaskAnalyzer.Analyze("steps: [a, b");
        Assert.True(malformed.ParseFailed);
    }

    [Fact]
    public void Supports_root_level_jobs_and_direct_steps()
    {
        var structure = AzureYamlTaskAnalyzer.Analyze("""
            jobs:
            - job: Build
              steps:
              - checkout: self
              - bash: |
                  echo hi
            """);
        Assert.False(structure.ParseFailed);
        Assert.Equal(["checkout", "script"], structure.Tasks.Select(x => x.Kind).ToArray());
    }

    private static string[] Seen(string yaml) => AzureYamlTaskAnalyzer.Analyze(yaml).Tasks.Select(t => t.Kind + ":" + t.Identifier).ToArray();

    [Fact]
    public void Conditional_insertions_in_a_steps_list_are_followed_in_order_with_else_each_and_nesting_and_their_conditions_are_never_returned()
    {
        var yaml = """
            steps:
            - task: A@1
            - ${{ if eq(parameters.env, 'prod-secret-condition') }}:
              - task: B@1
              - ${{ if eq(parameters.deep, true) }}:
                - task: C@1
            - ${{ else }}:
              - task: D@1
            - ${{ each e in parameters.environments }}:
              - script: echo
            - task: E@1
            """;
        var structure = AzureYamlTaskAnalyzer.Analyze(yaml);
        Assert.Equal(["task:A@1", "task:B@1", "task:C@1", "task:D@1", "script:script", "task:E@1"], Seen(yaml));
        var serial = System.Text.Json.JsonSerializer.Serialize(structure);
        Assert.DoesNotContain("prod-secret-condition", serial);
        Assert.DoesNotContain("parameters", serial);
        Assert.False(structure.Truncated);
    }

    [Fact]
    public void What_a_template_expression_computes_is_reported_as_unknown_instead_of_being_skipped()
    {
        Assert.Equal(["task:A@1", "unknown:computed-steps"], Seen("steps:\n- task: A@1\n- ${{ step }}\n"));
        Assert.Equal(["unknown:computed-steps"], Seen("steps: ${{ parameters.steps }}\n"));
        Assert.Equal(["unknown:computed-steps"], Seen("steps:\n- ${{ if eq(1, 1) }}: ${{ parameters.steps }}\n"));
        Assert.Equal(["unknown:computed-jobs"], Seen("jobs: ${{ parameters.jobs }}\n"));
        Assert.Equal(["unknown:computed-stages"], Seen("stages: ${{ parameters.stages }}\n"));
        // variables that are an expression carry no tasks and are not reported
        Assert.Empty(Seen("variables: ${{ parameters.vars }}\n"));
        // a plain mapping of variables is not a list of templates
        Assert.Empty(Seen("variables:\n  template: not-a-template-here\n"));
    }

    [Fact]
    public void The_step_shortcuts_and_unrecognised_steps_are_reported_by_kind_and_never_with_their_values()
    {
        var yaml = """
            steps:
            - publish: $(Build.ArtifactStagingDirectory)/secret-path-value
              artifact: drop
            - download: current
            - downloadBuild: specific-build-123
            - getPackage: my-feed-credential
            - reviewApp: environment-x
            - displayName: a step with no recognised key
              foo: bar
            - task:
                nested: mapping
            """;
        Assert.Equal(["shortcut:publish", "shortcut:download", "shortcut:downloadBuild", "shortcut:getPackage", "shortcut:reviewApp", "unknown:unrecognized-step", "unknown:unrecognized-task"], Seen(yaml));
        var serial = System.Text.Json.JsonSerializer.Serialize(AzureYamlTaskAnalyzer.Analyze(yaml));
        foreach (var value in new[] { "secret-path-value", "specific-build-123", "my-feed-credential", "environment-x", "a step with no recognised key", "nested" })
            Assert.DoesNotContain(value, serial);
    }

    [Fact]
    public void Templates_that_pull_in_jobs_stages_variables_or_a_base_pipeline_are_counted_once_each_wherever_they_are()
    {
        var yaml = """
            extends:
              template: base.yml@templates
              parameters:
                template: not-a-template-either
            variables:
            - group: shared
            - template: vars.yml
            - ${{ if eq(parameters.x, true) }}:
              - template: more-vars.yml
              - name: a
                value: b
            stages:
            - template: stages/deploy.yml@templates
            - stage: S
              jobs:
              - template: jobs/build.yml
              - job: J
                steps:
                - template: steps/one.yml
                - task: A@1
            """;
        Assert.Equal(["template", "template", "template", "template", "template", "template", "task"], Seen(yaml).Select(x => x.Split(':')[0]));
        Assert.Equal(6, AzureYamlTaskAnalyzer.Analyze(yaml).Tasks.Count(t => t.Kind == "template"));
    }

    [Fact]
    public void A_list_of_steps_used_by_two_jobs_through_an_alias_is_counted_for_each_job_as_before()
    {
        var yaml = """
            jobs:
            - job: One
              steps: &common
              - task: A@1
            - job: Two
              steps: *common
            """;
        Assert.Equal(["task:A@1", "task:A@1"], Seen(yaml));
    }

    [Fact]
    public void Aliases_that_refer_to_one_another_through_insertions_cannot_make_a_small_document_expensive()
    {
        // each level holds the one before it twice under an insertion and nothing ever yields a task, so no cap ends the walk early: read naively that is 2^23 visits
        var lines = new List<string> { "steps:", "- ${{ if a }}: &l0", "  - ${{ if x }}: []" };
        for (var level = 1; level <= 23; level++)
            lines.Add($"- ${{{{ if a }}}}: &l{level}\n  - ${{{{ if b }}}}: *l{level - 1}\n  - ${{{{ if c }}}}: *l{level - 1}");
        lines.Add("- task: Last@1");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var structure = AzureYamlTaskAnalyzer.Analyze(string.Join("\n", lines));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.False(structure.ParseFailed);
        Assert.Equal(["task:Last@1"], structure.Tasks.Select(t => t.Kind + ":" + t.Identifier));
    }

    [Fact]
    public void Insertions_nested_deeper_than_the_limit_and_more_steps_than_the_cap_mark_the_inventory_incomplete()
    {
        var deep = "steps:\n" + string.Concat(Enumerable.Range(0, 40).Select(i => new string(' ', i * 2) + "- ${{ if x }}:\n")) + new string(' ', 80) + "- task: Deep@1\n";
        var tooDeep = AzureYamlTaskAnalyzer.Analyze(deep);
        Assert.True(tooDeep.Truncated);
        Assert.DoesNotContain(tooDeep.Tasks, t => t.Identifier == "Deep@1"); // beyond the limit nothing is read, and the inventory says it is incomplete

        var many = "steps:\n" + string.Concat(Enumerable.Range(0, 200).Select(i => $"- task: T{i}@1\n"));
        var capped = AzureYamlTaskAnalyzer.Analyze(many);
        Assert.Equal(150, capped.Tasks.Count);
        Assert.True(capped.Truncated);
        var insertedMany = "steps:\n- ${{ if x }}:\n" + string.Concat(Enumerable.Range(0, 200).Select(i => $"  - task: T{i}@1\n"));
        Assert.True(AzureYamlTaskAnalyzer.Analyze(insertedMany).Truncated);
    }
}
