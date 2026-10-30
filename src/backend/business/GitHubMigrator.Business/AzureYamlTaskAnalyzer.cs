using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business;

// Structural extraction only: never return task inputs, scripts, variable values or YAML nodes.
public static class AzureYamlTaskAnalyzer
{
    private static readonly Regex Identifier = new(@"^[A-Za-z0-9][A-Za-z0-9._@/-]{0,99}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private const int MaxTasks = 150;

    public static PipelineStructure Analyze(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new([], false, true);
        if (raw.Length > 150_000) return new([], true, true);
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(raw));
            if (stream.Documents.Count != 1) return new([], false, true);
            var found = new List<PipelineTaskEvidence>();
            var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
            bool truncated = false;

            void Walk(YamlNode node, int depth)
            {
                if (depth > 24 || visited.Count >= 20_000) { truncated = true; return; }
                if (!visited.Add(node)) return;
                if (node is YamlMappingNode mapping)
                    foreach (var pair in mapping.Children)
                    {
                        if (pair.Key is YamlScalarNode key && key.Value == "steps" &&
                            pair.Value is YamlSequenceNode steps)
                        {
                            foreach (var step in steps.Children)
                            {
                                if (found.Count >= MaxTasks) { truncated = true; break; }
                                if (step is not YamlMappingNode stepMap) continue;
                                foreach (var item in stepMap.Children)
                                {
                                    if (item.Key is not YamlScalarNode k || k.Value is null) continue;
                                    if (k.Value == "task" && item.Value is YamlScalarNode task)
                                    {
                                        if (!string.IsNullOrWhiteSpace(task.Value) && Identifier.IsMatch(task.Value))
                                            found.Add(new("task", task.Value));
                                        else found.Add(new("unknown", "unrecognized-task"));
                                        break;
                                    }
                                    if (k.Value is "script" or "bash" or "pwsh" or "powershell")
                                    {
                                        found.Add(new("script", k.Value));
                                        break;
                                    }
                                    if (k.Value == "checkout")
                                    {
                                        found.Add(new("checkout", "checkout"));
                                        break;
                                    }
                                    if (k.Value == "template")
                                    {
                                        found.Add(new("template", "external-template"));
                                        break;
                                    }
                                }
                            }
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
