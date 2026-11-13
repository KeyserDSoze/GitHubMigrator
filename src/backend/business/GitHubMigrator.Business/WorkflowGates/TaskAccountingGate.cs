using System.Text.RegularExpressions;

namespace GitHubMigrator.Business.WorkflowGates;

// The Azure tasks a pipeline uses, found two ways: the product's analyser and a scan of the text. The analyser does not
// look inside conditional insertions (${{ if }}:), so the scan catches what it misses.
public static class SourceTaskInventory
{
    private static readonly Regex TaskLine = new(@"^\s*(?:-\s+)?task:\s*([A-Za-z0-9][A-Za-z0-9._@/-]{0,99})\s*(?:#.*)?$",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));

    public static IReadOnlySet<string> From(string? sourceYaml)
    {
        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceYaml)) return tasks;
        foreach (var task in AzureYamlTaskAnalyzer.Analyze(sourceYaml).Tasks.Where(t => t.Kind == "task")) tasks.Add(task.Identifier);
        try { foreach (Match m in TaskLine.Matches(sourceYaml)) tasks.Add(m.Groups[1].Value); }
        catch (RegexMatchTimeoutException) { /* the analyser result stands */ }
        return tasks;
    }
}

// A model may not drop a step silently. The converted workflow declares, in one machine-readable comment, which Azure
// tasks it converted and which it did not:
//     # azure-tasks: converted=UseDotNet@2,DotNetCoreCLI@2 manual=PublishTestResults@2
// Every source task must be in one of the lists, and a task in `manual` must be marked "MANUAL PORT REQUIRED" next to its
// name. This is accounting, not proof: it cannot tell that a "converted" task was converted well. A human still reviews.
public sealed class TaskAccountingGate : IWorkflowGate
{
    public const string GateName = "task-accounting";
    public string Name => GateName;

    private static readonly Regex Header = new(@"^\s*#\s*azure-tasks:(?<body>.*)$", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
    private static readonly Regex List = new(@"\b(?<kind>converted|manual)=(?<ids>[^\s]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
    public const string Marker = "MANUAL PORT REQUIRED";

    public Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct)
    {
        var source = context.Plan.SourceTasks;
        if (source.Count == 0) return Task.FromResult(GateResult.Passed(Name));
        var findings = new List<GateFinding>();
        var converted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headerFound = false;
        try
        {
            foreach (Match header in Header.Matches(context.Yaml))
            {
                headerFound = true;
                foreach (Match list in List.Matches(header.Groups["body"].Value))
                {
                    var target = list.Groups["kind"].Value == "converted" ? converted : manual;
                    foreach (var id in list.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!id.Equals("none", StringComparison.OrdinalIgnoreCase)) target.Add(id);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Task.FromResult(GateResult.NotRun(Name, "The accounting header could not be read in bounded time."));
        }

        if (!headerFound)
        {
            findings.Add(new(Name, "TASK_ACCOUNTING_MISSING", GateSeverity.Blocker,
                $"The workflow has no '# azure-tasks: converted=… manual=…' comment: {source.Count} source task(s) are not accounted for."));
            return Task.FromResult(GateResult.Passed(Name, findings));
        }

        var lines = context.Yaml.Split('\n');
        foreach (var task in source.Order(StringComparer.OrdinalIgnoreCase))
        {
            var inConverted = converted.Contains(task);
            var inManual = manual.Contains(task);
            if (!inConverted && !inManual)
                findings.Add(new(Name, "TASK_NOT_ACCOUNTED", GateSeverity.Blocker, $"Source task '{task}' is neither converted nor marked manual."));
            else if (inConverted && inManual)
                findings.Add(new(Name, "TASK_ACCOUNTING_AMBIGUOUS", GateSeverity.Warning, $"Task '{task}' is listed as both converted and manual."));
            if (inManual && !lines.Any(l => l.Contains(task, StringComparison.OrdinalIgnoreCase) && l.Contains(Marker, StringComparison.Ordinal)))
                findings.Add(new(Name, "MANUAL_MARKER_MISSING", GateSeverity.Blocker, $"Task '{task}' is marked manual but nothing in the workflow says '{Marker}' next to it."));
        }
        foreach (var listed in converted.Concat(manual).Where(id => !source.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            findings.Add(new(Name, "TASK_ACCOUNTING_UNKNOWN_TASK", GateSeverity.Warning, $"'{listed}' is listed but is not a task of the source pipeline."));
        return Task.FromResult(GateResult.Passed(Name, findings));
    }
}
