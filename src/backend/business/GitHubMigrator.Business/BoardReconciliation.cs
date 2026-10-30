using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

public enum TraceabilityStatus { TargetExists, MissingMapping, NotFound, Unavailable, Unsupported, NotChecked }
public sealed record BoardLinkCheck(int WorkItemId, string SourceRelation,
    string SourceReference, string? TargetReference, TraceabilityStatus Status);
public sealed record BoardReconciliationReport(string TargetOrganization, string TargetRepository,
    IReadOnlyList<BoardLinkCheck> Links, int TargetObjectsFound, int UnresolvedLinks,
    IReadOnlyList<WorkloadIssue> Issues, bool ReadyToPublish);

public interface IGitHubTraceabilityReader
{
    // Only a target HTTP 200 with the expected entity type counts as verified.
    // A missing item returns false; provider errors throw (unknown != not found).
    Task<bool> IssueExistsAsync(string organization, string repository, int number, string token, CancellationToken ct);
    Task<bool> CommitExistsAsync(string organization, string repository, string sha, string token, CancellationToken ct);
    Task<bool> PullRequestExistsAsync(string organization, string repository, int number, string token, CancellationToken ct);
}

// Read-only, source-ID-centered reconciliation. Never manufacture a GitHub SHA or issue number.
public sealed class BoardReconciliationService(
    IConnectionRepository connections, IConnectionSecretStore secrets,
    IAzureWorkloadReader source, IGitHubTraceabilityReader github)
{
    private static readonly Regex Project = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Repository = new(@"^[A-Za-z0-9_.-]{1,100}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Sha = new(@"(?<![A-Fa-f0-9])([A-Fa-f0-9]{40})(?![A-Fa-f0-9])",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public async Task<BoardReconciliationReport> ReconcileAsync(
        Guid sourceConnectionId, Guid destinationConnectionId, string projectId,
        string targetRepository, IReadOnlyList<int>? workItemIds,
        IReadOnlyDictionary<int, int>? knownIssues,
        IReadOnlyDictionary<string, string>? knownCommits,
        IReadOnlyDictionary<int, int>? knownPullRequests, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(projectId) || !Project.IsMatch(projectId) ||
            string.IsNullOrEmpty(targetRepository) || !Repository.IsMatch(targetRepository) ||
            targetRepository is "." or ".." || workItemIds is null ||
            workItemIds.Count is < 1 or > 100 || workItemIds.Any(id => id <= 0) ||
            workItemIds.Distinct().Count() != workItemIds.Count)
            throw new WorkloadInputException("InvalidReconciliationInput");
        if ((knownIssues?.Count ?? 0) > 500 || (knownCommits?.Count ?? 0) > 500 ||
            (knownPullRequests?.Count ?? 0) > 500 ||
            (knownIssues?.Any(x => x.Key < 1 || x.Value < 1) ?? false) ||
            (knownPullRequests?.Any(x => x.Key < 1 || x.Value < 1) ?? false) ||
            (knownCommits?.Any(x => !Sha.IsMatch(x.Key) || !Sha.IsMatch(x.Value)) ?? false))
            throw new WorkloadInputException("InvalidTraceabilityMapping");

        var sourceConnection = await connections.GetAsync(sourceConnectionId, ct);
        var destination = await connections.GetAsync(destinationConnectionId, ct);
        if (sourceConnection is null || sourceConnection.Direction != ConnectionDirection.Source ||
            sourceConnection.ProviderKey != "azure-devops-services" ||
            destination is null || destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified")
            throw new WorkloadInputException("UnsupportedReconciliationConnection");

        var sourceToken = await secrets.ReadAsync(sourceConnection.SecretReference, ct);
        var destinationToken = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(sourceToken) || string.IsNullOrWhiteSpace(destinationToken))
            throw new WorkloadInputException("MissingConnectionCredential");

        var items = await source.GetBoardItemsAsync(sourceConnection.Organization, projectId,
            workItemIds, sourceToken, ct);
        var found = items.ToDictionary(x => x.Id);
        var links = new List<BoardLinkCheck>();
        var warnings = new List<WorkloadIssue>();
        var probes = 0;
        // Limit API calls and expose unchecked links. Never silently claim global completeness.
        const int MaxProbes = 120;
        foreach (var id in workItemIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!found.TryGetValue(id, out var item))
            {
                links.Add(new(id, "work-item", id.ToString(), null, TraceabilityStatus.NotFound));
                continue;
            }
            foreach (var link in item.Links)
            {
                if (link.Kind == "work-item")
                {
                    var related = link.RelatedWorkItemId;
                    if (related is null || knownIssues is null ||
                        !knownIssues.TryGetValue(related.Value, out var ghIssue))
                    {
                        links.Add(new(id, link.RelationType ?? "work-item", related?.ToString() ?? link.Target,
                            null, TraceabilityStatus.MissingMapping));
                        continue;
                    }
                    var targetRef = $"#{ghIssue}";
                    var status = await CheckAsync(() => github.IssueExistsAsync(
                        destination.Organization, targetRepository, ghIssue, destinationToken, ct));
                    links.Add(new(id, link.RelationType ?? "work-item", related.Value.ToString(), targetRef,
                        status));
                }
                else if (link.Kind == "commit")
                {
                    var match = Sha.Match(link.Target);
                    if (!match.Success)
                    {
                        links.Add(new(id, "commit", "unparsed", null, TraceabilityStatus.Unsupported));
                        continue;
                    }
                    var srcSha = match.Groups[1].Value;
                    var mappedSha = knownCommits?.GetValueOrDefault(srcSha) ?? srcSha;
                    if (!Sha.IsMatch(mappedSha))
                    {
                        links.Add(new(id, "commit", srcSha, null, TraceabilityStatus.MissingMapping));
                        continue;
                    }
                    var status = await CheckAsync(() => github.CommitExistsAsync(
                        destination.Organization, targetRepository, mappedSha, destinationToken, ct));
                    links.Add(new(id, "commit", srcSha, mappedSha, status));
                }
                else if (link.Kind == "pull-request")
                {
                    var suffix = link.Target.Split('/').LastOrDefault();
                    if (!int.TryParse(suffix, out var original) || original < 1)
                    {
                        links.Add(new(id, "pull-request", "unparsed", null, TraceabilityStatus.Unsupported));
                        continue;
                    }
                    if (knownPullRequests is null || !knownPullRequests.TryGetValue(original, out var ghPr))
                    {
                        links.Add(new(id, "pull-request", original.ToString(), null,
                            TraceabilityStatus.MissingMapping));
                        continue;
                    }
                    var status = await CheckAsync(() => github.PullRequestExistsAsync(
                        destination.Organization, targetRepository, ghPr, destinationToken, ct));
                    links.Add(new(id, "pull-request", original.ToString(), $"#{ghPr}", status));
                }
                else links.Add(new(id, link.RelationType ?? link.Kind, "unmapped-relation", null,
                    TraceabilityStatus.Unsupported));
            }
        }
        var verified = links.Count(x => x.Status == TraceabilityStatus.TargetExists);
        var unresolved = links.Count - verified;
        if (unresolved > 0)
            warnings.Add(new("UnresolvedTraceability", $"{unresolved} source links are not verified on GitHub.", "blocker"));
        warnings.Add(new("CoverageNotGuaranteed",
            "Target existence is not proof that GitHub issues reference those objects. Direct Azure work-item relations only were checked; commit messages, TFVC changesets and indirect links need separate scanning.", "blocker"));
        return new(destination.Organization, targetRepository, links, verified, unresolved, warnings, false);

        async Task<TraceabilityStatus> CheckAsync(Func<Task<bool>> check)
        {
            if (++probes > MaxProbes) return TraceabilityStatus.NotChecked;
            try { return await check() ? TraceabilityStatus.Verified : TraceabilityStatus.NotFound; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { return TraceabilityStatus.Unavailable; }
        }
    }
}
