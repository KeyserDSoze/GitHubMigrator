using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business;

// The extra runner labels a self-hosted pool demands, read from the pipeline's own YAML (`pool:` with `demands:`), by the product and never by a model. They exist so that
// actionlint, which knows only GitHub's labels, does not reject `runs-on: [self-hosted, linux, docker, npm]` for labels the SOURCE asked for. A label here lets a workflow pass
// that one check; it creates no runner, and the SELF_HOSTED_RUNNER warning still asks a person to confirm that the destination has runners with these labels.
public static class AzureRunnerLabels
{
    public const int MaxLabels = 16;
    private const int MaxChars = 150_000;
    private const int MaxDepth = 24;

    // a plain capability name: `docker`, `npm`, `python3`. Anything with a space or an operator (`Agent.OS -equals Linux`), a variable or an expression is not a label.
    private static readonly Regex Label = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,49}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // labels every self-hosted runner has or GitHub already knows: nothing to add
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        { "self-hosted", "linux", "windows", "macos", "x64", "x86", "arm", "arm64", "ubuntu-latest", "windows-latest", "macos-latest" };

    public static IReadOnlyList<string> From(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxChars) return [];
        var labels = new List<string>();
        try
        {
            var stream = SafeYaml.Load(yaml);
            if (stream.Documents.Count != 1) return [];
            var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);

            void Add(YamlNode? demand)
            {
                if (demand is not YamlScalarNode { Value: { } text } || labels.Count >= MaxLabels) return;
                text = text.Trim();
                if (!Label.IsMatch(text) || Known.Contains(text) || labels.Contains(text, StringComparer.OrdinalIgnoreCase)) return;
                labels.Add(text);
            }

            void Walk(YamlNode node, int depth)
            {
                if (depth > MaxDepth || visited.Count >= 20_000 || !visited.Add(node)) return;
                if (node is YamlMappingNode mapping)
                    foreach (var pair in mapping.Children)
                    {
                        // a pool with a vmImage is Microsoft-hosted and has no demands to speak of: only named pools count
                        if (pair.Key is YamlScalarNode { Value: "pool" } && pair.Value is YamlMappingNode pool &&
                            !pool.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == "vmImage"))
                        {
                            var demands = pool.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: "demands" }).Value;
                            if (demands is YamlSequenceNode list) foreach (var item in list.Children) Add(item);
                            else Add(demands);
                        }
                        Walk(pair.Value, depth + 1);
                    }
                else if (node is YamlSequenceNode sequence)
                    foreach (var child in sequence.Children) Walk(child, depth + 1);
            }
            Walk(stream.Documents[0].RootNode, 0);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException) { return []; }
        return labels;
    }
}
