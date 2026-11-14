using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Domain.Tests.WorkflowGates;

public sealed record OrionSource(string Repo, string Path);
public sealed record OrionPlan(string[] Secrets, string[] Variables);
public sealed record OrionHost(string Host, string Why);
public sealed record OrionTask(string Azure, string Github, string? Note = null);
public sealed record OrionUnsupported(string Azure, string Reason, string Suggestion);
public sealed record OrionTemplate(string Template, string HandledBy);
public sealed record OrionWarning(string Code, string Why);
public sealed record OrionRedaction(string[] Findings);

// What a human established about converting one pipeline. The sidecar is evidence, not a claim of equivalence:
// everything that was not converted faithfully is listed in Unsupported or described in Notes.
public sealed record OrionSidecar(
    int PipelineId, string Name, OrionSource Source, string Equivalence, string[] OriginalTriggers, OrionPlan Plan,
    OrionHost[] ExtraAllowedHosts, OrionTask[] TaskMap, OrionUnsupported[] Unsupported, OrionTemplate[] Templates,
    OrionWarning[] AcceptedWarnings, OrionRedaction Redaction, string[] Notes);

public sealed record OrionCase(string Name, string SourceYaml, string GoldenYaml, OrionSidecar Sidecar)
{
    public override string ToString() => Name;

    // The default policy plus the hosts this plan explicitly accepts (its own endpoints); nothing else is relaxed.
    public WorkflowGatePolicy Policy()
    {
        var hosts = new HashSet<string>(WorkflowGatePolicy.Default.AllowedHosts, StringComparer.OrdinalIgnoreCase);
        foreach (var extra in Sidecar.ExtraAllowedHosts) hosts.Add(extra.Host);
        return new WorkflowGatePolicy { AllowedHosts = hosts };
    }

    // What the product's Azure reader would return for this pipeline: variables with their secret flags (pipeline and
    // linked groups), and the project's whole service-connection inventory. Rebuilt from the estate seed, no values.
    public AzurePipelineSource Pipeline()
    {
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(OrionCorpus.RepositoryRoot(), "tools", "AdoSim", "seed", "orion-seed.json")));
        var root = seed.RootElement;
        var definition = root.GetProperty("projects").EnumerateArray().SelectMany(p => p.GetProperty("pipelines").EnumerateArray())
            .Single(p => p.GetProperty("id").GetInt32() == Sidecar.PipelineId);
        var variables = new List<AzureVariableReference>();
        if (definition.TryGetProperty("variables", out var own))
            foreach (var v in own.EnumerateArray()) variables.Add(new(v.GetProperty("name").GetString()!, v.TryGetProperty("secret", out var sec) && sec.GetBoolean(), "pipeline"));
        if (definition.TryGetProperty("variableGroups", out var groups))
            foreach (var id in groups.EnumerateArray().Select(g => g.GetInt32()))
            {
                var group = root.GetProperty("variableGroups").EnumerateArray().Single(g => g.GetProperty("id").GetInt32() == id);
                foreach (var v in group.GetProperty("variables").EnumerateArray())
                    variables.Add(new(v.GetProperty("name").GetString()!, v.TryGetProperty("secret", out var sec) && sec.GetBoolean(), group.GetProperty("name").GetString()!));
            }
        var connections = root.GetProperty("serviceEndpoints").EnumerateArray().Select(e => new AzureServiceReference(e.GetProperty("name").GetString()!, e.GetProperty("type").GetString()!)).ToArray();
        return new(Sidecar.PipelineId, Sidecar.Name, "yaml", Sidecar.Source.Path, Sidecar.Source.Repo, variables, connections, "available", AzureYamlTaskAnalyzer.Analyze(SourceYaml));
    }

    // The runner labels are what the product derives from the source's pools, exactly as a conversion does; the sidecar does not get to name any.
    public WorkflowPlanContext Plan() => WorkflowPlanContext.Of(Sidecar.Plan.Secrets, Sidecar.Plan.Variables) with
    {
        SourceTasks = SourceTaskInventory.From(SourceYaml), RunnerLabels = AzureRunnerLabels.From(SourceYaml)
    };

    // The gates a converted workflow has to pass, accounting included.
    public WorkflowGatePipeline PolicyGates() => new([new WorkflowPolicyGate(), new LinterSuppressionGate(), new TaskAccountingGate()], Policy());
}

// The Orion estate as served by tools/AdoSim (its pipeline YAML) against hand-authored workflows. Reusable by the AI
// converter tests: score a candidate workflow with the same gates and plan, compare it with the golden one.
public static class OrionCorpus
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "AdoSim", "seed", "orion-seed.json"))) return dir.FullName;
        throw new InvalidOperationException("tools/AdoSim/seed/orion-seed.json not found above " + AppContext.BaseDirectory);
    }

    private static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "WorkflowGates", "Golden", "orion");

    public static IReadOnlyList<OrionCase> Load()
    {
        var root = RepositoryRoot();
        var cases = new List<OrionCase>();
        foreach (var sidecarFile in Directory.EnumerateFiles(GoldenDirectory, "*.expected.json").Order())
        {
            var name = Path.GetFileName(sidecarFile)[..^".expected.json".Length];
            var sidecar = JsonSerializer.Deserialize<OrionSidecar>(File.ReadAllText(sidecarFile), Json)
                ?? throw new InvalidOperationException(sidecarFile + " is empty");
            var source = Path.Combine(root, "tools", "AdoSim", "seed", "files", sidecar.Source.Repo, sidecar.Source.Path.TrimStart('/'));
            cases.Add(new(name, File.ReadAllText(source), File.ReadAllText(Path.Combine(GoldenDirectory, name + ".yml")), sidecar));
        }
        return cases;
    }

    public static OrionCase Get(string name) => Load().Single(c => c.Name == name);

    public static IEnumerable<object[]> Names() => Load().Select(c => new object[] { c.Name });
}
