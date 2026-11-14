using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business;

// Structural extraction only: never return task inputs, scripts, variable values, conditions or YAML nodes.
//
// What is reported, in document order: every step of every `steps:` list (a named task, an inline script, a checkout, a template, one of the step shortcuts
// such as publish/download) and every `template:` that pulls in jobs, stages or variables or that a pipeline extends. Conditional insertions (`${{ if }}:`,
// `${{ else }}:`, `${{ each }}:`) are followed into their lists; what a template expression computes cannot be read, so it is reported as unknown rather than
// skipped: an inventory never claims to be complete about something it could not see.
public static class AzureYamlTaskAnalyzer
{
    private static readonly Regex Identifier = new(@"^[A-Za-z0-9][A-Za-z0-9._@/-]{0,99}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private const int MaxTasks = 150;
    private const int MaxDepth = 24;

    private static readonly HashSet<string> Scripts = ["script", "bash", "pwsh", "powershell"];
    // step shortcuts that are not tasks by name: the pipeline artifact and package steps
    private static readonly HashSet<string> Shortcuts = ["publish", "download", "downloadBuild", "getPackage", "reviewApp"];
    // lists whose items may be `- template:` (jobs, stages, variables); `steps` is read separately because its items are steps
    private static readonly HashSet<string> TemplateLists = ["jobs", "stages", "variables"];

    public static PipelineStructure Analyze(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new([], false, true);
        if (raw.Length > 150_000) return new([], true, true);
        try
        {
            var stream = SafeYaml.Load(raw);
            if (stream.Documents.Count != 1) return new([], false, true);
            var found = new List<PipelineTaskEvidence>();
            var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
            // a list reached through a conditional insertion is read once, whichever alias led to it: aliases that refer to one another through insertions could
            // otherwise make a small document expensive to read (the lists of a `steps:` key itself are each read where they are, as before)
            var read = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
            bool truncated = false;

            bool Add(string kind, string identifier)
            {
                if (found.Count >= MaxTasks) { truncated = true; return false; }
                found.Add(new(kind, identifier));
                return true;
            }

            static bool IsInsertion(YamlScalarNode key) => key.Value is { } v && v.StartsWith("${{", StringComparison.Ordinal);
            static bool IsExpression(YamlNode node) => node is YamlScalarNode { Value: { } v } && v.TrimStart().StartsWith("${{", StringComparison.Ordinal);

            void ReadSteps(YamlSequenceNode steps, int depth, bool inserted)
            {
                if (depth > MaxDepth) { truncated = true; return; }
                if (inserted && !read.Add(steps)) return;
                foreach (var step in steps.Children)
                {
                    if (found.Count >= MaxTasks) { truncated = true; return; }
                    switch (step)
                    {
                        case YamlMappingNode map: ReadStep(map, depth); break;
                        // `- ${{ step }}` or any other scalar: steps produced by a template expression, which this reads no further
                        default: Add("unknown", "computed-steps"); break;
                    }
                }
            }

            void ReadStep(YamlMappingNode map, int depth)
            {
                foreach (var item in map.Children)
                {
                    if (item.Key is not YamlScalarNode key || key.Value is null) continue;
                    if (key.Value == "task" && item.Value is YamlScalarNode task)
                    {
                        if (!string.IsNullOrWhiteSpace(task.Value) && Identifier.IsMatch(task.Value)) Add("task", task.Value);
                        else Add("unknown", "unrecognized-task");
                        return;
                    }
                    if (Scripts.Contains(key.Value)) { Add("script", key.Value); return; }
                    if (key.Value == "checkout") { Add("checkout", "checkout"); return; }
                    if (key.Value == "template") { Add("template", "external-template"); return; }
                    if (Shortcuts.Contains(key.Value)) { Add("shortcut", key.Value); return; }
                    if (IsInsertion(key))
                    {
                        // ${{ if }}: / ${{ else }}: / ${{ each }}: with a list of steps under it; anything else under it is not a list this can follow
                        if (item.Value is YamlSequenceNode inner) ReadSteps(inner, depth + 1, inserted: true);
                        else Add("unknown", "computed-steps");
                        return;
                    }
                    if (key.Value == "task") { Add("unknown", "unrecognized-task"); return; }
                }
                Add("unknown", "unrecognized-step");
            }

            void ReadTemplates(YamlSequenceNode list, int depth, bool inserted)
            {
                if (depth > MaxDepth) { truncated = true; return; }
                if (inserted && !read.Add(list)) return;
                foreach (var item in list.Children.OfType<YamlMappingNode>())
                    foreach (var pair in item.Children)
                    {
                        if (pair.Key is not YamlScalarNode key || key.Value is null) continue;
                        if (key.Value == "template") { Add("template", "external-template"); break; }
                        if (IsInsertion(key) && pair.Value is YamlSequenceNode inner) { ReadTemplates(inner, depth + 1, inserted: true); break; }
                    }
            }

            void Walk(YamlNode node, int depth)
            {
                if (depth > MaxDepth || visited.Count >= 20_000) { truncated = true; return; }
                if (!visited.Add(node)) return;
                if (node is YamlMappingNode mapping)
                    foreach (var pair in mapping.Children)
                    {
                        if (pair.Key is YamlScalarNode key && key.Value is { } name)
                        {
                            if (name == "steps")
                            {
                                if (pair.Value is YamlSequenceNode steps) ReadSteps(steps, depth, inserted: false);
                                else if (IsExpression(pair.Value)) Add("unknown", "computed-steps");
                            }
                            else if (TemplateLists.Contains(name))
                            {
                                if (pair.Value is YamlSequenceNode list) ReadTemplates(list, depth, inserted: false);
                                else if (name != "variables" && IsExpression(pair.Value)) Add("unknown", "computed-" + name);
                            }
                            else if (name == "extends" && pair.Value is YamlMappingNode extends &&
                                extends.Children.Any(c => c.Key is YamlScalarNode { Value: "template" } && c.Value is YamlScalarNode))
                                Add("template", "external-template");
                        }
                        Walk(pair.Value, depth + 1);
                    }
                else if (node is YamlSequenceNode list)
                    foreach (var child in list.Children) Walk(child, depth + 1);
            }
            Walk(stream.Documents[0].RootNode, 0);
            return new(found.Take(MaxTasks).ToArray(), truncated, false);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return new([], false, true);
        }
    }
}
