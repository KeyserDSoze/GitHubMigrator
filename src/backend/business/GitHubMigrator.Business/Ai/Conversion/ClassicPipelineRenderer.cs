using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business;

// What the renderer needs besides the definition: the names of the organization's service connections by id (a classic step's input holds the connection's id, a YAML pipeline writes its name) and the names of
// tasks the built-in table does not know, as the organization's own catalogue answered (best effort, may be empty).
public sealed record ClassicRenderContext(IReadOnlyDictionary<string, string> EndpointNames, IReadOnlyDictionary<string, string> OrganizationTaskNames)
{
    public static ClassicRenderContext Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());
}

// Text is the rendering (empty when TooLarge); Notes say what it could not carry over or had to leave out, in words, for the model and for the person reviewing.
public sealed record ClassicRendering(string Text, IReadOnlyList<string> Notes, bool TooLarge);

// A classic (designer) build definition is JSON: phases of steps, each step a task id with a version spec and its inputs. This turns one into Azure-Pipelines-like YAML text, deterministically and without a model,
// so the rest of the conversion (redaction, task accounting, the gates, approval) works on the same kind of text it always has. It is a RENDERING, not the pipeline's file: nothing in it was ever in a repository, and it
// says so. What it does not carry over is named in the notes instead of being dropped: the triggers (Azure's defaults for a classic filter are not verified, so they are described, never turned into `on:`), build options,
// task groups (not expanded) and tasks it cannot name. Values of inputs whose name looks like a credential are not shown, and a step input that is exactly a service connection's id is shown as its name.
// Bounded: more than the limits below is refused (TooLarge) rather than cut.
public static partial class ClassicPipelineRenderer
{
    public const int MaxPhases = 20, MaxStepsPerPhase = 300, MaxInputsPerStep = 80, MaxVariables = 500, MaxValueChars = 50_000, MaxChars = 200_000, MaxNotes = 40, MaxLookups = 20;
    private const int MaxNoteChars = 300;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PlainName();

    [GeneratedRegex("password|secret|token|apikey|privatekey|principalkey|kubeconfig", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialName();

    [GeneratedRegex(@"^\$\(\s*[A-Za-z0-9_.-]+\s*\)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex VariableReference();

    // ---- what is a designer definition, and which of its tasks are unknown ----

    public static bool IsDesigner(JsonElement definition) =>
        definition.ValueKind == JsonValueKind.Object &&
        !string.Equals(Str(definition, "type"), "xaml", StringComparison.OrdinalIgnoreCase) &&
        Prop(definition, "process") is { ValueKind: JsonValueKind.Object } process &&
        (Int(process, "type") is null or 1) &&
        Prop(process, "phases") is { ValueKind: JsonValueKind.Array } phases && phases.GetArrayLength() > 0;

    // The ids of tasks the built-in table does not know (not task groups), at most MaxLookups, so the caller can ask the organization's catalogue about them.
    public static IReadOnlyList<string> TaskIdsToLookUp(JsonElement definition)
    {
        var ids = new List<string>();
        if (!IsDesigner(definition)) return ids;
        foreach (var phase in Items(Prop(definition, "process")!.Value, "phases").Take(MaxPhases))
            foreach (var step in Items(phase, "steps").Take(MaxStepsPerPhase))
                if (Prop(step, "task") is { ValueKind: JsonValueKind.Object } task && !IsTaskGroup(task) &&
                    ClassicTaskCatalog.NormalizeId(Str(task, "id")) is { } id && !ClassicTaskCatalog.IsKnown(id) && !ids.Contains(id) && ids.Count < MaxLookups)
                    ids.Add(id);
        return ids;
    }

    public static ClassicRendering? Render(JsonElement definition, ClassicRenderContext context) => IsDesigner(definition) ? new Run(context).Execute(definition) : null;

    // ---- json helpers ----

    private static JsonElement? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? Str(JsonElement element, string name) => Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) => Prop(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i,
        { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null
    };

    private static bool? Bool(JsonElement element, string name) => Prop(element, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } s when bool.TryParse(s.GetString(), out var b) => b,
        _ => null
    };

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) => Prop(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    private static bool IsTaskGroup(JsonElement task) => string.Equals(Str(task, "definitionType"), "metaTask", StringComparison.OrdinalIgnoreCase);

    // ---- the rendering ----

    private sealed class Run(ClassicRenderContext context)
    {
        private readonly List<string> _notes = [];
        private bool _exceeded;
        private int _withheld, _blank, _badNames, _endpoints, _inexact, _skippedSteps, _disabled, _secretVariables;
        private readonly HashSet<string> _taskGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unnamed = new(StringComparer.OrdinalIgnoreCase);

        public ClassicRendering Execute(JsonElement definition)
        {
            var process = Prop(definition, "process")!.Value;
            var phases = Items(process, "phases").ToArray();
            if (phases.Length > MaxPhases) _exceeded = true;

            var root = new YamlMappingNode();
            if (Str(definition, "buildNumberFormat") is { Length: > 0 } format) Add(root, "name", Free(format));

            var jobs = phases.Take(MaxPhases).Select((phase, index) => Phase(definition, process, phase, index, phases.Length)).ToArray();
            var flat = jobs.Length == 1 && jobs[0].Flat;
            if (flat) foreach (var field in jobs[0].Fields) root.Children.Add(field.Key, field.Value);
            if (Variables(definition) is { } variables) Add(root, "variables", variables);
            if (flat) Add(root, "steps", jobs[0].Steps);
            else
            {
                var sequence = new YamlSequenceNode();
                foreach (var job in jobs) { var map = new YamlMappingNode(); foreach (var field in job.Fields) map.Children.Add(field.Key, field.Value); Add(map, "steps", job.Steps); sequence.Add(map); }
                Add(root, "jobs", sequence);
            }

            Describe(definition);
            if (_exceeded) return new("", Notes(), true);

            var yaml = Save(root);
            var header = new StringBuilder();
            header.AppendLine("# Rendered by GitHubMigrator from a classic (designer) build definition. It is not a file in a repository and it is not meant to be run as it is.");
            header.AppendLine("# What the rendering could not carry over or could not show, then the definition:");
            foreach (var note in Notes()) header.AppendLine("# - " + note);
            var text = header + yaml;
            return text.Length > MaxChars ? new("", Notes(), true) : new(text, Notes(), false);
        }

        // ---- phases ----

        private sealed record Job(bool Flat, List<KeyValuePair<YamlNode, YamlNode>> Fields, YamlSequenceNode Steps);

        private Job Phase(JsonElement definition, JsonElement process, JsonElement phase, int index, int phaseCount)
        {
            var fields = new List<KeyValuePair<YamlNode, YamlNode>>();
            void Field(string key, YamlNode value) => fields.Add(new(Key(key), value));

            var phaseName = Str(phase, "name") ?? $"Phase {index + 1}";
            var target = Prop(phase, "target");
            var targetType = target is { } t ? Int(t, "type") : null;
            var server = targetType == 2;
            if (targetType is not (null or 1 or 2)) Note($"Phase '{phaseName}' has the target type {targetType} (not an agent job): its steps are shown as for an agent job.");
            if (target is { } options && Prop(options, "executionOptions") is { } execution && Int(execution, "type") is > 0 and var executionType)
                Note($"Phase '{phaseName}' uses a {(executionType == 1 ? "multi-configuration" : executionType == 2 ? "multi-agent" : "special")} execution option (not carried over).");

            var condition = Str(phase, "condition");
            var dependencies = Items(phase, "dependencies").Select(d => Str(d, "scope")).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => JobId(s!, -1)).ToArray();
            var flat = phaseCount == 1 && dependencies.Length == 0 && (string.IsNullOrWhiteSpace(condition) || condition == "succeeded()");

            if (!flat)
            {
                Field("job", Plain(JobId(Str(phase, "refName") ?? phaseName, index)));
                Field("displayName", Free(phaseName));
                if (dependencies.Length > 0) { var depends = new YamlSequenceNode(); foreach (var d in dependencies) depends.Add(Plain(d)); Field("dependsOn", depends); }
                if (!string.IsNullOrWhiteSpace(condition) && condition != "succeeded()") Field("condition", Free(condition));
            }
            if (server) Field("pool", Plain("server"));
            else if (Pool(definition, process, phase, target) is { } pool) Field("pool", pool);
            if ((Int(phase, "jobTimeoutInMinutes") ?? Int(definition, "jobTimeoutInMinutes")) is > 0 and var timeout) Field("timeoutInMinutes", Plain(timeout.ToString(CultureInfo.InvariantCulture)));
            if ((Int(phase, "jobCancelTimeoutInMinutes") ?? Int(definition, "jobCancelTimeoutInMinutes")) is > 0 and var cancel) Field("cancelTimeoutInMinutes", Plain(cancel.ToString(CultureInfo.InvariantCulture)));

            var steps = new YamlSequenceNode();
            if (index == 0 && !server && Checkout(definition) is { } checkout) steps.Add(checkout);
            var count = 0;
            foreach (var step in Items(phase, "steps"))
            {
                if (++count > MaxStepsPerPhase) { _exceeded = true; break; }
                if (Step(step) is { } rendered) steps.Add(rendered);
            }
            return new(flat, fields, steps);
        }

        private YamlNode? Pool(JsonElement definition, JsonElement process, JsonElement phase, JsonElement? target)
        {
            var queue = (target is { } t ? Prop(t, "queue") : null) ?? Prop(phase, "queue") ?? Prop(definition, "queue");
            var queueName = queue is { } q ? Str(q, "name") ?? (Prop(q, "pool") is { } p ? Str(p, "name") : null) : null;
            var hosted = queue is { } q2 && Prop(q2, "pool") is { } pool ? Bool(pool, "isHosted") : null;
            var image = (target is { } t2 && Prop(t2, "agentSpecification") is { } spec ? Str(spec, "identifier") : null)
                        ?? (Prop(process, "target") is { } processTarget && Prop(processTarget, "agentSpecification") is { } processSpec ? Str(processSpec, "identifier") : null);

            var map = new YamlMappingNode();
            // a hosted image only means something for a hosted pool; a self-hosted pool ignores it
            if (!string.IsNullOrWhiteSpace(image) && hosted != false) Add(map, "vmImage", Free(image));
            else if (!string.IsNullOrWhiteSpace(queueName)) Add(map, "name", Free(queueName));
            else return null;

            var demands = new YamlSequenceNode();
            if (target is { } t3 && Prop(t3, "demands") is { ValueKind: JsonValueKind.Array } own)
                foreach (var d in own.EnumerateArray().Take(20)) { if (d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } text) demands.Add(Free(text)); }
            else
                foreach (var d in Items(definition, "demands").Take(20))
                    if (Str(d, "name") is { Length: > 0 } name) demands.Add(Free(Str(d, "value") is { Length: > 0 } value ? $"{name} -equals {value}" : name));
            if (demands.Children.Count > 0) Add(map, "demands", demands);
            return map;
        }

        // "Get sources" in a classic pipeline: carried as a checkout step only when it has an option worth carrying (clean, submodules, shallow depth, LFS) or is switched off.
        private YamlNode? Checkout(JsonElement definition)
        {
            if (Prop(definition, "repository") is not { } repository) return null;
            var type = Str(repository, "type");
            if (!string.Equals(type, "TfsGit", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "Git", StringComparison.OrdinalIgnoreCase)) return null;
            var properties = Prop(repository, "properties");
            string? Property(string name) => properties is { } p ? Str(p, name) : null;

            if (string.Equals(Property("skipSyncSource"), "true", StringComparison.OrdinalIgnoreCase))
            {
                var none = new YamlMappingNode();
                Add(none, "checkout", Plain("none"));
                return none;
            }
            var map = new YamlMappingNode();
            Add(map, "checkout", Plain("self"));
            var any = false;
            if (string.Equals(Str(repository, "clean"), "true", StringComparison.OrdinalIgnoreCase)) { Add(map, "clean", Plain("true")); any = true; }
            if (Bool(repository, "checkoutSubmodules") == true) { Add(map, "submodules", Plain("true")); any = true; }
            if (int.TryParse(Property("fetchDepth"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth) && depth > 0) { Add(map, "fetchDepth", Plain(depth.ToString(CultureInfo.InvariantCulture))); any = true; }
            if (string.Equals(Property("gitLfsSupport"), "true", StringComparison.OrdinalIgnoreCase)) { Add(map, "lfs", Plain("true")); any = true; }
            return any ? map : null;
        }

        // ---- steps ----

        private YamlNode? Step(JsonElement step)
        {
            if (Prop(step, "task") is not { ValueKind: JsonValueKind.Object } task) { _skippedSteps++; return null; }
            var id = Str(task, "id");
            var spec = Str(task, "versionSpec");
            var major = ClassicTaskCatalog.MajorOf(spec);
            var normalized = ClassicTaskCatalog.NormalizeId(id);

            string reference;
            if (IsTaskGroup(task)) { _taskGroups.Add(normalized ?? "unknown"); reference = $"{normalized ?? "UnknownTask"}@{major ?? 0}"; }
            else if (ClassicTaskCatalog.Resolve(id, spec) is { } known) { if (!known.Exact) _inexact++; reference = $"{known.Name}@{known.Major}"; }
            else if (normalized is not null && context.OrganizationTaskNames.TryGetValue(normalized, out var named) && PlainName().IsMatch(named)) reference = $"{named}@{major ?? 0}";
            else { _unnamed.Add(normalized ?? "unknown"); reference = $"{normalized ?? "UnknownTask"}@{major ?? 0}"; }

            var map = new YamlMappingNode();
            Add(map, "task", Plain(reference));
            if (Str(step, "displayName") is { Length: > 0 } display) Add(map, "displayName", Free(display));
            if (Str(step, "refName") is { } refName && PlainName().IsMatch(refName)) Add(map, "name", Plain(refName));
            if (Bool(step, "enabled") == false) { Add(map, "enabled", Plain("false")); _disabled++; }
            if (Str(step, "condition") is { Length: > 0 } condition && condition != "succeeded()") Add(map, "condition", Free(condition));
            if (Bool(step, "continueOnError") == true) Add(map, "continueOnError", Plain("true"));
            if (Bool(step, "alwaysRun") == true) Add(map, "alwaysRun", Plain("true"));
            if (Int(step, "timeoutInMinutes") is > 0 and var timeout) Add(map, "timeoutInMinutes", Plain(timeout.ToString(CultureInfo.InvariantCulture)));
            if (Int(step, "retryCountOnTaskFailure") is > 0 and var retries) Add(map, "retryCountOnTaskFailure", Plain(retries.ToString(CultureInfo.InvariantCulture)));
            if (Prop(step, "environment") is { ValueKind: JsonValueKind.Object } environment && Entries(environment) is { Children.Count: > 0 } env) Add(map, "env", env);
            if (Prop(step, "inputs") is { ValueKind: JsonValueKind.Object } inputs && Entries(inputs) is { Children.Count: > 0 } rendered) Add(map, "inputs", rendered);
            return map;
        }

        private YamlMappingNode Entries(JsonElement values)
        {
            var map = new YamlMappingNode();
            var count = 0;
            foreach (var property in values.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (++count > MaxInputsPerStep) { _exceeded = true; break; }
                if (!PlainName().IsMatch(property.Name)) { _badNames++; continue; }
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText()
                };
                if (string.IsNullOrWhiteSpace(value)) { _blank++; continue; }
                if (CredentialName().IsMatch(property.Name) && !VariableReference().IsMatch(value.Trim()))
                {
                    _withheld++;
                    Add(map, property.Name, Free("<withheld: the name of this input looks like a credential>"));
                    continue;
                }
                if (ClassicTaskCatalog.NormalizeId(value.Trim()) is { } guid && context.EndpointNames.TryGetValue(guid, out var endpointName)) { _endpoints++; value = endpointName; }
                Add(map, property.Name, Free(value));
            }
            return map;
        }

        // ---- variables ----

        private YamlNode? Variables(JsonElement definition)
        {
            var sequence = new YamlSequenceNode();
            if (Prop(definition, "variables") is { ValueKind: JsonValueKind.Object } variables)
            {
                var count = 0;
                foreach (var variable in variables.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (++count > MaxVariables) { _exceeded = true; break; }
                    if (!PlainName().IsMatch(variable.Name)) { _badNames++; continue; }
                    var map = new YamlMappingNode();
                    Add(map, "name", Plain(variable.Name));
                    if (Bool(variable.Value, "isSecret") == true) _secretVariables++;
                    else if (Str(variable.Value, "value") is { Length: > 0 } value) Add(map, "value", Free(value));
                    sequence.Add(map);
                }
            }
            foreach (var group in Items(definition, "variableGroups").Take(50))
                if (Str(group, "name") is { Length: > 0 } name) { var map = new YamlMappingNode(); Add(map, "group", Free(name)); sequence.Add(map); }
            return sequence.Children.Count > 0 ? sequence : null;
        }

        // ---- what could not be carried over ----

        private void Describe(JsonElement definition)
        {
            var triggers = Items(definition, "triggers").ToArray();
            if (triggers.Length == 0) Note("No trigger: builds are started by hand. (Triggers are never turned into workflow triggers from a classic definition.)");
            foreach (var trigger in triggers.Take(10))
            {
                var kind = Str(trigger, "triggerType") ?? "unknown";
                switch (kind)
                {
                    case "continuousIntegration":
                        Note("Continuous integration trigger, not carried over: " + Filters(trigger) + (Bool(trigger, "batchChanges") == true ? "; changes are batched" : ""));
                        break;
                    case "pullRequest":
                        Note("Pull request trigger, not carried over: " + Filters(trigger));
                        break;
                    case "schedule":
                        foreach (var schedule in Items(trigger, "schedules").Take(5))
                            Note($"Schedule, not carried over: {Days(Int(schedule, "daysToBuild"))} at {(Int(schedule, "startHours") ?? 0):00}:{(Int(schedule, "startMinutes") ?? 0):00} ({Str(schedule, "timeZoneId") ?? "no time zone given"})" +
                                 (Bool(schedule, "scheduleOnlyWithChanges") == true ? ", only when there are changes" : "") + "; " + Filters(schedule));
                        break;
                    case "buildCompletion":
                        Note("A build completion trigger exists (not carried over).");
                        break;
                    default:
                        Note($"A trigger of type '{kind}' exists (not carried over).");
                        break;
                }
            }
            if (triggers.Length > 10) Note($"{triggers.Length - 10} more trigger(s) not listed.");
            if (Items(definition, "options").Count(o => Bool(o, "enabled") == true) is > 0 and var options) Note($"{options} build option(s) are switched on (for example work item creation on failure, badges): not carried over.");
            if (Prop(definition, "repository") is { } repository && Str(repository, "type") is { } type && !string.Equals(type, "TfsGit", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "Git", StringComparison.OrdinalIgnoreCase))
                Note($"The source repository is of type '{type}', not an Azure Repos Git repository: how the sources are fetched is not shown.");
            if (_secretVariables > 0) Note($"{_secretVariables} variable(s) are secret: they are listed by name, with no value.");
            if (_withheld > 0) Note($"{_withheld} step input(s) are not shown because their name looks like a credential.");
            if (_blank > 0) Note($"{_blank} step input(s) have no value (empty, or not returned by the service).");
            if (_endpoints > 0) Note($"{_endpoints} step input(s) held a service connection's id and are shown as its name.");
            if (_badNames > 0) Note($"{_badNames} input or variable name(s) were not plain names and are not shown.");
            if (_skippedSteps > 0) Note($"{_skippedSteps} step(s) without a task reference are not shown.");
            if (_disabled > 0) Note($"{_disabled} step(s) are disabled in the definition (shown with enabled: false).");
            if (_inexact > 0) Note($"{_inexact} task(s) are named from their id with a version the built-in table does not list: the name is the task's latest known one, the version is the one the definition asked for.");
            if (_taskGroups.Count > 0) Note($"{_taskGroups.Count} task group(s) are not expanded (shown by id): {string.Join(", ", _taskGroups.Order(StringComparer.Ordinal).Take(10))}.");
            if (_unnamed.Count > 0) Note($"{_unnamed.Count} task(s) could not be named (not a built-in task and not in the organization's catalogue; an extension or a removed task?), shown by id: {string.Join(", ", _unnamed.Order(StringComparer.Ordinal).Take(10))}.");
        }

        private static string Filters(JsonElement trigger)
        {
            string Join(string name) => string.Join(", ", Items(trigger, name).Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()).Where(f => !string.IsNullOrWhiteSpace(f)).Take(10));
            var branches = Join("branchFilters");
            var paths = Join("pathFilters");
            return (branches.Length > 0 ? $"branches [{branches}]" : "no branch filter") + (paths.Length > 0 ? $"; paths [{paths}]" : "");
        }

        private static string Days(int? mask)
        {
            if (mask is null or <= 0) return "no days";
            var names = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            var days = names.Where((_, i) => (mask.Value & (1 << i)) != 0).ToArray();
            return days.Length == 7 ? "every day" : string.Join(",", days);
        }

        // ---- yaml ----

        private static YamlScalarNode Key(string key) => new(key) { Style = ScalarStyle.Plain };

        private static void Add(YamlMappingNode map, string key, YamlNode value) => map.Children.Add(Key(key), value);

        private static YamlScalarNode Plain(string value) => new(value) { Style = ScalarStyle.Plain };

        // free text from the definition: control characters out, line breaks normalized, quoted so a value is never read as anything but a string
        private YamlScalarNode Free(string value)
        {
            var clean = Clean(value);
            if (clean.Length > MaxValueChars) _exceeded = true;
            return new(clean) { Style = clean.Contains('\n') ? ScalarStyle.Literal : ScalarStyle.SingleQuoted };
        }

        private static string Clean(string value)
        {
            var text = value.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n').Replace('\u0085', '\n');
            var sb = new StringBuilder(text.Length);
            foreach (var c in text) if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string JobId(string text, int index)
        {
            var id = Regex.Replace(text, "[^A-Za-z0-9_]", "_", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim('_');
            if (id.Length == 0 || !char.IsLetter(id[0]) && id[0] != '_') id = "Job_" + (index + 1) + (id.Length > 0 ? "_" + id : "");
            return id.Length > 100 ? id[..100] : id;
        }

        private static string Save(YamlMappingNode root)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
            // Save writes the end-of-document marker ("...") after a mapping, never a start marker ("---"): the text is a comment header and one document
            var lines = writer.ToString().Replace("\r\n", "\n").Split('\n').ToList();
            while (lines.Count > 0 && (lines[^1].Length == 0 || lines[^1] == "...")) lines.RemoveAt(lines.Count - 1);
            return string.Join('\n', lines) + "\n";
        }

        private void Note(string note)
        {
            var one = Clean(note).Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (one.Length > MaxNoteChars) one = one[..MaxNoteChars];
            if (_notes.Count < MaxNotes) _notes.Add(one);
        }

        private IReadOnlyList<string> Notes() => _notes.ToArray();
    }
}
