using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business.WorkflowGates;

public static class YamlLines
{
    public static int L(this YamlNode node) => (int)node.Start.Line;
}

public sealed record WorkflowStep(string JobId, int Index, YamlMappingNode Node)
{
    public int Line => Node.L();
    public string? Uses => ParsedWorkflow.Text(Node, "uses");
    public string? Run => ParsedWorkflow.Text(Node, "run");
    public YamlNode? With => ParsedWorkflow.Child(Node, "with");
    public YamlNode? Env => ParsedWorkflow.Child(Node, "env");
    public int RunLine => ParsedWorkflow.Child(Node, "run")?.L() ?? Line;
}

public sealed record WorkflowJob(string Id, YamlMappingNode Node)
{
    public int Line => Node.L();
    public string? Uses => ParsedWorkflow.Text(Node, "uses");
    public YamlNode? RunsOn => ParsedWorkflow.Child(Node, "runs-on");
    public YamlNode? Permissions => ParsedWorkflow.Child(Node, "permissions");
    public IReadOnlyList<WorkflowStep> Steps =>
        ParsedWorkflow.Child(Node, "steps") is YamlSequenceNode seq
            ? seq.Children.OfType<YamlMappingNode>().Select((n, i) => new WorkflowStep(Id, i, n)).ToArray() : [];
    public int StepNodeCount => ParsedWorkflow.Child(Node, "steps") is YamlSequenceNode seq ? seq.Children.Count : 0;
}

// A GitHub Actions workflow as untrusted input. Parsing only builds a node tree: nothing is evaluated or run.
public sealed class ParsedWorkflow
{
    private ParsedWorkflow(YamlMappingNode root) => Root = root;
    public YamlMappingNode Root { get; }

    public static ParsedWorkflow? TryParse(string yaml, out GateFinding? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            error = Fail("EMPTY_WORKFLOW", "The workflow is empty.");
            return null;
        }
        try
        {
            var stream = SafeYaml.Load(yaml);
            if (stream.Documents.Count != 1)
            {
                error = Fail("MULTIPLE_DOCUMENTS", $"A workflow file is exactly one YAML document, found {stream.Documents.Count}.");
                return null;
            }
            if (stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                error = Fail("NOT_A_WORKFLOW", "The YAML root is not a mapping.");
                return null;
            }
            return new ParsedWorkflow(root);
        }
        catch (YamlNestingException ex)
        {
            // refused before any node was built: how deeply a document nests must not decide whether the process survives
            error = Fail("YAML_TOO_DEEP", $"The YAML nests deeper than {ex.MaxDepth} levels; it is not read.");
            return null;
        }
        catch (YamlException ex)
        {
            error = Fail("YAML_PARSE_ERROR", "The YAML does not parse: " + ex.Message.Split('\n')[0], (int)ex.Start.Line);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            error = Fail("YAML_PARSE_ERROR", "The YAML does not parse.");
            return null;
        }
    }

    private static GateFinding Fail(string code, string message, int? line = null) =>
        new("structure", code, GateSeverity.Blocker, message, line);

    public static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.FirstOrDefault(p => p.Key is YamlScalarNode s && s.Value == key).Value;

    public static string? Text(YamlMappingNode map, string key) => (Child(map, key) as YamlScalarNode)?.Value;

    public YamlNode? Triggers => Child(Root, "on");
    public YamlNode? Permissions => Child(Root, "permissions");

    public IReadOnlyList<(string Name, int Line)> TriggerNames() => Triggers switch
    {
        YamlScalarNode s when s.Value is { } v => [(v, s.L())],
        YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Where(n => n.Value is not null).Select(n => (n.Value!, n.L())).ToArray(),
        YamlMappingNode map => map.Children.Where(p => p.Key is YamlScalarNode { Value: not null })
            .Select(p => (((YamlScalarNode)p.Key).Value!, (int)p.Key.L())).ToArray(),
        _ => []
    };

    public IReadOnlyList<WorkflowJob> Jobs() =>
        Child(Root, "jobs") is YamlMappingNode jobs
            ? jobs.Children.Where(p => p.Key is YamlScalarNode && p.Value is YamlMappingNode)
                .Select(p => new WorkflowJob(((YamlScalarNode)p.Key).Value ?? "", (YamlMappingNode)p.Value)).ToArray()
            : [];

    public bool HasJobsSection => Child(Root, "jobs") is YamlMappingNode;

    // Every scalar value in a subtree (used to look for expressions and URLs).
    public static IEnumerable<YamlScalarNode> Scalars(YamlNode? node)
    {
        switch (node)
        {
            case YamlScalarNode scalar: yield return scalar; break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children) foreach (var s in Scalars(child)) yield return s;
                break;
            case YamlMappingNode map:
                foreach (var pair in map.Children)
                {
                    foreach (var s in Scalars(pair.Key)) yield return s;
                    foreach (var s in Scalars(pair.Value)) yield return s;
                }
                break;
        }
    }
}
