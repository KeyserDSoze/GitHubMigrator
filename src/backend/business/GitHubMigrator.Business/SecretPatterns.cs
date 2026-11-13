using System.Text.RegularExpressions;

namespace GitHubMigrator.Business;

// One scanner for two jobs: redact source YAML before it can leave for a model, and refuse generated
// workflows that carry a literal credential. Patterns are deliberately conservative: a false positive costs a
// placeholder, a false negative costs a leaked credential.
public sealed record SecretPattern(string Kind, Regex Regex, int ValueGroup, bool BlocksSending = false);

public sealed record SecretMatch(string Kind, int Line, int Start, int Length, bool BlocksSending);

public static class SecretPatterns
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Words that make the VALUE of a key=value pair sensitive.
    private const string SensitiveKey =
        @"(?:pass(?:word|wd)?|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret|private[_-]?key|credentials?|auth(?:orization)?|sas)";

    public static readonly IReadOnlyList<SecretPattern> All =
    [
        new("PRIVATE_KEY_BLOCK", new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----|\z)", Options, Timeout), 0, BlocksSending: true),
        new("AWS_ACCESS_KEY_ID", new(@"\b(?:AKIA|ASIA|AIDA|AROA)[0-9A-Z]{16}\b", Options, Timeout), 0),
        new("AWS_SECRET_ACCESS_KEY", new(@"(?i)aws[_-]?secret[_-]?access[_-]?key['""]?\s*[:=]\s*['""]?([A-Za-z0-9/+=]{40})\b", Options, Timeout), 1),
        new("GITHUB_TOKEN", new(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,255}|github_pat_[A-Za-z0-9_]{22,255})\b", Options, Timeout), 0),
        new("SLACK_TOKEN", new(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", Options, Timeout), 0),
        new("GOOGLE_API_KEY", new(@"\bAIza[0-9A-Za-z_-]{35}\b", Options, Timeout), 0),
        new("STRIPE_KEY", new(@"\b[sr]k_(?:live|test)_[0-9A-Za-z]{16,}\b", Options, Timeout), 0),
        new("NPM_TOKEN", new(@"\bnpm_[A-Za-z0-9]{36}\b", Options, Timeout), 0),
        new("NUGET_API_KEY", new(@"\boy2[a-z0-9]{43}\b", Options, Timeout), 0),
        // Authorization: Basic <base64> / Bearer <opaque token>: the scheme word splits the value, so the generic rule misses it.
        // References ($(Token), ${{ secrets.X }}) start with '$' and are not matched.
        new("AUTHORIZATION_HEADER", new(@"(?i)\bauthorization['""]?\s*[:=]\s*['""]?(?:basic|bearer|token)\s+([A-Za-z0-9._~+/=-]{8,})", Options, Timeout), 1),
        new("JWT", new(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", Options, Timeout), 0),
        new("AZURE_CONNECTION_SECRET", new(@"(?i)\b(?:AccountKey|SharedAccessKey|SharedAccessSignature|Password|Pwd)=([^;\s'""<>]{6,})", Options, Timeout), 1),
        new("URL_CREDENTIALS", new(@"\b[a-z][a-z0-9+.-]*://[^/\s:@'""]+:([^@\s/'""]{3,})@", Options, Timeout), 1),
        // key: value / key=value where the key names a secret and the value is a literal, not a reference.
        new("SENSITIVE_LITERAL", new(
            @"(?i)\b(?<key>[\w.-]*" + SensitiveKey + @"[\w.-]*)['""]?\s*(?<sep>[:=])\s*(?!\$\(|\$\{\{|\$env:|\$\{|%|<|\*{3,}|['""]?\s*$|['""]?(?:true|false|null|none|\.\.\.)['""]?\s*$)['""]?([^\s'""#]{8,})['""]?", Options, Timeout), 1),
    ];

    // Finds every sensitive literal in the text (1-based line numbers).
    public static IReadOnlyList<SecretMatch> Scan(string text)
    {
        var found = new List<SecretMatch>();
        if (string.IsNullOrEmpty(text)) return found;
        var lineStarts = LineStarts(text);
        foreach (var pattern in All)
        {
            MatchCollection matches;
            try { matches = pattern.Regex.Matches(text); }
            catch (RegexMatchTimeoutException) { continue; }
            foreach (Match match in matches)
            {
                var group = match.Groups[pattern.ValueGroup];
                if (!group.Success || group.Length == 0) continue;
                if (pattern.Kind == "AZURE_CONNECTION_SECRET" && IsReference(group.Value, shellVariableToo: true)) continue;
                if (pattern.Kind == "URL_CREDENTIALS" && IsReference(group.Value, shellVariableToo: false)) continue;
                if (pattern.Kind == "SENSITIVE_LITERAL" && (IsPlaceholder(group.Value) || IsReferenceKey(match.Groups["key"].Value) ||
                    (match.Groups["sep"].Value == "=" && ShellVariable.IsMatch(group.Value)))) continue;
                found.Add(new(pattern.Kind, LineOf(lineStarts, group.Index), group.Index, group.Length, pattern.BlocksSending));
            }
        }
        // overlapping hits: keep the earliest, longest one so a value is redacted exactly once
        var ordered = found.OrderBy(m => m.Start).ThenByDescending(m => m.Length).ToList();
        var result = new List<SecretMatch>();
        var end = -1;
        foreach (var match in ordered)
        {
            if (match.Start < end) continue;
            result.Add(match);
            end = match.Start + match.Length;
        }
        return result;
    }

    // Keys whose value NAMES something instead of being a credential: Azure Pipelines inputs that hold a service connection
    // name (publishFeedCredentials: my-feed) and selectors such as authenticationType: servicePrincipal. Masking them
    // would hide exactly what a conversion needs to map to secrets, and they carry no secret.
    private static readonly HashSet<string> ConnectionReferenceKeys = new(StringComparer.OrdinalIgnoreCase)
        { "publishFeedCredentials", "externalFeedCredentials", "feedCredentials", "credentialsConfiguration" };
    private static readonly string[] SelectorSuffixes = ["Type", "Scheme", "Method", "Mode", "Selector"];

    // After '=' (a script argument or assignment) the shell expands $NAME / ${NAME}, so it is a reference; after ':' (a YAML
    // value) nothing expands it and it stays a literal.
    private static readonly Regex ShellVariable = new(@"^\$\{?[A-Za-z_][A-Za-z0-9_]*\}?$", Options, Timeout);

    // $(Var), ${{ expr }}, ${NAME}, $env:NAME, %NAME%: a name that something substitutes, never a credential itself.
    private static readonly Regex PipelineReference = new(@"^(?:\$\([^)\s]*\)|\$\{\{.*\}\}|\$\{[A-Za-z_][A-Za-z0-9_]*\}|\$env:[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%)$", Options, Timeout);

    private static bool IsReference(string value, bool shellVariableToo) =>
        PipelineReference.IsMatch(value) || (shellVariableToo && ShellVariable.IsMatch(value));

    private static bool IsReferenceKey(string key) =>
        ConnectionReferenceKeys.Contains(key) || SelectorSuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    // Values that are only a reference or a documented placeholder never are credentials.
    private static bool IsPlaceholder(string value) =>
        value.StartsWith("$(", StringComparison.Ordinal) || value.StartsWith("${{", StringComparison.Ordinal) ||
        value.StartsWith("<REDACTED", StringComparison.Ordinal) || value.All(c => c == '*' || c == 'x' || c == 'X') ||
        value.Equals("changeme", StringComparison.OrdinalIgnoreCase);

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static int LineOf(List<int> starts, int index)
    {
        var lo = 0; var hi = starts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= index) lo = mid; else hi = mid - 1;
        }
        return lo + 1;
    }
}
