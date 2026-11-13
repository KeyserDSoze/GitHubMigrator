using System.Text;

namespace GitHubMigrator.Business;

public sealed record RedactionFinding(string Kind, int Line, string Placeholder);

// Text that may leave for a model. Blocked means it must NOT be sent at all (e.g. a private key block), even redacted.
public sealed record RedactionResult(string Text, IReadOnlyList<RedactionFinding> Findings, bool Blocked, string? BlockedReason,
    bool Truncated)
{
    public bool SafeToSend => !Blocked;
}

// Pre-send gate for source pipeline text. Credentials become stable placeholders (<REDACTED:KIND:n>), control
// characters are removed and the size is bounded. The redactor never claims text is "clean": it reports what it
// replaced so an operator can audit it, and it refuses what it cannot safely mask.
public static class SourceRedactor
{
    public const int DefaultMaxBytes = 120_000;

    public static RedactionResult Redact(string? text, int maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrEmpty(text)) return new("", [], false, null, false);
        var cleaned = StripControlCharacters(text);
        var matches = Propagate(cleaned, SecretPatterns.Scan(cleaned));
        var builder = new StringBuilder(cleaned.Length);
        var findings = new List<RedactionFinding>();
        var cursor = 0;
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        var numbered = new Dictionary<(string Kind, string Value), int>();
        var blocked = false;
        foreach (var match in matches)
        {
            builder.Append(cleaned, cursor, match.Start - cursor);
            // the same value always gets the same placeholder, wherever it appears
            var value = cleaned.Substring(match.Start, match.Length);
            if (!numbered.TryGetValue((match.Kind, value), out var number))
            {
                number = counters[match.Kind] = counters.GetValueOrDefault(match.Kind) + 1;
                numbered[(match.Kind, value)] = number;
            }
            var placeholder = $"<REDACTED:{match.Kind}:{number}>";
            builder.Append(placeholder);
            findings.Add(new(match.Kind, match.Line, placeholder));
            blocked |= match.BlocksSending;
            cursor = match.Start + match.Length;
        }
        builder.Append(cleaned, cursor, cleaned.Length - cursor);

        var result = builder.ToString();
        var truncated = false;
        if (Encoding.UTF8.GetByteCount(result) > maxBytes)
        {
            result = TruncateUtf8(result, maxBytes);
            truncated = true;
        }
        return new(result, findings, blocked, blocked ? "A private key block was found in the source; nothing is sent." : null, truncated);
    }

    // A credential found under a key (password: abc...) usually appears again without it (echo abc..., a URL, a log line).
    // Every other occurrence of a value the scanner identified is masked too, or the key-less copy would reach the model.
    private static IReadOnlyList<SecretMatch> Propagate(string text, IReadOnlyList<SecretMatch> found)
    {
        var all = found.ToList();
        foreach (var group in found.Where(m => !m.BlocksSending && m.Length >= 8).GroupBy(m => (m.Kind, Value: text.Substring(m.Start, m.Length))))
        {
            for (var at = text.IndexOf(group.Key.Value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(group.Key.Value, at + group.Key.Value.Length, StringComparison.Ordinal))
            {
                if (all.Any(m => at < m.Start + m.Length && m.Start < at + group.Key.Value.Length)) continue;
                all.Add(new(group.Key.Kind, LineOf(text, at), at, group.Key.Value.Length, false));
            }
        }
        return all.OrderBy(m => m.Start).ToArray();
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static string StripControlCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (!char.IsControl(ch) || ch is '\n' or '\t') builder.Append(ch);
        return builder.ToString();
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
