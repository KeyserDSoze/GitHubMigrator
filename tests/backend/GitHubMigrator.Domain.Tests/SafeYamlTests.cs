using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Domain.Tests.WorkflowGates;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Domain.Tests;

// YAML the product does not control must not be able to end the process by how deeply it nests: YamlDotNet builds its node tree recursively and ~24,000 nested
// sequences (a 50 KB document) overflow the stack, which no catch stops. These tests would take the whole test host down if any parse site loaded such a document.
public sealed class SafeYamlTests
{
    private static string Flow(int depth) => new string('[', depth) + new string(']', depth);
    private static string Maps(int depth) => string.Concat(Enumerable.Repeat("{a: ", depth)) + "1" + new string('}', depth);
    private static string Block(int depth) => string.Concat(Enumerable.Repeat("- ", depth)) + "x\n";

    [Fact]
    public void A_document_nested_exactly_as_deep_as_the_limit_loads_and_one_level_more_is_refused()
    {
        Assert.Single(SafeYaml.Load(Flow(SafeYaml.MaxDepth)).Documents);
        Assert.Single(SafeYaml.Load(Maps(SafeYaml.MaxDepth)).Documents);
        Assert.Single(SafeYaml.Load(Block(SafeYaml.MaxDepth)).Documents);
        foreach (var tooDeep in new[] { Flow(SafeYaml.MaxDepth + 1), Maps(SafeYaml.MaxDepth + 1), Block(SafeYaml.MaxDepth + 1) })
            Assert.Equal(SafeYaml.MaxDepth, Assert.Throws<YamlNestingException>(() => SafeYaml.Load(tooDeep)).MaxDepth);
        Assert.Throws<YamlNestingException>(() => SafeYaml.Load(Flow(10), maxDepth: 9));
        Assert.Single(SafeYaml.Load(Flow(10), maxDepth: 10).Documents);
    }

    [Fact]
    public void Nesting_that_would_overflow_the_stack_is_refused_quickly_in_every_shape()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var bomb in new[]
        {
            "steps: " + Flow(70_000), Flow(70_000), Maps(40_000), Block(70_000), "a:\n  - " + Flow(60_000),
            "steps:\n- " + string.Concat(Enumerable.Repeat("{\"${{ if x }}\": [", 30_000)) + string.Concat(Enumerable.Repeat("]}", 30_000))
        })
            Assert.Throws<YamlNestingException>(() => SafeYaml.Load(bomb));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Brackets_in_text_comments_and_block_scalars_are_not_nesting_and_neither_are_aliases_or_wide_documents()
    {
        var quoted = "a: \"" + new string('[', 5_000) + "\"\nb: '" + new string('{', 5_000) + "'\n# " + new string('[', 5_000) + "\nc: |\n  " + new string('[', 5_000) + "\n";
        Assert.Single(SafeYaml.Load(quoted).Documents);
        Assert.Single(SafeYaml.Load("base: &b\n  x: 1\n" + string.Concat(Enumerable.Range(0, 2_000).Select(i => $"k{i}: *b\n"))).Documents);
        Assert.Single(SafeYaml.Load(string.Concat(Enumerable.Range(0, 20_000).Select(i => $"key{i}: [{i}]\n"))).Documents);
        // depth is per document: two documents, each at the limit
        Assert.Equal(2, SafeYaml.Load(Flow(SafeYaml.MaxDepth) + "\n---\n" + Flow(SafeYaml.MaxDepth) + "\n").Documents.Count);
    }

    [Fact]
    public void A_syntax_error_is_still_a_yaml_error_and_a_normal_document_loads_exactly_as_before()
    {
        Assert.ThrowsAny<YamlException>(() => SafeYaml.Load("a: [unclosed\n"));
        Assert.ThrowsAny<YamlException>(() => SafeYaml.Load("a: b: c\n"));
        foreach (var c in OrionCorpus.Load())
            foreach (var text in new[] { c.SourceYaml, c.GoldenYaml })
            {
                var safe = SafeYaml.Load(text);
                var plain = new YamlStream();
                plain.Load(new StringReader(text));
                Assert.Equal(plain.Documents.Count, safe.Documents.Count);
                Assert.Equal(plain.Documents[0].RootNode.ToString(), safe.Documents[0].RootNode.ToString());
                Assert.Equal(((YamlMappingNode)plain.Documents[0].RootNode).Children.Keys.Select(k => k.ToString()), ((YamlMappingNode)safe.Documents[0].RootNode).Children.Keys.Select(k => k.ToString()));
            }
    }

    private static readonly string Bomb = "steps: " + Flow(70_000);

    [Fact]
    public void The_workflow_gate_blocks_a_workflow_that_nests_too_deeply_instead_of_parsing_it()
    {
        var parsed = ParsedWorkflow.TryParse("name: x\non: workflow_dispatch\njobs: " + Flow(70_000) + "\n", out var error);
        Assert.Null(parsed);
        Assert.Equal(("YAML_TOO_DEEP", GateSeverity.Blocker), (error!.Code, error.Severity));
        Assert.Contains("100", error.Message); // the message names the limit, never the document

        var report = new WorkflowGatePipeline([new WorkflowPolicyGate(), new LinterSuppressionGate()], WorkflowGatePolicy.Default)
            .RunAsync("name: x\non: workflow_dispatch\njobs: " + Flow(70_000) + "\n", WorkflowPlanContext.Empty, default).Result;
        Assert.False(report.Approvable);
        Assert.Contains(report.Findings, f => f.Code == "YAML_TOO_DEEP");
    }

    [Fact]
    public void Every_reader_of_untrusted_yaml_survives_it_and_says_it_could_not_read_it()
    {
        var structure = AzureYamlTaskAnalyzer.Analyze(Bomb);
        Assert.True(structure.ParseFailed);
        Assert.Empty(structure.Tasks);
        Assert.Empty(AzureRunnerLabels.From("pool: " + Flow(70_000)));
        Assert.Null(AzureTriggerReader.Read("trigger: " + Flow(70_000)));
        Assert.Equal("WorkflowNotReadable", WorkflowTriggerEditor.Derive("name: x\non: workflow_dispatch\njobs: " + Flow(70_000) + "\n", AzureTriggerReader.Read("trigger:\n  branches:\n    include: [main]\n")!).Error);
        Assert.Empty(SourceTaskInventory.From(Bomb)); // the text scan has nothing to find either, and nothing crashed
    }
}
