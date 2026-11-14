using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Business;

public sealed record PlanEntry(string Name, string Origin);

// How one Azure service connection is meant to be reached from a workflow, and which names that creates.
public sealed record ConnectionMapping(string Connection, string Kind, string Guidance, IReadOnlyList<string> Secrets, IReadOnlyList<string> Variables);

// The only secrets and variables a converted workflow may reference. Deterministic: a model is told the list, the gates
// enforce it, and nothing is created at the destination from it (that stays a human step). Values never appear here.
// RunnerLabels are the extra self-hosted labels the source's pools demand (see AzureRunnerLabels); plans stored before they existed have none.
public sealed record ConversionPlan(IReadOnlyList<PlanEntry> Secrets, IReadOnlyList<PlanEntry> Variables,
    IReadOnlyList<ConnectionMapping> Connections, IReadOnlyList<string> Notes, IReadOnlyList<string>? RunnerLabels = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Labels => RunnerLabels ?? [];

    public WorkflowPlanContext ToGateContext(string? sourceYaml) =>
        WorkflowPlanContext.Of(Secrets.Select(s => s.Name), Variables.Select(v => v.Name)) with { SourceTasks = SourceTaskInventory.From(sourceYaml), RunnerLabels = Labels };

    // The same context from a stored task list, so a stored proposal can be gated again without its source text.
    public WorkflowPlanContext ToGateContextFor(IEnumerable<string> sourceTasks) =>
        WorkflowPlanContext.Of(Secrets.Select(s => s.Name), Variables.Select(v => v.Name)) with
        {
            SourceTasks = new HashSet<string>(sourceTasks, StringComparer.OrdinalIgnoreCase), RunnerLabels = Labels
        };

    // Labels enter the hash only when there are some, so the hash of a plan without them is what it always was.
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', Secrets.Select(s => "s:" + s.Name).Order(StringComparer.Ordinal).Concat(Variables.Select(v => "v:" + v.Name).Order(StringComparer.Ordinal))
            .Concat(Labels.Select(l => "l:" + l).Order(StringComparer.Ordinal))))));
}

public static partial class ConversionPlanBuilder
{
    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlnum();

    // GitHub names: letters, digits and underscore, not starting with a digit or "GITHUB_".
    public static string GitHubName(string raw, bool upper)
    {
        var name = NonAlnum().Replace(raw ?? "", "_").Trim('_');
        if (name.Length == 0) name = "X";
        if (char.IsDigit(name[0])) name = "_" + name;
        if (name.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase)) name = "AZ_" + name;
        if (upper) name = name.ToUpperInvariant();
        return name.Length > 100 ? name[..100] : name;
    }

    public static ConversionPlan Build(AzurePipelineSource pipeline, string? sourceYaml)
    {
        var secrets = new List<PlanEntry>();
        var variables = new List<PlanEntry>();
        var notes = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(List<PlanEntry> into, string name, string origin)
        {
            if (taken.Add(name)) into.Add(new(name, origin));
        }

        foreach (var variable in pipeline.Variables.DistinctBy(v => (v.Name, v.Group)))
        {
            var name = GitHubName(variable.Name, upper: false);
            var origin = (variable.Group == "pipeline" ? "pipeline variable" : $"variable group '{variable.Group}'") +
                (name != variable.Name ? $", renamed from '{variable.Name}'" : "");
            if (!taken.Contains(name)) Add(variable.IsSecret ? secrets : variables, name, origin);
            else notes.Add($"Variable '{variable.Name}' ({variable.Group}) collides with another name and is not mapped.");
        }

        var connections = new List<ConnectionMapping>();
        foreach (var connection in pipeline.ServiceConnections.DistinctBy(c => c.Name))
        {
            // The project inventory is not pipeline-scoped: only connections the YAML names are mapped.
            if (string.IsNullOrEmpty(sourceYaml) || !Regex.IsMatch(sourceYaml, @"(?<![\w.-])" + Regex.Escape(connection.Name) + @"(?![\w.-])", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))
                continue;
            var mapping = Map(connection);
            connections.Add(mapping);
            foreach (var secret in mapping.Secrets) Add(secrets, secret, $"service connection '{connection.Name}' ({connection.Kind})");
            foreach (var variable in mapping.Variables) Add(variables, variable, $"service connection '{connection.Name}' ({connection.Kind})");
        }
        notes.Add("Service connections are mapped only when the pipeline YAML names them; the project inventory is not pipeline-scoped.");
        var labels = AzureRunnerLabels.From(sourceYaml);
        if (labels.Count > 0)
            notes.Add($"The pipeline's self-hosted pools demand {string.Join(", ", labels)}: a workflow may use these as extra runner labels, and the destination needs runners that have them.");
        return new(secrets, variables, connections, notes, labels.Count == 0 ? null : labels);
    }

    private static ConnectionMapping Map(AzureServiceReference connection)
    {
        var n = GitHubName(connection.Name, upper: true);
        var kind = connection.Kind.ToLowerInvariant();
        return kind switch
        {
            "azurerm" or "azure" => new(connection.Name, connection.Kind, "Log in with azure/login using OIDC (the job needs permissions: id-token: write).",
                ["AZURE_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID"], []),
            "dockerregistry" => new(connection.Name, connection.Kind, "Log in with docker/login-action (registry, username, password).", [$"{n}_USERNAME", $"{n}_PASSWORD"], []),
            "externalnugetfeed" => new(connection.Name, connection.Kind, "dotnet nuget push --source <url> --api-key <key>, both passed through env.", [$"{n}_API_KEY"], [$"{n}_URL"]),
            "externalnpmregistry" or "npm" => new(connection.Name, connection.Kind, "Registry URL via setup-node registry-url, token via NODE_AUTH_TOKEN in env.", [$"{n}_TOKEN"], [$"{n}_URL"]),
            "sonarqube" => new(connection.Name, connection.Kind, "dotnet sonarscanner / the scanner CLI with the token and host URL passed through env.", ["SONAR_TOKEN"], ["SONAR_HOST_URL"]),
            "ssh" => new(connection.Name, connection.Kind, "ssh/rsync with a key written from the secret and a pinned known_hosts (StrictHostKeyChecking=yes).",
                [$"{n}_KEY", $"{n}_KNOWN_HOSTS"], [$"{n}_HOST", $"{n}_USER"]),
            "kubernetes" => new(connection.Name, connection.Kind, "azure/aks-set-context (after an Azure login) with the cluster and resource group variables, then kubectl.", [], [$"{n}_CLUSTER", $"{n}_RESOURCE_GROUP"]),
            "github" => new(connection.Name, connection.Kind, "Prefer the built-in GITHUB_TOKEN; use the secret only for another repository.", [$"{n}_TOKEN"], []),
            _ => new(connection.Name, connection.Kind, "No safe automatic mapping: treat the tasks that use it as manual.", [$"{n}_CREDENTIAL"], [])
        };
    }
}
