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
}
