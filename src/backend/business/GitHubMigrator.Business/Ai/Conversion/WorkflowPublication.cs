using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// What the destination repository looks like right now, as far as publishing one workflow file on a new branch is concerned.
// Read only. The workflow text is compared inside the reader and never returned.
// PriorVersion: the file is exactly the text of the conversion this proposal was derived from (and approved), so it may be replaced by this one.
// It is only ever reported when the caller named that text; anything else that is not the approved text is Different.
public enum WorkflowFileState { Absent, Identical, Different, PriorVersion }

// How a plan puts the file in place: a new file, or the replacement of the approved text of the parent conversion, on a NEW branch either way.
public static class PublicationModes
{
    public const string Create = "Create";
    public const string Update = "Update";
}

// WorkflowFileBlobSha is git's object id of the file at the base commit, reported only for PriorVersion (the contents API needs it to replace a file).
public sealed record PublicationTarget(bool RepositoryExists, bool Archived, string? DefaultBranch, string? BaseBranch, string? BaseSha,
    WorkflowFileState WorkflowFile, bool HeadBranchExists, string? HeadBranchSha, IReadOnlyList<int> OpenPullRequests, string? WorkflowFileBlobSha = null);

public interface IGitHubPublicationReader
{
    // `baseBranch` null means the repository's default branch. Only GET requests, only the one repository.
    // `priorWorkflowSha256` is the hash of the text this one may replace (the approved parent conversion), or null when nothing may be replaced.
    Task<PublicationTarget> ReadAsync(string organization, string repository, string? baseBranch, string headBranch, string workflowPath,
        string workflowSha256, string? priorWorkflowSha256, string token, CancellationToken ct);
    string GitUrl(string organization, string repository);
}

public sealed record PublicationFinding(string Code, string Severity, string Message);
// Code names the step so a screen can say it in the operator's language; Title is the English fallback.
public sealed record PublicationStep(string Code, string Title, string? Command);

// Everything a person needs to put ONE approved workflow in a pull request, and a hash that names exactly this plan: the repository,
// the base commit it was planned against, the branch, the path, the text and the words of the pull request. Producing it writes
// nothing anywhere: the person (or, later, an executor that has to present this hash) does the writing.
public sealed record PublicationPlan(Guid ProposalId, string WorkflowSha256, string Organization, string Repository, string BaseBranch, string? BaseSha,
    string HeadBranch, string WorkflowPath, string CommitMessage, string PullRequestTitle, string PullRequestBody,
    IReadOnlyList<PublicationFinding> Findings, bool Ready, IReadOnlyList<PublicationStep> Steps, string PlanSha256, DateTimeOffset ReadAtUtc,
    string Mode = PublicationModes.Create, string? ReplacesBlobSha = null, string? ReplacesWorkflowSha256 = null);

public static partial class WorkflowPublicationPlanner
{
    public const string PlanVersion = "publication-plan-v1";

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NameChars();

    [GeneratedRegex(@"^\s*#\s*azure-tasks:.*?\bmanual=(?<ids>\S*)", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex ManualHeader();

    // Same naming as the deterministic preview, so the plan and the preview agree on where the workflow lives.
    public static string WorkflowPathFor(int pipelineId, string? pipelineName)
    {
        var name = NameChars().Replace((pipelineName ?? "").ToLowerInvariant().Replace(' ', '-'), "-").Trim('-');
        if (name.Length == 0) name = "pipeline";
        if (name.Length > 60) name = name[..60].Trim('-');
        return $".github/workflows/azdo-{pipelineId}-{name}.yml";
    }

    // A new approved text gets a new branch; the same text always gets the same one, which is what makes a re-plan comparable.
    public static string HeadBranchFor(int pipelineId, string workflowSha256) => $"migrate/azdo-{pipelineId}-{workflowSha256[..8]}";

    public static PublicationPlan Build(ApprovedWorkflow approved, WorkflowProposal proposal, IReadOnlyList<ProposalEvent> events,
        string organization, string repository, string requestedBase, PublicationTarget? target, bool targetUnreadable, string gitUrl, DateTimeOffset now,
        string? priorWorkflowSha256 = null, IReadOnlyList<PublicationFinding>? extraFindings = null)
    {
        var path = WorkflowPathFor(proposal.PipelineId, proposal.Evidence.PipelineName);
        var head = HeadBranchFor(proposal.PipelineId, approved.WorkflowSha256);
        var baseBranch = target?.BaseBranch ?? requestedBase;
        var triggers = proposal.Kind == ProposalKinds.Triggers;
        // a file is replaced only when the caller named the text it may replace, the reader found exactly that text, and said which blob it is
        var update = triggers && priorWorkflowSha256 is not null && target is { WorkflowFile: WorkflowFileState.PriorVersion, WorkflowFileBlobSha: { } };
        var findings = Findings(target, targetUnreadable, update, priorWorkflowSha256);
        // things the destination alone does not show: what this product published earlier for the same file and has not seen merged or closed
        if (extraFindings is { Count: > 0 }) findings.AddRange(extraFindings);
        var approval = events.LastOrDefault(e => e.Type == "Approved");
        var title = update
            ? Truncate($"Add automatic triggers to the GitHub Actions workflow for Azure pipeline #{proposal.PipelineId} ({Plain(proposal.Evidence.PipelineName, 60)})", 120)
            : Truncate($"Add GitHub Actions workflow for Azure pipeline #{proposal.PipelineId} ({Plain(proposal.Evidence.PipelineName, 60)}){(triggers ? " with automatic triggers" : "")}", 120);
        var commit = update
            ? $"ci: add automatic triggers to the GitHub Actions workflow of Azure pipeline {proposal.PipelineId}\n\nReplaces the workflow approved earlier (SHA-256 {priorWorkflowSha256}) with the version that has the Azure triggers converted by fixed rules, approved by a person. Workflow SHA-256: {approved.WorkflowSha256}"
            : triggers
                ? $"ci: add Azure pipeline {proposal.PipelineId} as a GitHub Actions workflow with automatic triggers\n\nConverted by an AI model, triggers mapped from Azure by fixed rules, approved by a person. Workflow SHA-256: {approved.WorkflowSha256}"
                : $"ci: add Azure pipeline {proposal.PipelineId} as a manual GitHub Actions workflow\n\nProposed by an AI model, approved by a person. Workflow SHA-256: {approved.WorkflowSha256}";
        var body = Body(approved, proposal, approval, path, update ? priorWorkflowSha256 : null);
        var ready = findings.All(f => f.Severity != "Blocker") && target?.WorkflowFile != WorkflowFileState.Identical;
        var steps = Steps(gitUrl, repository, baseBranch, head, path, approved.WorkflowSha256, title, commit, update);
        // a plan that creates a file hashes exactly what it always did; the replacement of a file is part of what the person confirms
        var parts = new List<string> { PlanVersion, proposal.Id.ToString(), approved.WorkflowSha256, $"{organization}/{repository}", baseBranch, target?.BaseSha ?? "", head, path, commit, title, body };
        if (update) parts.AddRange(["update", target!.WorkflowFileBlobSha!, priorWorkflowSha256!]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
        return new(proposal.Id, approved.WorkflowSha256, organization, repository, baseBranch, target?.BaseSha, head, path, commit, title, body, findings, ready, steps, hash, now,
            update ? PublicationModes.Update : PublicationModes.Create, update ? target!.WorkflowFileBlobSha : null, update ? priorWorkflowSha256 : null);
    }

    private static List<PublicationFinding> Findings(PublicationTarget? t, bool unreadable, bool replace, string? priorSha256)
    {
        var list = new List<PublicationFinding>();
        if (unreadable || t is null)
        {
            list.Add(new("TargetUnreadable", "Blocker", "The destination repository could not be read: nothing below was checked against it."));
            return list;
        }
        if (!t.RepositoryExists)
        {
            list.Add(new("RepositoryNotFound", "Blocker", "The destination repository does not exist, or the destination credential cannot see it."));
            return list;
        }
        if (t.Archived) list.Add(new("RepositoryArchived", "Blocker", "The destination repository is archived and read-only."));
        if (t.BaseSha is null) { list.Add(new("BaseBranchNotFound", "Blocker", "The base branch does not exist in the destination repository.")); return list; }
        switch (t.WorkflowFile)
        {
            case WorkflowFileState.Different:
                list.Add(new("WorkflowPathOccupied", "Blocker", "A different file already exists at the workflow path on the base branch; this plan would overwrite it."));
                break;
            case WorkflowFileState.PriorVersion when replace:
                list.Add(new("WorkflowWillBeReplaced", "Info", $"The base branch has exactly the workflow approved earlier (SHA-256 {priorSha256}). This plan replaces that file with the approved version, on a new branch: the base branch changes only when a person merges the pull request."));
                break;
            case WorkflowFileState.PriorVersion:
                // the reader found a text nobody named as replaceable, or could not say which blob it is: that is somebody's file
                list.Add(new("WorkflowPathOccupied", "Blocker", "A different file already exists at the workflow path on the base branch; this plan would overwrite it."));
                break;
            case WorkflowFileState.Identical:
                list.Add(new("AlreadyPublished", "Info", "The base branch already contains exactly this workflow: there is nothing to publish."));
                break;
        }
        if (t.HeadBranchExists) list.Add(new("HeadBranchExists", "Blocker", "The branch this plan would create already exists in the destination repository."));
        if (t.OpenPullRequests.Count > 0)
            list.Add(new("OpenPullRequestExists", "Blocker", $"An open pull request already uses this branch (#{string.Join(", #", t.OpenPullRequests.Take(5))})."));
        return list;
    }

    private static string Body(ApprovedWorkflow approved, WorkflowProposal proposal, ProposalEvent? approval, string path, string? replacesSha256)
    {
        var evidence = proposal.Evidence;
        var p = evidence.Provenance;
        var manual = ManualTasks(approved.WorkflowYaml);
        var sb = new StringBuilder();
        sb.AppendLine("## Azure Pipelines → GitHub Actions (proposal)");
        sb.AppendLine();
        sb.AppendLine($"Converted from the Azure pipeline `{Plain(evidence.PipelineName, 80)}` (#{proposal.PipelineId}, project `{Plain(proposal.ProjectId, 60)}`) by an AI model, then approved by a person.");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Workflow | `{path}` |");
        sb.AppendLine($"| Workflow SHA-256 | `{approved.WorkflowSha256}` |");
        if (replacesSha256 is not null) sb.AppendLine($"| Replaces | the workflow at this path approved earlier, SHA-256 `{replacesSha256}` |");
        if (approval is not null) sb.AppendLine($"| Approved by | `{Plain(approval.Actor, 60)}` on {approval.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC |");
        sb.AppendLine($"| Proposed by | `{Plain(proposal.CreatedBy, 60)}` on {proposal.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC |");
        if (p is not null) sb.AppendLine($"| Model | `{Plain(p.Model, 60)}` via `{Plain(p.Host, 80)}` |");
        if (p is not null) sb.AppendLine($"| Checked with | gate policy {Plain(p.PolicyVersion, 20)}, action pins verified {Plain(p.ActionCatalogVerifiedOn, 12)}, actionlint and zizmor |");
        sb.AppendLine();
        sb.AppendLine("### What this is not");
        sb.AppendLine("- It is **not** proven equivalent to the Azure pipeline. Read it line by line.");
        if (proposal.Kind == ProposalKinds.Triggers)
        {
            if (replacesSha256 is not null)
                sb.AppendLine("- It **replaces** the file already at this path with the version that has the triggers. The file was found unchanged since it was approved (same SHA-256 as above); the pull request shows exactly what differs.");
            sb.AppendLine("- It **starts by itself** on the triggers below. They were converted from the Azure pipeline by fixed rules (no model), and the two systems differ in ways a person accepted at approval.");
            sb.AppendLine();
            sb.AppendLine("### Automatic triggers");
            foreach (var line in proposal.Evidence.Triggers?.Mapped ?? []) sb.AppendLine($"- `{Plain(line, 300)}`");
            var acceptedCodes = approval?.Detail?.AcknowledgedWarnings ?? [];
            var differences = (proposal.Evidence.Triggers?.Differences ?? []).Where(d => acceptedCodes.Contains(d.Code)).ToArray();
            if (differences.Length > 0)
            {
                sb.AppendLine("- Differences from Azure, accepted by a person:");
                foreach (var d in differences) sb.AppendLine($"  - `{Plain(d.Code, 60)}`: {d.Message}");
            }
        }
        else
        {
            sb.AppendLine("- It runs only when started by hand (`workflow_dispatch`). The original Azure triggers were **not** converted.");
        }
        sb.AppendLine();
        sb.AppendLine("### Needs a person");
        sb.AppendLine(manual.Count == 0 ? "- No task is marked as a manual port." : "- Tasks that must be ported by hand (`MANUAL PORT REQUIRED` in the file): " + string.Join(", ", manual.Select(m => $"`{Plain(m, 60)}`")) + ".");
        var accepted = approval?.Detail?.AcknowledgedWarnings ?? [];
        sb.AppendLine(accepted.Count == 0 ? "- No warning was accepted at approval." : "- Warnings accepted at approval: " + string.Join(", ", accepted.Select(w => $"`{Plain(w, 60)}`")) + ".");
        sb.AppendLine("- Secrets and variables the workflow may use. Create them in this repository first; **names only, no value was copied**:");
        sb.AppendLine("  - Secrets: " + (evidence.Plan.Secrets.Count == 0 ? "none" : string.Join(", ", evidence.Plan.Secrets.Select(s => $"`{Plain(s.Name, 100)}`"))));
        sb.AppendLine("  - Variables: " + (evidence.Plan.Variables.Count == 0 ? "none" : string.Join(", ", evidence.Plan.Variables.Select(v => $"`{Plain(v.Name, 100)}`"))));
        if (!string.IsNullOrWhiteSpace(proposal.ModelNotes))
        {
            var notes = Clean(proposal.ModelNotes!, 4000);
            var fence = new string('`', Math.Max(3, LongestBacktickRun(notes) + 1));
            sb.AppendLine();
            sb.AppendLine("### Notes written by the model (unverified)");
            sb.AppendLine(fence + "text");
            sb.AppendLine(notes);
            sb.AppendLine(fence);
        }
        return sb.ToString();
    }

    private static List<PublicationStep> Steps(string gitUrl, string repository, string baseBranch, string head, string path, string sha, string title, string commit, bool replace)
    {
        var list = new List<PublicationStep>
        {
            new("Clone", "Clone the destination repository (or use an existing clone)", $"git clone {Sh(gitUrl)} && cd {Sh(repository)}"),
            new("Branch", "Start a branch from the base branch", $"git fetch origin && git checkout -b {Sh(head)} {Sh("origin/" + baseBranch)}"),
            replace
                ? new("Replace", "Replace the workflow text with the text shown (the file already exists on the base branch)", $"cat > {Sh(path)}   # paste the workflow, end with Ctrl-D")
                : new("Save", "Save the workflow text exactly as shown", $"mkdir -p .github/workflows && cat > {Sh(path)}   # paste the workflow, end with Ctrl-D"),
            new("Verify", "Check that it is the approved text", $"sha256sum {Sh(path)}   # must print {sha}"),
            new("Commit", "Commit", $"git add {Sh(path)} && git commit {string.Join(' ', commit.Split("\n\n", 2).Select(part => "-m " + Sh(part)))}"),
            new("Push", "Push the branch (the credential needs permission to write workflow files: the `workflow` scope for a token)", $"git push -u origin {Sh(head)}"),
            new("OpenPullRequest", "Open the pull request with the text above as its description", $"gh pr create --base {Sh(baseBranch)} --head {Sh(head)} --title {Sh(title)} --body-file pr-body.md")
        };
        return list;
    }

    private static IReadOnlyList<string> ManualTasks(string yaml)
    {
        try
        {
            var found = ManualHeader().Match(yaml);
            return found.Success ? found.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(i => !i.Equals("none", StringComparison.OrdinalIgnoreCase)).Take(50).ToArray() : [];
        }
        catch (RegexMatchTimeoutException) { return []; }
    }

    // Untrusted text inside a code span: no backticks, no line breaks, no control characters, bounded.
    private static string Plain(string? value, int max)
    {
        var cleaned = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Replace('`', '\'').Trim();
        return cleaned.Length == 0 ? "?" : Truncate(cleaned, max);
    }

    private static string Clean(string value, int max) => Truncate(new string(value.Replace("\r\n", "\n").Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim(), max);
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
    private static string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text) { run = c == '`' ? run + 1 : 0; if (run > longest) longest = run; }
        return longest;
    }
}

// Plans the publication of an approved workflow against a destination repository. Reads only; it never writes to the destination
// and never records anything: a plan is cheap to produce again and its hash says whether it is still the same plan.
public sealed class WorkflowPublicationService(WorkflowProposalService proposals, IConnectionRepository connections, IConnectionSecretStore secrets,
    IGitHubPublicationReader reader, TimeProvider clock, IPublicationLedger? ledger = null, IGitHubPublicationObserver? observer = null)
{
    private static readonly Regex RepoKey = new(@"^(?!\.{1,2}$)[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BranchKey = new(@"^(?!/)(?!.*//)(?!.*\.\.)(?!.*\.lock$)[A-Za-z0-9._/-]{1,200}(?<!/)$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<(PublicationPlan Plan, string WorkflowYaml)> PlanAsync(Guid proposalId, Guid destinationId, string repository, string? baseBranch, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(repository) || !RepoKey.IsMatch(repository)) throw new WorkloadInputException("InvalidRepository");
        if (baseBranch is not null && !BranchKey.IsMatch(baseBranch)) throw new WorkloadInputException("InvalidBranch");

        // the approval is checked first: nothing about the destination is read for a proposal nobody approved
        var (approved, proposal, events) = await proposals.RequireApprovedWithRecordAsync(proposalId, ct);

        var destination = await connections.GetAsync(destinationId, ct) ?? throw new WorkloadInputException("DestinationNotFound");
        if (destination.Direction != ConnectionDirection.Destination || !ProviderKeys.IsGitHubDestination(destination.ProviderKey) || destination.ValidationCode != "Verified")
            throw new WorkloadInputException("UnsupportedDestination");
        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrEmpty(token)) throw new WorkloadInputException("DestinationCredentialMissing");

        var path = WorkflowPublicationPlanner.WorkflowPathFor(proposal.PipelineId, proposal.Evidence.PipelineName);
        var head = WorkflowPublicationPlanner.HeadBranchFor(proposal.PipelineId, approved.WorkflowSha256);
        var prior = await ReplaceableHashAsync(proposal, ct);
        PublicationTarget? target = null;
        var unreadable = false;
        try { target = await reader.ReadAsync(destination.Organization, repository, baseBranch, head, path, approved.WorkflowSha256, prior, token, ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { unreadable = true; }

        var earlier = target is { RepositoryExists: true, BaseSha: not null, WorkflowFile: WorkflowFileState.Absent or WorkflowFileState.PriorVersion }
            ? await EarlierPullRequestsAsync(proposal, destination.Organization, repository, target.BaseBranch ?? baseBranch ?? "", head, path, token, ct) : [];

        var plan = WorkflowPublicationPlanner.Build(approved, proposal, events, destination.Organization, repository, baseBranch ?? "(default branch)", target, unreadable,
            reader.GitUrl(destination.Organization, repository), clock.GetUtcNow(), prior, earlier);
        plan = await WithBlockingAttemptAsync(plan, ct);
        return (plan, approved.WorkflowYaml);
    }

    // The destination can look perfectly free while the ledger still keeps THIS exact plan (same proposal, same hash) blocked: an attempt that failed after a write may have happened, one that never
    // finished, or one published whose pull request was closed unmerged and whose branch is gone. Publishing would be refused at the last moment, so the plan says it first. It is added only
    // when nothing else stands in the way: a branch or an open pull request already explains the block, and releasing the attempt would be refused. It changes whether the plan is ready,
    // never its hash: what a person confirms is the same plan.
    private async Task<PublicationPlan> WithBlockingAttemptAsync(PublicationPlan plan, CancellationToken ct)
    {
        if (ledger is null || !plan.Ready) return plan;
        var attempt = (await ledger.ListAsync(plan.ProposalId, ct)).Where(a => a.PlanSha256 == plan.PlanSha256 && a.BlocksItsPlan).OrderByDescending(a => a.StartedAtUtc).FirstOrDefault();
        if (attempt is null) return plan;

        var message = attempt.State switch
        {
            PublicationStates.Published =>
                $"An earlier attempt published this exact plan as pull request #{attempt.PullNumber}. That pull request was closed without merging and its branch is gone, but the ledger still counts the plan as published. A superadmin can release the attempt (the product reads the destination again first), or the base branch can move, which makes a different plan.",
            PublicationStates.Failed =>
                $"An earlier attempt of this exact plan failed ({attempt.ErrorCode}) after something may have been written. The destination shows nothing of it now, but the ledger keeps this exact plan blocked. A superadmin can release the attempt (the product reads the destination again first), or the base branch can move, which makes a different plan.",
            _ when clock.GetUtcNow() - attempt.UpdatedAtUtc < PublicationReleaseService.StaleAfter =>
                $"An earlier attempt of this exact plan is still running (last progress {attempt.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC). Wait for it to finish; it is never run twice at the same time.",
            _ => $"An earlier attempt of this exact plan never finished (last progress {attempt.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC) and the ledger keeps this exact plan blocked. A superadmin can release the attempt (the product reads the destination again first), or the base branch can move, which makes a different plan."
        };
        return plan with { Findings = [.. plan.Findings, new("AttemptBlocksPlan", "Blocker", message)], Ready = false };
    }

    // The same file added (or replaced) on another branch while an earlier pull request for it is still open would make the two conflict the moment either is merged. The plan cannot see
    // that from the destination alone (the file is not there yet), so it asks the ledger which pull requests THIS PRODUCT opened for this file in this repository and reads their state.
    // Another branch of this plan is already reported by HeadBranchExists/OpenPullRequestExists; a pull request that cannot be read is said so, not assumed closed.
    private async Task<IReadOnlyList<PublicationFinding>> EarlierPullRequestsAsync(WorkflowProposal proposal, string organization, string repository, string baseBranch, string head,
        string path, string token, CancellationToken ct)
    {
        if (ledger is null || observer is null) return [];
        var earlier = (await ledger.ListForPathAsync(organization, repository, path, ct))
            .Where(a => a.PullNumber is > 0 && a.State is PublicationStates.Published or PublicationStates.PullRequestOpened && a.HeadBranch != head &&
                string.Equals(a.Organization, organization, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Repository, repository, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.BaseBranch, baseBranch, StringComparison.Ordinal))
            .GroupBy(a => a.PullNumber!.Value).Select(g => g.OrderByDescending(a => a.StartedAtUtc).First()).Take(3).ToArray();

        var findings = new List<PublicationFinding>();
        foreach (var attempt in earlier)
        {
            var number = attempt.PullNumber!.Value;
            var whose = attempt.ProposalId == proposal.ParentProposalId ? "The conversion this proposal was derived from"
                : attempt.ProposalId == proposal.Id ? "An earlier publication of this proposal" : "An earlier publication of this workflow file";
            try
            {
                var pull = await observer.ReadPullAsync(organization, repository, number, attempt.HeadBranch, token, ct);
                if (pull is { State: "open" })
                    findings.Add(new("EarlierPullRequestOpen", "Blocker",
                        $"{whose} is still open as pull request #{number} in this repository (not merged): the same file would be added on another branch and the two pull requests would conflict. Merge or close #{number} first, then plan again."));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                findings.Add(new("EarlierPullRequestUnchecked", "Warning", $"{whose} was published as pull request #{number}; it could not be read just now, so whether it is still open is not known."));
            }
        }
        return findings;
    }

    // The one text a derived (Triggers) proposal may replace at the destination: the approved text of the conversion it was derived from, and only while that
    // approval stands, for the same pipeline. A revoked, missing or foreign parent names nothing, so a file at the path is then somebody else's.
    private async Task<string?> ReplaceableHashAsync(WorkflowProposal proposal, CancellationToken ct)
    {
        if (proposal.Kind != ProposalKinds.Triggers || proposal.ParentProposalId is not { } parentId) return null;
        try
        {
            var (workflow, parent, _) = await proposals.RequireApprovedWithRecordAsync(parentId, ct);
            return parent.Kind == ProposalKinds.Conversion && parent.SourceConnectionId == proposal.SourceConnectionId && parent.ProjectId == proposal.ProjectId &&
                parent.PipelineId == proposal.PipelineId ? workflow.WorkflowSha256 : null;
        }
        catch (WorkflowProposalException) { return null; }
    }
}
