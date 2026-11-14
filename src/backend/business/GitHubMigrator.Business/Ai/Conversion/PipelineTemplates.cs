using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitHubMigrator.Business.WorkflowGates;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business;

public sealed record RepositoryFile(string? Content, bool TooLarge);

// Reads one file of one Git repository of the SAME Azure DevOps organization the pipeline comes from, with GET requests and the source connection's own credential. `versionType` is
// "branch" or "tag" (null: the default branch). Nothing else about the repository is read.
public interface IRepositoryFileReader
{
    Task<RepositoryFile> ReadRepositoryFileAsync(string organization, string projectId, string repository, string path, string? versionType, string? version, string token, CancellationToken ct);
}

// One template file that was read, redacted and is sent to the model with the pipeline. Text is already redacted; the tasks were found in the text as it was (they are names, never values).
public sealed record TemplateFile(string Repository, string Path, string? Ref, string Text, string Sha256, int Bytes, string ReferencedFrom,
    IReadOnlyList<string> Tasks, IReadOnlyList<string> RedactionKinds);

// A template that is NOT sent, with a stable reason: the model is told it cannot see it and the person reviewing is told which it was.
public sealed record UnresolvedTemplate(string Reference, string Reason);

public sealed record ResolvedTemplates(IReadOnlyList<TemplateFile> Files, IReadOnlyList<UnresolvedTemplate> Unresolved)
{
    public static ResolvedTemplates None { get; } = new([], []);
    public bool IsEmpty => Files.Count == 0 && Unresolved.Count == 0;
}

// What the proposal keeps of the templates that were sent (never their text): enough for a person to see exactly which files left the source.
public sealed record TemplateProvenance(string Repository, string Path, string? Ref, string Sha256, int Bytes);

public sealed record TemplateLimits(int MaxFiles = 12, int MaxDepth = 3, int MaxFileChars = 100_000, int MaxTotalBytes = 100_000, int MaxReads = 30);

// Where the pipeline's own file is: local templates are read from the same repository, relative to the file that includes them.
public sealed record TemplateRoot(string Project, string? RepositoryId, string? Path);

// Finds the templates a pipeline includes and reads them, bounded: local templates and the ones of `type: git` repository resources of the same organization, at most a few files, a few levels
// deep, only YAML files, only plain paths. Everything it cannot or must not read is reported with a reason instead of being skipped. It reads nothing but files it was pointed at by the
// pipeline's own text; a path is never taken from anywhere else and never leaves the repository it is relative to.
public static partial class PipelineTemplates
{
    public const string ReasonExpression = "ExpressionPath";
    public const string ReasonPath = "PathNotSupported";
    public const string ReasonNotYaml = "NotYaml";
    public const string ReasonEscapes = "EscapesRepository";
    public const string ReasonUnknownAlias = "UnknownRepositoryAlias";
    public const string ReasonRepositoryType = "RepositoryTypeNotSupported";
    public const string ReasonRepositoryName = "RepositoryNameNotSupported";
    public const string ReasonRef = "RefNotSupported";
    public const string ReasonNotReadable = "NotReadable";
    public const string ReasonTooLarge = "TooLarge";
    public const string ReasonRedaction = "RedactionBlocked";
    public const string ReasonTooMany = "TooMany";
    public const string ReasonTooDeep = "TooDeep";
    public const string ReasonBudget = "BudgetExceeded";
    public const string ReasonNoReader = "SourceDoesNotReadFiles";
    public const string ReasonRootUnknown = "RootLocationUnknown";

    private const int MaxChars = 200_000;
    private const int MaxDepthOfYaml = 30;
    private const int MaxNodes = 20_000;
    private const int MaxReferencesPerFile = 40;

    [GeneratedRegex(@"^[A-Za-z0-9._/ -]{1,250}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PlainPath();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Alias();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,99}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Segment();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RefName();

    public sealed record RepositoryResource(string Alias, string Type, string? Name, string? Ref);

    // ---- reading the pipeline's own text ---------------------------------------------------------------------------------------------------

    // Every `template:` value (a plain string) anywhere in the document, in document order, and the repository resources the document declares.
    public static (IReadOnlyList<string> References, IReadOnlyDictionary<string, RepositoryResource> Repositories) Extract(string? yaml)
    {
        var references = new List<string>();
        var repositories = new Dictionary<string, RepositoryResource>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxChars) return (references, repositories);
        YamlStream stream;
        try { stream = SafeYaml.Load(yaml); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or ArgumentException) { return (references, repositories); }
        if (stream.Documents.Count != 1) return (references, repositories);

        var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        void Walk(YamlNode node, int depth)
        {
            if (depth > MaxDepthOfYaml || visited.Count >= MaxNodes || !visited.Add(node)) return;
            if (node is YamlMappingNode map)
                foreach (var pair in map.Children)
                {
                    if (pair.Key is YamlScalarNode { Value: "template" } && pair.Value is YamlScalarNode { Value: { } value } && value.Trim().Length > 0)
                    {
                        if (!references.Contains(value.Trim(), StringComparer.Ordinal) && references.Count < MaxReferencesPerFile) references.Add(value.Trim());
                        continue;
                    }
                    Walk(pair.Value, depth + 1);
                }
            else if (node is YamlSequenceNode sequence)
                foreach (var child in sequence.Children) Walk(child, depth + 1);
        }
        Walk(stream.Documents[0].RootNode, 0);

        if (stream.Documents[0].RootNode is YamlMappingNode root &&
            Child(root, "resources") is YamlMappingNode resources && Child(resources, "repositories") is YamlSequenceNode list)
            foreach (var item in list.Children.OfType<YamlMappingNode>().Take(50))
                if (Child(item, "repository") is YamlScalarNode { Value: { } alias } && Alias().IsMatch(alias.Trim()))
                    repositories[alias.Trim()] = new(alias.Trim(), (Child(item, "type") as YamlScalarNode)?.Value?.Trim() ?? "", // Azure requires a type: a resource without one is not read
                        (Child(item, "name") as YamlScalarNode)?.Value?.Trim(), (Child(item, "ref") as YamlScalarNode)?.Value?.Trim());
        return (references, repositories);
    }

    private static YamlNode? Child(YamlMappingNode map, string key) => map.Children.FirstOrDefault(p => p.Key is YamlScalarNode s && s.Value == key).Value;

    // ---- a path, as the pipeline wrote it ----------------------------------------------------------------------------------------------------

    // The path of a template reference as a repository-rooted path ("/a/b.yml") and the repository alias it names, or why it cannot be read. A path without an alias is relative to the directory
    // of the file that includes it ("/x" is rooted); a path with an alias is rooted in that repository. It never leaves the repository, is a plain path and names a YAML file.
    public static (string? Path, string? Alias, string? Reason) Parse(string raw, string includerDirectory)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return (null, null, ReasonPath);
        if (text.Contains("${{", StringComparison.Ordinal) || text.Contains("$(", StringComparison.Ordinal) || text.Contains("$[", StringComparison.Ordinal)) return (null, null, ReasonExpression);

        string? alias = null;
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            alias = text[(at + 1)..];
            text = text[..at];
            if (!Alias().IsMatch(alias)) return (null, null, ReasonPath);
        }
        text = text.Replace('\\', '/');
        if (!PlainPath().IsMatch(text)) return (null, alias, ReasonPath);

        var stack = new List<string>();
        if (!(text.StartsWith('/') || alias is not null)) stack.AddRange(includerDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count == 0) return (null, alias, ReasonEscapes);
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (segment.Trim().Length == 0) return (null, alias, ReasonPath);
            stack.Add(segment);
        }
        if (stack.Count == 0) return (null, alias, ReasonPath);
        var path = "/" + string.Join('/', stack);
        if (!path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) return (null, alias, ReasonNotYaml);
        return (path, alias, null);
    }

    // "refs/heads/main", "refs/tags/v1" or a plain branch name; anything else is not read.
    public static (string? Type, string? Version, bool Ok) ParseRef(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return (null, null, true);
        var type = "branch";
        if (text.StartsWith("refs/heads/", StringComparison.Ordinal)) text = text["refs/heads/".Length..];
        else if (text.StartsWith("refs/tags/", StringComparison.Ordinal)) { type = "tag"; text = text["refs/tags/".Length..]; }
        else if (text.StartsWith("refs/", StringComparison.Ordinal)) return (null, null, false);
        if (!RefName().IsMatch(text) || text.Contains("..", StringComparison.Ordinal) || text.EndsWith('/') || text.Contains("//", StringComparison.Ordinal)) return (null, null, false);
        return (type, text, true);
    }

    // "Project/Repository" or "Repository" (the project of the pipeline); never anything else.
    public static (string? Project, string? Repository) ParseRepositoryName(string? raw, string defaultProject)
    {
        var parts = (raw ?? "").Trim().Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(p => !Segment().IsMatch(p.Trim()))) return (null, null);
        return parts.Length == 1 ? (defaultProject, parts[0].Trim()) : (parts[0].Trim(), parts[1].Trim());
    }

    // ---- reading them ------------------------------------------------------------------------------------------------------------------------

    private sealed record Place(string Key, string Project, string Repository, string? Type, string? Version, string Display);

    public static async Task<ResolvedTemplates> ResolveAsync(string rootYaml, TemplateRoot root, IRepositoryFileReader? reader, string organization, string token,
        TemplateLimits limits, CancellationToken ct)
    {
        var (rootReferences, repositories) = Extract(rootYaml);
        if (rootReferences.Count == 0) return ResolvedTemplates.None;

        var files = new List<TemplateFile>();
        var unresolved = new List<UnresolvedTemplate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reads = 0;
        var bytes = 0;
        var self = root.RepositoryId is { Length: > 0 } id ? new Place("self", root.Project, id, null, null, "self") : null;
        var rootDirectory = root.Path is { Length: > 0 } p ? DirectoryOf(p.Replace('\\', '/')) : null;

        void Skip(string reference, string reason)
        {
            var shown = Printable(reference);
            if (!unresolved.Any(u => u.Reference == shown && u.Reason == reason)) unresolved.Add(new(shown, reason));
        }

        var queue = new Queue<(IReadOnlyList<string> References, Place? Place, string? Directory, int Depth, string From)>();
        queue.Enqueue((rootReferences, self, rootDirectory, 1, "pipeline"));
        while (queue.Count > 0)
        {
            var (references, includerPlace, includerDirectory, depth, from) = queue.Dequeue();
            foreach (var reference in references)
            {
                ct.ThrowIfCancellationRequested();
                // where does this reference point?
                var (path, alias, reason) = Parse(reference, includerDirectory ?? "/");
                if (reason is not null) { Skip(reference, reason); continue; }

                Place? place;
                if (alias is null || alias.Equals("self", StringComparison.OrdinalIgnoreCase))
                {
                    place = alias is null ? includerPlace : self;
                    // a path relative to the including file needs to know where that file is; a rooted path (or an @self one) only needs the repository
                    var relative = alias is null && !reference.TrimStart().StartsWith('/') && !reference.TrimStart().StartsWith('\\');
                    if (place is null || (relative && includerDirectory is null)) { Skip(reference, ReasonRootUnknown); continue; }
                }
                else
                {
                    if (!repositories.TryGetValue(alias, out var resource)) { Skip(reference, ReasonUnknownAlias); continue; }
                    if (!resource.Type.Equals("git", StringComparison.OrdinalIgnoreCase)) { Skip(reference, ReasonRepositoryType); continue; }
                    var (project, repository) = ParseRepositoryName(resource.Name, root.Project);
                    if (project is null || repository is null) { Skip(reference, ReasonRepositoryName); continue; }
                    var (type, version, ok) = ParseRef(resource.Ref);
                    if (!ok) { Skip(reference, ReasonRef); continue; }
                    place = new($"{project}/{repository}@{type}:{version}", project, repository, type, version, $"{project}/{repository}");
                }

                var key = $"{place!.Key}|{path}";
                if (!seen.Add(key)) continue;
                if (depth > limits.MaxDepth) { Skip(reference, ReasonTooDeep); continue; }
                if (files.Count >= limits.MaxFiles) { Skip(reference, ReasonTooMany); continue; }
                if (reader is null) { Skip(reference, ReasonNoReader); continue; }
                if (++reads > limits.MaxReads) { Skip(reference, ReasonTooMany); continue; }

                RepositoryFile read;
                try { read = await reader.ReadRepositoryFileAsync(organization, place.Project, place.Repository, path!, place.Type, place.Version, token, ct); }
                catch (HttpRequestException) { Skip(reference, ReasonNotReadable); continue; }
                if (read.TooLarge) { Skip(reference, ReasonTooLarge); continue; }
                if (string.IsNullOrWhiteSpace(read.Content)) { Skip(reference, ReasonNotReadable); continue; }

                var redaction = SourceRedactor.Redact(read.Content, limits.MaxFileChars);
                if (redaction.Blocked) { Skip(reference, ReasonRedaction); continue; }
                if (redaction.Truncated) { Skip(reference, ReasonTooLarge); continue; }
                var size = Encoding.UTF8.GetByteCount(redaction.Text);
                if (bytes + size > limits.MaxTotalBytes) { Skip(reference, ReasonBudget); continue; }

                bytes += size;
                var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(redaction.Text)));
                var versionLabel = place.Type is null ? null : $"{(place.Type == "tag" ? "refs/tags/" : "refs/heads/")}{place.Version}";
                files.Add(new(place.Display, path!, versionLabel, redaction.Text, sha, size, from, SourceTaskInventory.From(read.Content).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    redaction.Findings.Select(f => f.Kind).ToArray()));

                // what this file includes in turn is relative to ITS directory (and its repository), and one level deeper
                var (nested, _) = Extract(read.Content);
                if (nested.Count > 0) queue.Enqueue((nested, place, DirectoryOf(path!), depth + 1, $"{place.Display}:{path}"));
            }
        }
        return new(files, unresolved);
    }

    private static string DirectoryOf(string path)
    {
        var at = path.LastIndexOf('/');
        return at <= 0 ? "/" : path[..at];
    }

    // The reference as the pipeline wrote it, made safe to show and to put in a prompt: one printable line, bounded.
    public static string Printable(string reference)
    {
        var clean = new string((reference ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 120 ? clean[..120] : clean;
    }
}
