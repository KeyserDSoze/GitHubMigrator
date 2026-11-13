using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// What the destination repository looks like right now, as far as publishing one workflow file on a new branch is concerned.
// Read only. The workflow text is compared inside the reader and never returned.
public enum WorkflowFileState { Absent, Identical, Different }

public sealed record PublicationTarget(bool RepositoryExists, bool Archived, string? DefaultBranch, string? BaseBranch, string? BaseSha,
    WorkflowFileState WorkflowFile, bool HeadBranchExists, string? HeadBranchSha, IReadOnlyList<int> OpenPullRequests);

public interface IGitHubPublicationReader
{
    // `baseBranch` null means the repository's default branch. Only GET requests, only the one repository.
    Task<PublicationTarget> ReadAsync(string organization, string repository, string? baseBranch, string headBranch, string workflowPath,
        string workflowSha256, string token, CancellationToken ct);
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
    IReadOnlyList<PublicationFinding> Findings, bool Ready, IReadOnlyList<PublicationStep> Steps, string PlanSha256, DateTimeOffset ReadAtUtc);

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
        string organization, string repository, string requestedBase, PublicationTarget? target, bool targetUnreadable, string gitUrl, DateTimeOffset now)
    {
        var path = WorkflowPathFor(proposal.PipelineId, proposal.Evidence.PipelineName);
        var head = HeadBranchFor(proposal.PipelineId, approved.WorkflowSha256);
        var baseBranch = target?.BaseBranch ?? requestedBase;
        var findings = Findings(target, targetUnreadable);
        var approval = events.LastOrDefault(e => e.Type == "Approved");
        var title = Truncate($"Add GitHub Actions workflow for Azure pipeline #{proposal.PipelineId} ({Plain(proposal.Evidence.PipelineName, 60)})", 120);
        var commit = $"ci: add Azure pipeline {proposal.PipelineId} as a manual GitHub Actions workflow\n\nProposed by an AI model, approved by a person. Workflow SHA-256: {approved.WorkflowSha256}";
        var body = Body(approved, proposal, approval, path);
        var ready = findings.All(f => f.Severity != "Blocker") && target?.WorkflowFile != WorkflowFileState.Identical;
        var steps = Steps(gitUrl, repository, baseBranch, head, path, approved.WorkflowSha256, title, commit);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            PlanVersion, proposal.Id, approved.WorkflowSha256, $"{organization}/{repository}", baseBranch, target?.BaseSha ?? "", head, path, commit, title, body))));
        return new(proposal.Id, approved.WorkflowSha256, organization, repository, baseBranch, target?.BaseSha, head, path, commit, title, body, findings, ready, steps, hash, now);
    }

    private static List<PublicationFinding> Findings(PublicationTarget? t, bool unreadable)
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
            case WorkflowFileState.Identical:
                list.Add(new("AlreadyPublished", "Info", "The base branch already contains exactly this workflow: there is nothing to publish."));
                break;
        }
        if (t.HeadBranchExists) list.Add(new("HeadBranchExists", "Blocker", "The branch this plan would create already exists in the destination repository."));
        if (t.OpenPullRequests.Count > 0)
            list.Add(new("OpenPullRequestExists", "Blocker", $"An open pull request already uses this branch (#{string.Join(", #", t.OpenPullRequests.Take(5))})."));
        return list;
    }

    private static string Body(ApprovedWorkflow approved, WorkflowProposal proposal, ProposalEvent? approval, string path)
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
        if (approval is not null) sb.AppendLine($"| Approved by | `{Plain(approval.Actor, 60)}` on {approval.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC |");
        sb.AppendLine($"| Proposed by | `{Plain(proposal.CreatedBy, 60)}` on {proposal.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC |");
        if (p is not null) sb.AppendLine($"| Model | `{Plain(p.Model, 60)}` via `{Plain(p.Host, 80)}` |");
        if (p is not null) sb.AppendLine($"| Checked with | gate policy {Plain(p.PolicyVersion, 20)}, action pins verified {Plain(p.ActionCatalogVerifiedOn, 12)}, actionlint and zizmor |");
        sb.AppendLine();
        sb.AppendLine("### What this is not");
        sb.AppendLine("- It is **not** proven equivalent to the Azure pipeline. Read it line by line.");
        sb.AppendLine("- It runs only when started by hand (`workflow_dispatch`). The original Azure triggers were **not** converted.");
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

    private static List<PublicationStep> Steps(string gitUrl, string repository, string baseBranch, string head, string path, string sha, string title, string commit)
    {
        var list = new List<PublicationStep>
        {
            new("Clone", "Clone the destination repository (or use an existing clone)", $"git clone {Sh(gitUrl)} && cd {Sh(repository)}"),
            new("Branch", "Start a branch from the base branch", $"git fetch origin && git checkout -b {Sh(head)} {Sh("origin/" + baseBranch)}"),
            new("Save", "Save the workflow text exactly as shown", $"mkdir -p .github/workflows && cat > {Sh(path)}   # paste the workflow, end with Ctrl-D"),
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
    IGitHubPublicationReader reader, TimeProvider clock)
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
        PublicationTarget? target = null;
        var unreadable = false;
        try { target = await reader.ReadAsync(destination.Organization, repository, baseBranch, head, path, approved.WorkflowSha256, token, ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { unreadable = true; }

        var plan = WorkflowPublicationPlanner.Build(approved, proposal, events, destination.Organization, repository, baseBranch ?? "(default branch)", target, unreadable,
            reader.GitUrl(destination.Organization, repository), clock.GetUtcNow());
        return (plan, approved.WorkflowYaml);
    }
}
