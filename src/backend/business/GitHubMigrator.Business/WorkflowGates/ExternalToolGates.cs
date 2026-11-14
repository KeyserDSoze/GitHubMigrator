using System.Text.Json;

namespace GitHubMigrator.Business.WorkflowGates;

// Where a static analyser runs is infrastructure; what its answer means is policy (here).
public sealed record ToolRun(bool Completed, string Output, string? Detail = null, int ExitCode = 0);

// What a run may be told besides the workflow: labels the product itself derived from the source. Never anything a model wrote.
public sealed record WorkflowToolOptions(IReadOnlyList<string> RunnerLabels)
{
    public static WorkflowToolOptions None { get; } = new([]);
}

public interface IWorkflowToolRunner
{
    string Tool { get; }
    Task<ToolRun> RunAsync(string workflowYaml, CancellationToken ct);

    // A runner that can use the options overrides this; one that cannot simply runs the workflow as it always did.
    Task<ToolRun> RunAsync(string workflowYaml, WorkflowToolOptions options, CancellationToken ct) => RunAsync(workflowYaml, ct);
}

// actionlint: syntax, expressions, runner labels, needs graph, shell and script checks.
public sealed class ActionlintGate(IWorkflowToolRunner runner) : IWorkflowGate
{
    public const string GateName = "actionlint";
    public string Name => GateName;

    public async Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct)
    {
        // actionlint alone is told about the labels the source demanded: they are the only labels beyond GitHub's own that it accepts
        var run = context.Plan.RunnerLabels.Count == 0 ? await runner.RunAsync(context.Yaml, ct) : await runner.RunAsync(context.Yaml, new(context.Plan.RunnerLabels), ct);
        // exit 0 = clean, 1 = findings; anything else (3 = fatal) or silence is a tool that did not analyse the file
        var unusable = ToolOutput.Unusable(Name, run, exitCode => exitCode is 0 or 1);
        if (unusable is not null) return unusable;
        var findings = new List<GateFinding>();
        try
        {
            using var document = JsonDocument.Parse(run.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return GateResult.NotRun(Name, "actionlint returned an unexpected document.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var kind = item.TryGetProperty("kind", out var k) ? k.GetString() ?? "unknown" : "unknown";
                if (context.Policy.IgnoredActionlintKinds.Contains(kind)) continue;
                var message = item.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var line = item.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : (int?)null;
                // the finding text comes from a tool analysing untrusted input: bounded, single line
                findings.Add(new(Name, "ACTIONLINT_" + kind.ToUpperInvariant().Replace('-', '_'), GateSeverity.Blocker,
                    Bound(message), line));
            }
        }
        catch (JsonException) { return GateResult.NotRun(Name, "actionlint returned unreadable output."); }
        return GateResult.Passed(Name, findings);
    }

    internal static string Bound(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}

// zizmor: security audits (template injection, unpinned actions, dangerous triggers, excessive permissions).
// Findings an author muted with "# zizmor: ignore" still count: generated output may not silence its own linter.
public sealed class ZizmorGate(IWorkflowToolRunner runner) : IWorkflowGate
{
    public const string GateName = "zizmor";
    public string Name => GateName;

    public async Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct)
    {
        var run = await runner.RunAsync(context.Yaml, ct);
        // exit 0 = clean, 10..14 = findings by severity; 1/3 = could not audit (e.g. no auditable input)
        var unusable = ToolOutput.Unusable(Name, run, exitCode => exitCode is 0 or (>= 10 and <= 14));
        if (unusable is not null) return unusable;
        var findings = new List<GateFinding>();
        try
        {
            using var document = JsonDocument.Parse(run.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return GateResult.NotRun(Name, "zizmor returned an unexpected document.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var ident = item.TryGetProperty("ident", out var i) ? i.GetString() ?? "unknown" : "unknown";
                if (context.Policy.IgnoredZizmorIdents.Contains(ident)) continue;
                var severity = item.TryGetProperty("determinations", out var d) && d.TryGetProperty("severity", out var s) ? s.GetString() : null;
                var mapped = severity switch
                {
                    "High" => context.Policy.ZizmorHighIs,
                    "Medium" => context.Policy.ZizmorMediumIs,
                    _ => GateSeverity.Info
                };
                var message = item.TryGetProperty("desc", out var desc) ? desc.GetString() ?? "" : "";
                int? line = null;
                if (item.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0 &&
                    locations[0].TryGetProperty("concrete", out var concrete) && concrete.TryGetProperty("location", out var location) &&
                    location.TryGetProperty("start_point", out var start) && start.TryGetProperty("row", out var row) && row.TryGetInt32(out var r))
                    line = r + 1;
                findings.Add(new(Name, "ZIZMOR_" + ident.ToUpperInvariant().Replace('-', '_'), mapped, ActionlintGate.Bound(message), line));
            }
        }
        catch (JsonException) { return GateResult.NotRun(Name, "zizmor returned unreadable output."); }
        return GateResult.Passed(Name, findings);
    }
}

static class ToolOutput
{
    // A tool that crashed, was killed or printed nothing has not looked at the file: that must never read as "no findings".
    public static GateResult? Unusable(string gate, ToolRun run, Func<int, bool> acceptedExitCode)
    {
        if (!run.Completed) return GateResult.NotRun(gate, run.Detail ?? gate + " did not run.");
        if (!acceptedExitCode(run.ExitCode)) return GateResult.NotRun(gate, $"{gate} exited with unexpected code {run.ExitCode}.");
        if (string.IsNullOrWhiteSpace(run.Output)) return GateResult.NotRun(gate, gate + " produced no report.");
        return null;
    }
}

// Linter suppressions are ordinary comments to the YAML parser: without this rule a model could mute zizmor.
public sealed class LinterSuppressionGate : IWorkflowGate
{
    public const string GateName = "suppressions";
    public string Name => GateName;

    public Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct)
    {
        var findings = new List<GateFinding>();
        var lines = context.Yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains("zizmor:", StringComparison.OrdinalIgnoreCase) && lines[i].Contains("ignore", StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains("actionlint-ignore", StringComparison.OrdinalIgnoreCase) ||
                lines[i].Contains("nosec", StringComparison.OrdinalIgnoreCase))
                findings.Add(new(Name, "LINTER_SUPPRESSION_COMMENT", GateSeverity.Blocker, "A comment tries to silence a linter or scanner.", i + 1));
        return Task.FromResult(GateResult.Passed(Name, findings));
    }
}

// Runs every gate and says whether a human may review the workflow. It never edits the workflow.
public sealed class WorkflowGatePipeline(IEnumerable<IWorkflowGate> gates, WorkflowGatePolicy? policy = null)
{
    private readonly WorkflowGatePolicy _policy = policy ?? WorkflowGatePolicy.Default;
    private readonly IReadOnlyList<IWorkflowGate> _gates = gates.ToArray();

    // Structure, policy and suppressions are always required (a pipeline built without them is never approvable), then every
    // gate it was given (the analysers excepted: whether they are required is the operator's policy), then those analysers.
    public IReadOnlyList<string> RequiredGates =>
        [.. new[] { WorkflowStructureGate.GateName, WorkflowPolicyGate.GateName, LinterSuppressionGate.GateName }
            .Concat(_gates.Select(g => g.Name).Where(n => n is not (ActionlintGate.GateName or ZizmorGate.GateName)))
            .Concat(_policy.RequiredExternalGates.Order()).Distinct()];

    public async Task<WorkflowGateReport> RunAsync(string yaml, WorkflowPlanContext? plan, CancellationToken ct)
    {
        yaml ??= "";
        var context0 = new WorkflowGateContext(yaml, null, plan ?? WorkflowPlanContext.Empty, _policy);
        var structure = WorkflowStructureGate.Evaluate(yaml, _policy, out var parsed);
        var context = context0 with { Parsed = parsed };
        var results = new List<GateResult> { structure };
        var structurallySound = parsed is not null && structure.Status == GateStatus.Passed;
        foreach (var gate in _gates.Where(g => g.Name != WorkflowStructureGate.GateName))
        {
            ct.ThrowIfCancellationRequested();
            if (!structurallySound && gate.Name != LinterSuppressionGate.GateName)
            {
                results.Add(GateResult.NotRun(gate.Name, "The structure gate failed: the workflow is not analysable."));
                continue;
            }
            try { results.Add(await gate.EvaluateAsync(context, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // a gate that crashes is a gate that did not run: fail closed, never pass silently
                results.Add(GateResult.NotRun(gate.Name, "The gate failed to run (" + ex.GetType().Name + ")."));
            }
        }
        return new(WorkflowHash.Sha256(yaml), _policy.Version, results, RequiredGates);
    }

    public static WorkflowGatePipeline Create(IWorkflowToolRunner? actionlint, IWorkflowToolRunner? zizmor, WorkflowGatePolicy? policy = null,
        IEnumerable<IWorkflowGate>? extraGates = null)
    {
        var gates = new List<IWorkflowGate> { new WorkflowPolicyGate(), new LinterSuppressionGate() };
        if (extraGates is not null) gates.AddRange(extraGates);
        gates.Add(actionlint is null ? new MissingToolGate(ActionlintGate.GateName) : new ActionlintGate(actionlint));
        gates.Add(zizmor is null ? new MissingToolGate(ZizmorGate.GateName) : new ZizmorGate(zizmor));
        return new(gates, policy);
    }
}

public sealed class MissingToolGate(string name) : IWorkflowGate
{
    public string Name => name;
    public Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct) =>
        Task.FromResult(GateResult.NotRun(name, name + " is not installed or configured."));
}
