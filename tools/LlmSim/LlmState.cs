using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmSim;

public sealed class RecipeEntry(Recipe recipe, string source, Regex? regex)
{
    public Recipe Recipe { get; } = recipe;
    public string Source { get; } = source;
    public Regex? Regex { get; } = regex;
    public int Used { get; set; }
}

public sealed class QueuedReply(Reply reply)
{
    public Reply Reply { get; } = reply;
    public int Remaining { get; set; } = reply.Repeat ?? 1;
}

public sealed class LlmStats
{
    public long Total { get; set; }
    public long Streamed { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public SortedDictionary<string, long> ByStatus { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ByModel { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ByMatched { get; } = new(StringComparer.Ordinal);
}

// Embedded answers live under seed/files; a relative path may also point next to the recipes file (never outside it).
public sealed class ContentStore(string? baseDirectory)
{
    private const string Prefix = "seed/files/";
    private static readonly Lazy<Dictionary<string, string>> Embedded = new(() =>
    {
        var assembly = typeof(ContentStore).Assembly;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normal = name.Replace('\\', '/');
            if (normal.StartsWith(Prefix, StringComparison.Ordinal)) names[normal[Prefix.Length..]] = name;
        }
        return names;
    });

    public static string ReadEmbedded(string logicalName)
    {
        var assembly = typeof(ContentStore).Assembly;
        var manifest = assembly.GetManifestResourceNames().First(n => n.Replace('\\', '/') == logicalName);
        using var reader = new StreamReader(assembly.GetManifestResourceStream(manifest)!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static IReadOnlyCollection<string> EmbeddedNames => Embedded.Value.Keys;

    private string? FilePath(string name)
    {
        if (baseDirectory is null) return null;
        var full = Path.GetFullPath(Path.Combine(baseDirectory, name));
        var relative = Path.GetRelativePath(baseDirectory, full);
        var outside = relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return outside || Path.IsPathRooted(relative) ? null : full;
    }

    public string? Validate(string name)
    {
        var key = name.Replace('\\', '/').TrimStart('/');
        if (Embedded.Value.ContainsKey(key)) return null;
        var path = FilePath(name);
        if (path is null)
            return baseDirectory is null
                ? $"contentFrom '{name}' is not an embedded file and no recipes file directory is configured."
                : $"contentFrom '{name}' must be an embedded file or stay inside the recipes file directory.";
        return File.Exists(path) ? null : $"contentFrom '{name}' was not found (embedded files: seed/files/**; or a file next to the recipes file).";
    }

    public string Read(string name)
    {
        var key = name.Replace('\\', '/').TrimStart('/');
        if (Embedded.Value.TryGetValue(key, out var manifest))
        {
            using var reader = new StreamReader(typeof(ContentStore).Assembly.GetManifestResourceStream(manifest)!, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return File.ReadAllText(FilePath(name) ?? throw new FileNotFoundException(name), Encoding.UTF8);
    }
}

public static class ReplyRules
{
    public static readonly HashSet<int> Statuses = [400, 401, 403, 404, 408, 413, 429, 500, 502, 503];
    private static readonly HashSet<string> FinishReasons = ["stop", "length", "content_filter"];
    private static readonly HashSet<string> ReservedHeaders = ["content-length", "transfer-encoding", "connection", "content-type"];

    public static string? Validate(Reply reply, ContentStore content)
    {
        if (reply.Content is not null && reply.ContentFrom is not null) return "Use either 'content' or 'contentFrom', not both.";
        if (reply.ContentFrom is not null && content.Validate(reply.ContentFrom) is { } missing) return missing;
        if (reply.FinishReason is not null && !FinishReasons.Contains(reply.FinishReason))
            return $"finishReason must be one of: {string.Join(", ", FinishReasons)}.";
        if (reply.Status is { } status && !Statuses.Contains(status))
            return $"status must be one of: {string.Join(", ", Statuses.Order())}.";
        if (reply.RetryAfterSeconds is < 0) return "retryAfterSeconds must be >= 0.";
        if (reply.DelayMs is < 0 or > 600_000) return "delayMs must be between 0 and 600000.";
        if (reply.HangMs is < 0 or > 600_000) return "hangMs must be between 0 and 600000.";
        if (reply.Repeat is < 1) return "repeat must be >= 1.";
        if (reply.Usage is { Prompt: < 0 } or { Completion: < 0 }) return "usage values must be >= 0.";
        if (reply.ContentType is not null && !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(reply.ContentType, out _))
            return $"contentType '{reply.ContentType}' is not a valid media type.";
        if (reply.Headers is { } headers)
            foreach (var (name, value) in headers)
            {
                if (name.Length == 0 || name.Any(c => c <= ' ' || c == ':' || c > '~') || value.Any(c => c is '\r' or '\n'))
                    return $"header '{name}' is not a valid header.";
                if (ReservedHeaders.Contains(name.ToLowerInvariant())) return $"header '{name}' is set by the simulator (use contentType for Content-Type).";
            }
        return null;
    }

    public static string? Validate(Recipe recipe, ContentStore content, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(recipe.Name)) return "A recipe needs a 'name'.";
        if (recipe.Reply is null) return $"Recipe '{recipe.Name}' needs a 'reply'.";
        if (recipe.When?.MinMessages is < 0) return $"Recipe '{recipe.Name}': minMessages must be >= 0.";
        if (recipe.When?.Regex is { } pattern)
        {
            try { regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { return $"Recipe '{recipe.Name}': invalid regex: {ex.Message}"; }
        }
        return Validate(recipe.Reply, content) is { } error ? $"Recipe '{recipe.Name}': {error}" : null;
    }
}

// All mutable simulator state. Everything takes Lock: selection decrements counters, so it must be atomic.
public sealed class LlmState
{
    public const int MaxLogEntries = 2000;

    private readonly List<QueuedReply> _queue = [];
    private readonly List<RecipeEntry> _recipes = [];
    private readonly List<Fault> _faults = [];
    private readonly List<RequestLogEntry> _log = [];
    private readonly LlmStats _stats = new();
    private readonly HashSet<string> _keys;

    public LlmState(LlmSimOptions options)
    {
        Options = options;
        Clock = options.Clock ?? TimeProvider.System;
        _keys = [.. options.EffectiveApiKeys];
        Models = options.EffectiveModels.Distinct(StringComparer.Ordinal).ToList();
        var (recipes, baseDirectory) = LoadRecipes(options.Recipes);
        Content = new ContentStore(baseDirectory);
        foreach (var recipe in recipes)
        {
            if (ReplyRules.Validate(recipe, Content, out var regex) is { } error)
                throw new InvalidDataException($"Invalid recipe in '{options.Recipes ?? "default"}': {error}");
            _recipes.Add(new RecipeEntry(recipe, "file", regex));
        }
    }

    public LlmSimOptions Options { get; }
    public TimeProvider Clock { get; }
    public IReadOnlyList<string> Models { get; }
    public ContentStore Content { get; }
    public object Lock { get; } = new();
    public bool RequireKey => Options.RequireKey;

    public DateTimeOffset Now => Clock.GetUtcNow();

    private static (List<Recipe> Recipes, string? BaseDirectory) LoadRecipes(string? profile)
    {
        string text; string? baseDirectory = null;
        if (string.IsNullOrWhiteSpace(profile) || profile == "default") text = ContentStore.ReadEmbedded("seed/default-recipes.json");
        else
        {
            var full = Path.GetFullPath(profile);
            if (!File.Exists(full)) throw new FileNotFoundException($"Recipes file '{profile}' was not found (use 'default' or a JSON file path).");
            text = File.ReadAllText(full, Encoding.UTF8);
            baseDirectory = Path.GetDirectoryName(full);
        }
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var recipes = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<List<Recipe>>(LlmJson.Strict)
            : document.RootElement.Deserialize<RecipeFile>(LlmJson.Strict)?.Recipes;
        return (recipes ?? [], baseDirectory);
    }

    // ---- authentication ------------------------------------------------------------------

    // Accepts "Authorization: Bearer <key>" and Azure's "api-key: <key>"; returns the key worth reporting on.
    public (bool Valid, string? Presented) Authenticate(HttpContext ctx)
    {
        var candidates = new List<string>();
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header[7..].Trim() is { Length: > 0 } bearer) candidates.Add(bearer);
        if (ctx.Request.Headers["api-key"].ToString().Trim() is { Length: > 0 } apiKey) candidates.Add(apiKey);
        var valid = candidates.FirstOrDefault(_keys.Contains);
        return (valid is not null, valid ?? candidates.FirstOrDefault());
    }

    // ---- answer selection ----------------------------------------------------------------

    public (Reply? Reply, string Matched) Select(PromptView prompt)
    {
        lock (Lock)
        {
            if (_queue.Count > 0)
            {
                var next = _queue[0];
                if (--next.Remaining <= 0) _queue.RemoveAt(0);
                return (next.Reply, "queue");
            }
            foreach (var entry in _recipes)
            {
                if (entry.Recipe.Reply!.Repeat is { } cap && entry.Used >= cap) continue;
                if (!Matches(entry, prompt)) continue;
                entry.Used++;
                return (entry.Recipe.Reply, "recipe:" + entry.Recipe.Name);
            }
        }
        return (null, "default");
    }

    private static bool Matches(RecipeEntry entry, PromptView prompt)
    {
        var when = entry.Recipe.When;
        if (when is null) return true;
        if (when.Contains is { Count: > 0 } all && !all.All(s => prompt.AllText.Contains(s, StringComparison.Ordinal))) return false;
        if (when.ContainsAny is { Count: > 0 } any && !any.Any(s => prompt.AllText.Contains(s, StringComparison.Ordinal))) return false;
        if (when.System is { } system && !prompt.SystemText.Contains(system, StringComparison.Ordinal)) return false;
        if (when.LastUser is { } lastUser && !prompt.LastUserText.Contains(lastUser, StringComparison.Ordinal)) return false;
        if (when.Model is { } model && !model.Equals(prompt.Model, StringComparison.Ordinal)) return false;
        if (when.MinMessages is { } min && prompt.Messages.Count < min) return false;
        if (entry.Regex is { } regex)
        {
            try { if (!regex.IsMatch(prompt.AllText)) return false; }
            catch (RegexMatchTimeoutException) { return false; }
        }
        return true;
    }

    public int Enqueue(IEnumerable<Reply> replies)
    {
        lock (Lock)
        {
            var added = replies.Select(r => new QueuedReply(r)).ToList();
            _queue.AddRange(added);
            return added.Count;
        }
    }

    public void ClearQueue() { lock (Lock) _queue.Clear(); }
    public List<(Reply Reply, int Remaining)> QueueSnapshot() { lock (Lock) return _queue.Select(q => (q.Reply, q.Remaining)).ToList(); }

    public void AddRecipes(IEnumerable<(Recipe Recipe, Regex? Regex)> recipes)
    {
        lock (Lock) _recipes.AddRange(recipes.Select(r => new RecipeEntry(r.Recipe, "runtime", r.Regex)));
    }

    public void ClearRuntimeRecipes() { lock (Lock) _recipes.RemoveAll(r => r.Source == "runtime"); }
    public List<(Recipe Recipe, string Source, int Used)> RecipeSnapshot() { lock (Lock) return _recipes.Select(r => (r.Recipe, r.Source, r.Used)).ToList(); }

    // ---- faults ---------------------------------------------------------------------------

    public int AddFault(Fault fault) { lock (Lock) { _faults.Add(fault); return _faults.Count; } }
    public void ClearFaults() { lock (Lock) _faults.Clear(); }

    public Fault? TakeFault(string path)
    {
        lock (Lock)
        {
            foreach (var fault in _faults)
            {
                if (fault.Remaining is <= 0) continue;
                if (fault.PathRegex is { } pattern && !Regex.IsMatch(path, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))) continue;
                if (fault.Remaining is not null) fault.Remaining--;
                return fault;
            }
        }
        return null;
    }

    // ---- request log ----------------------------------------------------------------------

    public void Record(RequestLogEntry entry)
    {
        lock (Lock)
        {
            _log.Add(entry);
            if (_log.Count > MaxLogEntries) _log.RemoveRange(0, _log.Count - MaxLogEntries);
            _stats.Total++;
            if (entry.Streamed) _stats.Streamed++;
            _stats.PromptTokens += entry.PromptTokens;
            _stats.CompletionTokens += entry.CompletionTokens;
            Bump(_stats.ByStatus, entry.Status.ToString());
            Bump(_stats.ByModel, entry.Model ?? "(none)");
            Bump(_stats.ByMatched, entry.Matched);
        }
    }

    private static void Bump(SortedDictionary<string, long> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    public List<RequestLogEntry> LogSnapshot() { lock (Lock) return [.. _log]; }
    public void ClearLog() { lock (Lock) _log.Clear(); }

    public object StatsSnapshot()
    {
        lock (Lock)
            return new
            {
                total = _stats.Total, streamed = _stats.Streamed,
                byStatus = new SortedDictionary<string, long>(_stats.ByStatus, StringComparer.Ordinal),
                byModel = new SortedDictionary<string, long>(_stats.ByModel, StringComparer.Ordinal),
                byMatched = new SortedDictionary<string, long>(_stats.ByMatched, StringComparer.Ordinal),
                promptTokens = _stats.PromptTokens, completionTokens = _stats.CompletionTokens,
                totalTokens = _stats.PromptTokens + _stats.CompletionTokens
            };
    }

    public void Reset()
    {
        lock (Lock)
        {
            _queue.Clear();
            _faults.Clear();
            _recipes.RemoveAll(r => r.Source == "runtime");
            foreach (var entry in _recipes) entry.Used = 0;
            _log.Clear();
            _stats.Total = _stats.Streamed = _stats.PromptTokens = _stats.CompletionTokens = 0;
            _stats.ByStatus.Clear(); _stats.ByModel.Clear(); _stats.ByMatched.Clear();
        }
    }
}
