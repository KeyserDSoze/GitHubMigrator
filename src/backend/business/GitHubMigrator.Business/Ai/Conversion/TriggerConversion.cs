using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business;

// What a reviewer must weigh when Azure triggers become GitHub triggers: every assumption and everything that could not be carried over. Codes are
// stable (they are accepted by name at approval); messages are English text written by this product, never text from the pipeline.
public sealed record TriggerDifference(string Code, string Message);

public sealed record AzureFilter(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude)
{
    public static AzureFilter Empty { get; } = new([], []);
    public bool IsEmpty => Include.Count == 0 && Exclude.Count == 0;
}

// Azure CI trigger. Absent means "every branch" in Azure Pipelines, which is why Defaulted is kept apart from Disabled.
public sealed record AzureCiTrigger(bool Disabled, bool Defaulted, AzureFilter Branches, AzureFilter Tags, AzureFilter Paths, bool Batch);
public sealed record AzurePrTrigger(bool Disabled, AzureFilter Branches, AzureFilter Paths, bool AutoCancel, bool? Drafts);
public sealed record AzureSchedule(string Cron, AzureFilter Branches, bool Always);
public sealed record AzureTriggers(AzureCiTrigger Ci, AzurePrTrigger Pr, IReadOnlyList<AzureSchedule> Schedules, IReadOnlyList<TriggerDifference> Notes);

// A filter in GitHub's syntax: `!` entries are exclusions after the positive ones, or Ignore lists when there is nothing positive.
public sealed record GitHubFilter(IReadOnlyList<string> Patterns, IReadOnlyList<string> Ignore)
{
    public static GitHubFilter None { get; } = new([], []);
    public bool IsEmpty => Patterns.Count == 0 && Ignore.Count == 0;
}

public sealed record GitHubEventFilters(GitHubFilter Branches, GitHubFilter Tags, GitHubFilter Paths);

public sealed record GitHubTriggers(GitHubEventFilters? Push, GitHubEventFilters? PullRequest, IReadOnlyList<string> Schedules, IReadOnlyList<TriggerDifference> Differences)
{
    public bool IsEmpty => Push is null && PullRequest is null && Schedules.Count == 0;

    // One line per trigger, for a reviewer and for the pull request description. Only what the mapper wrote.
    public IReadOnlyList<string> Summary()
    {
        var lines = new List<string>();
        static string Of(string name, GitHubFilter f) => f.IsEmpty ? "" : $"{name}: {string.Join(", ", f.Patterns.Concat(f.Ignore.Select(i => "ignore " + i)))}";
        static string Describe(string title, GitHubEventFilters e) =>
            title + (string.Join("; ", new[] { Of("branches", e.Branches), Of("tags", e.Tags), Of("paths", e.Paths) }.Where(x => x.Length > 0)) is { Length: > 0 } filters ? " — " + filters : " — no filter (every branch and tag)");
        if (Push is not null) lines.Add(Describe("push", Push));
        if (PullRequest is not null) lines.Add(Describe("pull_request", PullRequest));
        foreach (var cron in Schedules) lines.Add("schedule: " + cron);
        return lines;
    }
}

// Reads the triggers of an Azure Pipelines YAML. It is a parser of untrusted text: it builds a node tree, evaluates nothing, and anything it does not
// understand becomes a difference and is left out, never guessed.
public static partial class AzureTriggerReader
{
    public const int MaxChars = 200_000;

    [GeneratedRegex(@"\$\{\{|\$\(|\$\[", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Expression();

    public static AzureTriggers? Read(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxChars) return null;
        YamlStream stream;
        try { stream = SafeYaml.Load(yaml); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or ArgumentException) { return null; }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) return null;

        var notes = new List<TriggerDifference>();
        var ci = ReadCi(Child(root, "trigger"), notes);
        var pr = ReadPr(Child(root, "pr"), notes);
        var schedules = ReadSchedules(Child(root, "schedules"), notes);
        if (Child(root, "resources") is YamlMappingNode resources &&
            new[] { "pipelines", "repositories", "containers" }.Any(kind => Child(resources, kind) is YamlSequenceNode items && items.Children.OfType<YamlMappingNode>().Any(i => Child(i, "trigger") is not null)))
            notes.Add(new("TRIGGER_RESOURCE_TRIGGERS_NOT_MAPPED", "A resource (pipeline, repository or container) triggers this pipeline in Azure. There is no automatic equivalent: it is not converted."));
        return new(ci, pr, schedules, Dedupe(notes));
    }

    private static AzureCiTrigger ReadCi(YamlNode? node, List<TriggerDifference> notes)
    {
        switch (node)
        {
            case null:
                return new(false, true, AzureFilter.Empty, AzureFilter.Empty, AzureFilter.Empty, false);
            case YamlScalarNode { Value: "none" }:
                return new(true, false, AzureFilter.Empty, AzureFilter.Empty, AzureFilter.Empty, false);
            case YamlSequenceNode list:
                return new(false, false, new(Patterns(list, notes), []), AzureFilter.Empty, AzureFilter.Empty, false);
            case YamlMappingNode map:
                return new(false, false, Filter(Child(map, "branches"), notes), Filter(Child(map, "tags"), notes), Filter(Child(map, "paths"), notes),
                    Child(map, "batch") is YamlScalarNode { Value: { } b } && b.Equals("true", StringComparison.OrdinalIgnoreCase));
            default:
                notes.Add(new("TRIGGER_SHAPE_UNSUPPORTED", "The CI trigger has a shape this product does not read: no CI trigger is converted."));
                return new(true, false, AzureFilter.Empty, AzureFilter.Empty, AzureFilter.Empty, false);
        }
    }

    private static AzurePrTrigger ReadPr(YamlNode? node, List<TriggerDifference> notes)
    {
        switch (node)
        {
            case null or YamlScalarNode { Value: "none" }:
                return new(true, AzureFilter.Empty, AzureFilter.Empty, false, null);
            case YamlSequenceNode list:
                return new(false, new(Patterns(list, notes), []), AzureFilter.Empty, false, null);
            case YamlMappingNode map:
                return new(false, Filter(Child(map, "branches"), notes), Filter(Child(map, "paths"), notes),
                    Child(map, "autoCancel") is YamlScalarNode { Value: { } a } && a.Equals("true", StringComparison.OrdinalIgnoreCase),
                    Child(map, "drafts") is YamlScalarNode { Value: { } d } ? d.Equals("true", StringComparison.OrdinalIgnoreCase) : null);
            default:
                notes.Add(new("TRIGGER_SHAPE_UNSUPPORTED", "The pull request trigger has a shape this product does not read: it is not converted."));
                return new(true, AzureFilter.Empty, AzureFilter.Empty, false, null);
        }
    }

    private static IReadOnlyList<AzureSchedule> ReadSchedules(YamlNode? node, List<TriggerDifference> notes)
    {
        if (node is null) return [];
        if (node is not YamlSequenceNode list)
        {
            notes.Add(new("TRIGGER_SHAPE_UNSUPPORTED", "The schedules have a shape this product does not read: they are not converted."));
            return [];
        }
        var found = new List<AzureSchedule>();
        foreach (var item in list.Children)
        {
            if (item is not YamlMappingNode map || Child(map, "cron") is not YamlScalarNode { Value: { } cron } || Expression().IsMatch(cron))
            {
                notes.Add(new("TRIGGER_SHAPE_UNSUPPORTED", "A schedule has no readable cron expression: it is not converted."));
                continue;
            }
            found.Add(new(cron.Trim(), Filter(Child(map, "branches"), notes), Child(map, "always") is YamlScalarNode { Value: { } always } && always.Equals("true", StringComparison.OrdinalIgnoreCase)));
        }
        return found;
    }

    // branches/tags/paths are either a list (include) or { include, exclude }
    private static AzureFilter Filter(YamlNode? node, List<TriggerDifference> notes) => node switch
    {
        null => AzureFilter.Empty,
        YamlSequenceNode list => new(Patterns(list, notes), []),
        YamlMappingNode map => new(Patterns(Child(map, "include"), notes), Patterns(Child(map, "exclude"), notes)),
        _ => Unsupported(notes)
    };

    private static AzureFilter Unsupported(List<TriggerDifference> notes)
    {
        notes.Add(new("TRIGGER_SHAPE_UNSUPPORTED", "A trigger filter has a shape this product does not read: it is left out."));
        return AzureFilter.Empty;
    }

    private static IReadOnlyList<string> Patterns(YamlNode? node, List<TriggerDifference> notes)
    {
        if (node is null) return [];
        if (node is not YamlSequenceNode list) { Unsupported(notes); return []; }
        var patterns = new List<string>();
        foreach (var item in list.Children)
        {
            if (item is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value)) { Unsupported(notes); continue; }
            if (Expression().IsMatch(value)) { notes.Add(new("TRIGGER_TEMPLATE_EXPRESSION", "A trigger pattern is computed from an expression or variable: it cannot be converted and is left out.")); continue; }
            patterns.Add(value.Trim());
        }
        return patterns;
    }

    private static YamlNode? Child(YamlMappingNode map, string key) => map.Children.FirstOrDefault(p => p.Key is YamlScalarNode s && s.Value == key).Value;
    private static IReadOnlyList<TriggerDifference> Dedupe(IEnumerable<TriggerDifference> notes) => notes.DistinctBy(n => n.Code).ToArray();
}

// Azure trigger -> GitHub trigger, deterministically. No model is involved. Every place where the two differ, or where an assumption was made, is a
// difference that a person accepts by name; nothing is silently dropped.
public static partial class GitHubTriggerMapper
{
    [GeneratedRegex(@"^[A-Za-z0-9._/*+@~\-]{1,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._/*+@~\- ]{1,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[0-9A-Za-z*/,\-]{1,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CronField();

    private sealed class Run
    {
        public List<TriggerDifference> Differences { get; } = [];
        public void Note(string code, string message) { if (Differences.All(d => d.Code != code)) Differences.Add(new(code, message)); }
    }

    public static GitHubTriggers Map(AzureTriggers azure)
    {
        var run = new Run();
        foreach (var note in azure.Notes) run.Note(note.Code, note.Message);

        GitHubEventFilters? push = null;
        if (!azure.Ci.Disabled)
        {
            if (azure.Ci.Defaulted)
                run.Note("TRIGGER_DEFAULT_CI_ALL_BRANCHES", "The pipeline has no trigger section, which in Azure Pipelines means it runs on every push to every branch. The same is mapped to GitHub: review whether that is wanted.");
            push = new(Refs(azure.Ci.Branches, "branch", run), Refs(azure.Ci.Tags, "tag", run), Paths(azure.Ci.Paths, run));
            if (azure.Ci.Batch) run.Note("TRIGGER_BATCH_NOT_MAPPED", "Azure batches CI runs (one at a time, the newest queued). GitHub starts a run per push: add a concurrency group if that matters.");
        }

        GitHubEventFilters? pull = null;
        if (!azure.Pr.Disabled)
        {
            pull = new(Refs(azure.Pr.Branches, "branch", run), GitHubFilter.None, Paths(azure.Pr.Paths, run));
            run.Note("TRIGGER_PR_AZURE_REPOS_IGNORED", "In Azure Repos Git the pr: section of the YAML is ignored (branch policies start pull request builds). Enabling pull_request here adds runs that did not come from the YAML: review it.");
            if (azure.Pr.AutoCancel) run.Note("TRIGGER_AUTOCANCEL_NOT_MAPPED", "Azure cancels superseded pull request runs (autoCancel). GitHub does not unless a concurrency group is added.");
            if (azure.Pr.Drafts == false) run.Note("TRIGGER_DRAFTS_NOT_MAPPED", "Azure skips draft pull requests (drafts: false). GitHub runs on them unless a job condition is added.");
        }

        var crons = new List<string>();
        foreach (var schedule in azure.Schedules)
        {
            if (!ValidCron(schedule.Cron, run)) { run.Note("TRIGGER_SCHEDULE_UNSUPPORTED", "A schedule's cron expression is not in a form this product converts: it is left out."); continue; }
            crons.Add(schedule.Cron);
            if (!schedule.Always) run.Note("TRIGGER_SCHEDULE_ALWAYS_FALSE", "Azure runs a schedule only if the source changed since the last scheduled run (always: false). GitHub runs it every time.");
            if (!schedule.Branches.IsEmpty) run.Note("TRIGGER_SCHEDULE_BRANCHES", "Azure schedules name branches; GitHub runs a schedule on the default branch only. Check that the default branch is the one meant.");
        }
        run.Note("TRIGGER_TIME_ZONE", "Cron expressions are read as UTC on both sides.");
        if (crons.Count == 0) run.Differences.RemoveAll(d => d.Code == "TRIGGER_TIME_ZONE");

        return new(push, pull, crons.Distinct().ToArray(), run.Differences);
    }

    private static GitHubFilter Refs(AzureFilter filter, string kind, Run run)
    {
        var include = filter.Include.Select(p => Ref(p, kind, run)).OfType<string>().Distinct().ToList();
        var exclude = filter.Exclude.Select(p => Ref(p, kind, run)).OfType<string>().Distinct().ToList();
        return Combine(include, exclude);
    }

    private static string? Ref(string azure, string kind, Run run)
    {
        var p = azure.Trim();
        foreach (var prefix in new[] { "refs/heads/", "refs/tags/" })
            if (p.StartsWith(prefix, StringComparison.Ordinal)) { p = p[prefix.Length..]; break; }
        if (p.Contains('?')) { run.Note("TRIGGER_PATTERN_UNSUPPORTED", "A pattern uses the single-character wildcard (?), which GitHub does not have: it is left out."); return null; }
        if (!RefPattern().IsMatch(p)) { run.Note("TRIGGER_PATTERN_UNSUPPORTED", "A pattern has characters this product does not carry over: it is left out."); return null; }
        return Wildcard(p, run);
    }

    private static GitHubFilter Paths(AzureFilter filter, Run run)
    {
        var include = filter.Include.SelectMany(p => Path(p, run)).Distinct().ToList();
        var exclude = filter.Exclude.SelectMany(p => Path(p, run)).Distinct().ToList();
        return Combine(include, exclude);
    }

    // Azure path filters are prefixes: "src" is the folder or the file. GitHub needs both spellings.
    private static IEnumerable<string> Path(string azure, Run run)
    {
        var p = azure.Trim().TrimStart('/');
        if (p.Length == 0 || p.Contains('?') || !PathPattern().IsMatch(p))
        {
            run.Note("TRIGGER_PATTERN_UNSUPPORTED", "A path pattern uses characters this product does not carry over: it is left out.");
            return [];
        }
        var converted = Wildcard(p, run);
        if (p.Contains('*')) return [converted];
        run.Note("TRIGGER_PATH_PREFIX", "Azure path filters match by prefix: a plain path is mapped to the path itself and to everything under it.");
        var folder = converted.TrimEnd('/');
        return folder.Length == 0 ? [] : [folder, folder + "/**"];
    }

    // In Azure a lone * crosses "/"; in GitHub it does not, ** does. Patterns that already use ** are valid GitHub as they are.
    private static string Wildcard(string pattern, Run run)
    {
        if (!pattern.Contains('*') || pattern.Contains("**")) return pattern;
        run.Note("TRIGGER_WILDCARD_SEMANTICS", "In Azure a * matches across '/'. It is mapped to GitHub's **, which does too: check that the patterns still select the intended branches and paths.");
        return pattern.Replace("*", "**");
    }

    // GitHub does not allow branches and branches-ignore together: with something to include, exclusions become "!" entries after it.
    private static GitHubFilter Combine(List<string> include, List<string> exclude) =>
        include.Count > 0 ? new([.. include, .. exclude.Select(e => "!" + e)], []) : new([], exclude);

    private static bool ValidCron(string cron, Run run)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || !fields.All(f => CronField().IsMatch(f))) return false;
        var minute = fields[0];
        if (minute == "*" || (minute.StartsWith("*/", StringComparison.Ordinal) && int.TryParse(minute[2..], out var every) && every < 5))
            run.Note("TRIGGER_CRON_INTERVAL", "GitHub does not run schedules more often than every five minutes; a shorter interval is run less often.");
        return true;
    }
}

public sealed record TriggerDerivation(bool Ok, string? Error, string? Yaml, GitHubTriggers? Triggers, IReadOnlyList<TriggerDifference> Differences);

// Puts the mapped triggers into an approved, manual-only workflow by rewriting its `on:` entry and nothing else: every other line is returned
// byte for byte, and the result is parsed again to prove the `on:` entry says what was meant.
public static class WorkflowTriggerEditor
{
    private static readonly string[] Automatic = ["push", "pull_request", "schedule"];

    public static TriggerDerivation Derive(string approvedYaml, AzureTriggers azure)
    {
        var mapped = GitHubTriggerMapper.Map(azure);
        var differences = mapped.Differences.ToList();
        if (mapped.IsEmpty) return new(false, "NoAutomaticTriggers", null, mapped, differences);

        var text = approvedYaml.Replace("\r\n", "\n");
        YamlStream stream;
        try { stream = SafeYaml.Load(text); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or ArgumentException) { return new(false, "WorkflowNotReadable", null, mapped, differences); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) return new(false, "WorkflowNotReadable", null, mapped, differences);

        var rootKeys = root.Children.Where(p => p.Key is YamlScalarNode).OrderBy(p => p.Key.Start.Line).ToList();
        var onIndex = rootKeys.FindIndex(p => ((YamlScalarNode)p.Key).Value == "on");
        if (onIndex < 0) return new(false, "OnSectionNotFound", null, mapped, differences);
        var lines = text.Split('\n').ToList();
        var start = (int)rootKeys[onIndex].Key.Start.Line - 1;
        var end = onIndex + 1 < rootKeys.Count ? (int)rootKeys[onIndex + 1].Key.Start.Line - 1 : lines.Count;
        // comments and blank lines right before the next top-level key belong to it, not to `on:`
        while (end > start + 1 && (lines[end - 1].Trim().Length == 0 || lines[end - 1].StartsWith('#'))) end--;

        var existing = rootKeys[onIndex].Value;
        var dispatch = new List<string>();
        switch (existing)
        {
            case YamlMappingNode { Style: MappingStyle.Block } block:
            {
                var keys = block.Children.Where(p => p.Key is YamlScalarNode).OrderBy(p => p.Key.Start.Line).ToList();
                if (keys.Any(p => Automatic.Contains(((YamlScalarNode)p.Key).Value, StringComparer.Ordinal) || ((YamlScalarNode)p.Key).Value is not "workflow_dispatch"))
                    return new(false, "WorkflowHasOtherTriggers", null, mapped, differences);
                for (var i = 0; i < keys.Count; i++)
                {
                    var from = (int)keys[i].Key.Start.Line - 1;
                    var to = i + 1 < keys.Count ? (int)keys[i + 1].Key.Start.Line - 1 : end;
                    dispatch.AddRange(lines.Skip(from).Take(to - from));
                }
                break;
            }
            case YamlScalarNode { Value: "workflow_dispatch" }:
            case YamlSequenceNode seq when seq.Children.OfType<YamlScalarNode>().Select(c => c.Value).SequenceEqual(["workflow_dispatch"]):
            case YamlMappingNode flow when flow.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).SequenceEqual(["workflow_dispatch"]) && flow.Children.Values.All(v => v is YamlScalarNode { Value: null or "" } || v is YamlMappingNode { Children.Count: 0 }):
                dispatch.Add("  workflow_dispatch:");
                break;
            default:
                return new(false, "TriggerShapeUnsupported", null, mapped, differences);
        }

        var generated = new List<string>();
        void Filters(string name, GitHubEventFilters f)
        {
            generated.Add($"  {name}:");
            Block("branches", "branches-ignore", f.Branches);
            Block("tags", "tags-ignore", f.Tags);
            Block("paths", "paths-ignore", f.Paths);
            void Block(string positive, string ignore, GitHubFilter filter)
            {
                if (filter.Patterns.Count > 0) { generated.Add($"    {positive}:"); generated.AddRange(filter.Patterns.Select(p => "      - " + Q(p))); }
                if (filter.Ignore.Count > 0) { generated.Add($"    {ignore}:"); generated.AddRange(filter.Ignore.Select(p => "      - " + Q(p))); }
            }
        }
        if (mapped.Push is { } push) Filters("push", push);
        if (mapped.PullRequest is { } pull) Filters("pull_request", pull);
        if (mapped.Schedules.Count > 0)
        {
            generated.Add("  schedule:");
            generated.AddRange(mapped.Schedules.Select(c => "    - cron: " + Q(c)));
        }

        var onBlock = new List<string> { "on:" };
        onBlock.AddRange(dispatch);
        onBlock.AddRange(generated);
        var result = string.Join('\n', lines.Take(start).Concat(onBlock).Concat(lines.Skip(end)));

        // prove it: parses, one document, the same top-level keys, and `on` holds workflow_dispatch plus exactly what was generated
        try
        {
            var check = SafeYaml.Load(result);
            if (check.Documents.Count != 1 || check.Documents[0].RootNode is not YamlMappingNode after ||
                !after.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).Order().SequenceEqual(root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).Order()) ||
                after.Children.First(p => p.Key is YamlScalarNode { Value: "on" }).Value is not YamlMappingNode onAfter)
                return new(false, "DerivedWorkflowInvalid", null, mapped, differences);
            var expected = new[] { "workflow_dispatch" }.Concat(new[] { mapped.Push is null ? null : "push", mapped.PullRequest is null ? null : "pull_request", mapped.Schedules.Count == 0 ? null : "schedule" }.OfType<string>());
            if (!onAfter.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).SequenceEqual(expected)) return new(false, "DerivedWorkflowInvalid", null, mapped, differences);
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or ArgumentException) { return new(false, "DerivedWorkflowInvalid", null, mapped, differences); }

        // what runs when the triggers fire: a workflow that logs in to Azure or uses secrets will do so on every matching event
        if (Regex.IsMatch(text, @"azure/login|id-token:\s*write|\$\{\{\s*secrets\.", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
            differences.Add(new("TRIGGER_STARTS_PRIVILEGED_JOBS", "The workflow signs in to Azure or uses secrets. With these triggers it will do so on every matching event, with no one starting it: check the branches, the environments and who may push."));
        return new(true, null, result, mapped, differences);
    }

    // single-quoted YAML scalar; the patterns were already restricted to a safe character set
    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";
}
