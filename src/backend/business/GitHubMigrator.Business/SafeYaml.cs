using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business;

public sealed class YamlNestingException(int maxDepth) : InvalidOperationException($"The YAML nests deeper than {maxDepth} levels.")
{
    public int MaxDepth { get; } = maxDepth;
}

// Loads YAML the product does not control (a pipeline from the source repository, a workflow a model wrote, a stored proposal) without letting its nesting decide whether
// the process survives. YamlDotNet builds the node tree recursively and has no depth limit of its own: about 24,000 nested sequences (a 50 KB document) overflow the stack,
// which no catch can stop and which ends the whole API process. The parser's event stream is read by a loop, not by recursion, so the depth is measured there first and a
// document that nests too deeply is refused before any node is built. Real pipelines and workflows nest a dozen levels; the limit is far above that and far below any danger.
public static class SafeYaml
{
    public const int MaxDepth = 100;

    // Exactly `new YamlStream().Load(text)` for a document that is not too deep; YamlNestingException (an InvalidOperationException) for one that is, YamlException for a syntax error.
    public static YamlStream Load(string text, int maxDepth = MaxDepth)
    {
        EnsureShallow(text, maxDepth);
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream;
    }

    public static void EnsureShallow(string text, int maxDepth = MaxDepth)
    {
        var parser = new Parser(new StringReader(text));
        var depth = 0;
        while (parser.MoveNext())
        {
            if (parser.Current is SequenceStart or MappingStart) { if (++depth > maxDepth) throw new YamlNestingException(maxDepth); }
            else if (parser.Current is SequenceEnd or MappingEnd) depth--;
        }
    }
}
