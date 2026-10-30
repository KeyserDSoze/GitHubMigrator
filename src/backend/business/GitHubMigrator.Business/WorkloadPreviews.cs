using System.Text;
using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// All workbench outputs are explicit review drafts. Never silently deploy a workflow or create an issue.
public sealed record WorkloadIssue(string Code, string Detail, string Severity = "warning");
public sealed record PipelineTaskEvidence(string Kind, string Identifier);
public sealed record PipelineStructure(IReadOnlyList<PipelineTaskEvidence> Tasks,
    bool Truncated, bool ParseFailed);
public sealed record AzurePipelineSource(
    int Id, string Name, string Kind, string? YamlPath, string? RepositoryId,
    IReadOnlyList<AzureVariableReference> Variables, IReadOnlyList<AzureServiceReference> ServiceConnections,
    string? SourceYaml, PipelineStructure? Structure = null);
public sealed record AzureVariableReference(string Name, bool IsSecret, string Group);
public sealed record AzureServiceReference(string Name, string Kind);
public sealed record GitHubConfigNames(IReadOnlyList<string> Secrets, IReadOnlyList<string> Variables);
public sealed record ConfigurationStep(string Name, string Kind, bool ExistsOnGitHub, string Instructions);
public sealed record AiReview(string Status, IReadOnlyList<string> Findings);
public sealed record PipelineBlueprint(
    int PipelineId, string SourceName, string TargetRepository, string WorkflowPath,
    string WorkflowYaml, IReadOnlyList<ConfigurationStep> SetupSteps, IReadOnlyList<WorkloadIssue> Issues,
    AiReview Ai, bool DeployableWithoutReview);

public sealed record AzureBoardLink(string Kind, string Target, int? RelatedWorkItemId = null, string? RelationType = null);
public sealed record AzureBoardItem(int Id, string Title, string Type, string State,
    string? Description, IReadOnlyList<AzureBoardLink> Links);
public sealed record BoardIssueDraft(int SourceId, string Title, string Body, string State,
    IReadOnlyList<WorkloadIssue> Issues);
public sealed record BoardsPreview(IReadOnlyList<BoardIssueDraft> Issues,
    IReadOnlyList<WorkloadIssue> GlobalWarnings, bool ReadyToPublish);

public interface IAzureWorkloadReader
{
    Task<AzurePipelineSource> GetPipelineAsync(string organization, string projectId,
        int pipelineId, string token, CancellationToken ct);
    Task<IReadOnlyList<AzureBoardItem>> GetBoardItemsAsync(string organization, string projectId,
        IReadOnlyList<int> itemIds, string token, CancellationToken ct);
}

public interface IGitHubConfigurationReader
{
    Task<GitHubConfigNames> GetNamesAsync(string organization, string repository,
        string token, CancellationToken ct);
}

public interface IPipelineAiReviewer
{
    Task<AiReview> ReviewAsync(string sourceSummary, string proposedWorkflow,
        IReadOnlyList<ConfigurationStep> steps, CancellationToken ct);
}

public sealed class WorkloadInputException(string code) : Exception("Invalid workload request")
{
    public string Code { get; } = code;
}

public sealed class WorkloadPreviewService(
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IAzureWorkloadReader source,
    IGitHubConfigurationReader target,
    IPipelineAiReviewer ai)
{
    private static readonly Regex SimpleKey = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RepoKey = new(@"^[A-Za-z0-9_.-]{1,100}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex WorkflowKey = new(@"[^a-z0-9-]+", RegexOptions.Compiled);
    private static readonly Regex CommitSha = new(@"(?<![a-fA-F0-9])([a-fA-F0-9]{40})(?![a-fA-F0-9])",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public async Task<PipelineBlueprint> PreviewPipelineAsync(Guid sourceId, Guid destinationId,
        string projectId, int pipelineId, string targetRepo, CancellationToken ct)
    {
        ValidateSimple(projectId);
        ValidateRepo(targetRepo);
        if (pipelineId < 1) throw new WorkloadInputException("InvalidPipelineId");
        var (src, srcToken) = await SourceAsync(sourceId, ct);
        var (dst, dstToken) = await DestinationAsync(destinationId, ct);
        var pipeline = await source.GetPipelineAsync(src.Organization, projectId, pipelineId, srcToken, ct);
        var warnings = new List<WorkloadIssue>();
        if (pipeline.Kind != "yaml")
            warnings.Add(new("ClassicPipelineUnsupported", "Classic build/release tasks need a manual workflow rewrite.", "blocker"));
        if (pipeline.SourceYaml is null)
            warnings.Add(new("SourceYamlUnavailable", "Source YAML was not readable or the pipeline is classic.", "blocker"));
        var name = WorkflowKey.Replace(pipeline.Name.ToLowerInvariant().Replace(' ', '-'), "-").Trim('-');
        if (name.Length == 0) name = "pipeline";
        if (name.Length > 60) name = name[..60].Trim('-');
        var workflowPath = $".github/workflows/azdo-{pipeline.Id}-{name}.yml";

        // Safe disabled scaffold: a run does not execute unreviewed arbitrary source tasks.
        // Developers must replace the explicit manual steps before enabling a trigger.
        var yaml = new StringBuilder();
        yaml.AppendLine("# MIGRATION DRAFT — F-009. Review and replace TODOs before enabling.");
        yaml.AppendLine($"name: 'Migrated Azure pipeline {pipeline.Id}'");
        yaml.AppendLine("on:");
        yaml.AppendLine("  workflow_dispatch:");
        yaml.AppendLine("permissions:");
        yaml.AppendLine("  contents: read");
        yaml.AppendLine("jobs:");
        yaml.AppendLine("  migration-review:");
        yaml.AppendLine("    if: ${{ false }} # deliberate safety gate");
        yaml.AppendLine("    runs-on: ubuntu-latest");
        yaml.AppendLine("    steps:");
        yaml.AppendLine("      - uses: actions/checkout@v4");
        yaml.AppendLine("      - name: Manual migration required");
        yaml.AppendLine("        run: echo 'TODO review Azure Pipelines tasks, agents, conditions and service connections'");
        if (pipeline.Structure is { ParseFailed: true })
            warnings.Add(new("SourceYamlAnalysisFailed",
                "YAML is unreadable or exceeds safe analysis limits; review the source manually.", "blocker"));
        if (pipeline.Structure is { Truncated: true })
            warnings.Add(new("SourceYamlAnalysisTruncated",
                "Task inventory reached safety limits; do not assume all tasks are listed.", "blocker"));
        if (pipeline.Structure is { Tasks.Count: > 0 } structure)
        {
            for (var index = 0; index < structure.Tasks.Count; index++)
            {
                var task = structure.Tasks[index];
                yaml.AppendLine($"      - name: 'REVIEW {index + 1}: {task.Identifier}'");
                yaml.AppendLine($"        run: echo 'TODO translate Azure {task.Kind} task {index + 1}'");
                warnings.Add(new("TaskTranslationPending",
                    $"Azure step {index + 1} ({task.Kind}: {task.Identifier}) needs explicit GitHub Actions translation.", "blocker"));
            }
        }
        var safeYaml = yaml.ToString();

        GitHubConfigNames existing;
        try { existing = await target.GetNamesAsync(dst.Organization, targetRepo, dstToken, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add(new("GitHubConfigUnreadable", "Cannot inspect destination secret and variable names.", "blocker"));
            existing = new([], []);
        }
        var namesSecrets = existing.Secrets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var namesVars = existing.Variables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = new List<ConfigurationStep>();
        foreach (var variable in pipeline.Variables.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!Regex.IsMatch(variable.Name, @"^[A-Za-z_][A-Za-z0-9_]*$")) {
                warnings.Add(new("InvalidVariableName", "An Azure variable name requires manual adaptation."));
                continue;
            }
            var exists = variable.IsSecret ? namesSecrets.Contains(variable.Name) : namesVars.Contains(variable.Name);
            steps.Add(new(variable.Name, variable.IsSecret ? "secret" : "variable", exists,
                variable.IsSecret
                    ? $"GitHub repository Settings > Secrets and variables > Actions > Secrets > New repository secret: {variable.Name}. Recreate the value from its original secure owner; APIs do not reveal it."
                    : $"GitHub repository Settings > Secrets and variables > Actions > Variables > New repository variable: {variable.Name}. Confirm the value manually; no source values are copied."));
        }
        foreach (var endpoint in pipeline.ServiceConnections)
            steps.Add(new(endpoint.Name, "service-connection", false,
                $"Reconfigure Azure DevOps service connection '{endpoint.Name}' ({endpoint.Kind}) as GitHub OIDC/federated credential, environment and cloud role, or an approved secret. Validate permissions in the target cloud."));
        if (steps.Any(x => !x.ExistsOnGitHub))
            warnings.Add(new("ConfigurationMissing", "Destination has missing configuration steps.", "blocker"));
        warnings.Add(new("WorkflowConversionRequiresReview",
            "The generated workflow is a non-executing scaffold; translate tasks and triggers, test, then approve.", "blocker"));

        // Never send YAML contents or provider secret values to the model: even YAML can contain
        // embedded credentials, and the originating repository is not a trusted prompt.
        AiReview review;
        try
        {
            var summary = $"Azure pipeline ID {pipeline.Id}; kind {pipeline.Kind}; " +
                $"variables count {pipeline.Variables.Count}; connection types " +
                string.Join(",", pipeline.ServiceConnections.Select(e => e.Kind).Distinct()) +
                $"; task identifiers: " + string.Join(",",
                    pipeline.Structure?.Tasks.Take(60).Select(t => t.Kind + ":" + t.Identifier)
                    ?? Array.Empty<string>());
            review = await ai.ReviewAsync(summary, safeYaml, steps, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            review = new("unavailable", ["AI review unavailable; human review is mandatory."]);
            warnings.Add(new("AiUnavailable", "AI assessment could not be completed."));
        }
        return new(pipeline.Id, pipeline.Name, targetRepo, workflowPath, safeYaml, steps, warnings, review, false);
    }

    public async Task<BoardsPreview> PreviewBoardsAsync(Guid sourceId, string projectId,
        IReadOnlyList<int>? ids, IReadOnlyDictionary<int, int>? issueNumbers, CancellationToken ct)
    {
        ValidateSimple(projectId);
        if (ids is null || ids.Count is < 1 or > 100 || ids.Any(x => x <= 0) || ids.Distinct().Count() != ids.Count)
            throw new WorkloadInputException("InvalidWorkItemIds");
        var (src, srcToken) = await SourceAsync(sourceId, ct);
        var items = await source.GetBoardItemsAsync(src.Organization, projectId, ids, srcToken, ct);
        var byId = items.ToDictionary(i => i.Id);
        var drafts = new List<BoardIssueDraft>();
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var item)) {
                drafts.Add(new(id, $"Missing Azure work item #{id}", "", "unknown",
                    [new("WorkItemMissing", $"Azure DevOps item {id} could not be read.", "blocker")]));
                continue;
            }
            var issues = new List<WorkloadIssue>();
            var body = new StringBuilder();
            body.AppendLine($"<!-- azdo-workitem:{src.Organization}:{projectId}:{id} -->");
            body.AppendLine($"**Azure DevOps ID:** {id}");
            body.AppendLine($"**Original type:** {Safe(item.Type)}");
            body.AppendLine($"**Original state:** {Safe(item.State)}");
            body.AppendLine($"**Source:** https://dev.azure.com/{src.Organization}/{Uri.EscapeDataString(projectId)}/_workitems/edit/{id}");
            body.AppendLine();
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                body.AppendLine("### Source description (plain text)");
                body.AppendLine(Safe(item.Description));
                body.AppendLine();
            }
            if (item.Links.Count > 0) body.AppendLine("### Traceability");
            foreach (var link in item.Links.Take(250))
            {
                if (link.Kind == "work-item" && link.RelatedWorkItemId is > 0)
                {
                    if (issueNumbers is not null && issueNumbers.TryGetValue(link.RelatedWorkItemId.Value, out var num) && num > 0)
                        body.AppendLine($"- Azure relation #{link.RelatedWorkItemId}: target issue #{num} (verify hierarchy/semantics)");
                    else
                    {
                        body.AppendLine($"- Azure relation #{link.RelatedWorkItemId}: **not mapped to a GitHub issue**");
                        issues.Add(new("RelatedIssueUnmapped", $"Related Azure work item {link.RelatedWorkItemId} not in target mapping."));
                    }
                }
                else if (link.Kind == "commit")
                {
                    var sha = CommitSha.Match(link.Target);
                    if (sha.Success) {
                        body.AppendLine($"- Commit `{sha.Groups[1].Value}` (source reference; verify SHA in migrated GitHub repository)");
                        issues.Add(new("CommitTargetUnverified", "Commit SHA not yet verified in GitHub."));
                    }
                    else issues.Add(new("CommitReferenceUnresolved", "Commit artifact does not contain a verifiable full SHA."));
                }
                else if (link.Kind == "pull-request")
                {
                    body.AppendLine("- Linked Azure DevOps pull request requires explicit source-to-GitHub PR mapping.");
                    issues.Add(new("PullRequestTargetUnmapped", "Source pull request has no verified GitHub PR mapping."));
                }
                else
                {
                    issues.Add(new("UnknownRelation", "Work item contains a relation not yet supported by the migration mapper."));
                }
            }
            if (item.Links.Count > 250) issues.Add(new("RelationsTruncated", "More than 250 relations in source item.", "blocker"));
            drafts.Add(new(id, Safe(item.Title, 180), body.ToString(), item.State, issues));
        }
        var global = new List<WorkloadIssue>();
        if (drafts.Any(d => d.Issues.Count > 0))
            global.Add(new("TraceabilityIncomplete", "Some work item links could not be verified or mapped.", "blocker"));
        global.Add(new("IssueNumbersUnassigned",
            "Original Azure IDs will be preserved in issue bodies; GitHub issue numbers are allocated by GitHub.", "warning"));
        return new(drafts, global, false);
    }

    private async Task<(MigrationConnection, string)> SourceAsync(Guid id, CancellationToken ct)
    {
        var c = await connections.GetAsync(id, ct) ?? throw new WorkloadInputException("SourceNotFound");
        if (c.Direction != ConnectionDirection.Source || c.ProviderKey != "azure-devops-services")
            throw new WorkloadInputException("UnsupportedSource");
        var token = await secrets.ReadAsync(c.SecretReference, ct);
        if (string.IsNullOrEmpty(token)) throw new WorkloadInputException("SourceCredentialMissing");
        return (c, token);
    }

    private async Task<(MigrationConnection, string)> DestinationAsync(Guid id, CancellationToken ct)
    {
        var c = await connections.GetAsync(id, ct) ?? throw new WorkloadInputException("DestinationNotFound");
        if (c.Direction != ConnectionDirection.Destination || c.ProviderKey != "github-enterprise-cloud" ||
            c.ValidationCode != "Verified") throw new WorkloadInputException("UnsupportedDestination");
        var token = await secrets.ReadAsync(c.SecretReference, ct);
        if (string.IsNullOrEmpty(token)) throw new WorkloadInputException("DestinationCredentialMissing");
        return (c, token);
    }

    private static void ValidateSimple(string key)
    {
        if (string.IsNullOrEmpty(key) || !SimpleKey.IsMatch(key)) throw new WorkloadInputException("InvalidProjectId");
    }
    private static void ValidateRepo(string repo)
    {
        if (string.IsNullOrEmpty(repo) || !RepoKey.IsMatch(repo) || repo is "." or "..")
            throw new WorkloadInputException("InvalidRepository");
    }
    private static string Safe(string? raw, int limit = 10000)
    {
        // Prevent source HTML and Markdown constructs from turning into active content in a GitHub issue.
        var value = System.Net.WebUtility.HtmlDecode(Regex.Replace(raw ?? "", "<[^>]*>", " "));
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        value = Regex.Replace(value, @"[\u0000-\u001F\u007F]", " ");
        if (value.Length > limit) value = value[..limit] + " [truncated]";
        return value.Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
