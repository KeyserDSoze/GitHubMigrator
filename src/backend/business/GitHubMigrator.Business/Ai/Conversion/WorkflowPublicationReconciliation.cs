using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// What the destination shows now about one publication attempt, read with GET requests only. File texts are compared inside the reader (against the approved
// hash and, for an update, against the text that was replaced) and never returned: only the classification is.
public sealed record PullObservation(int Number, string State, bool Merged, string? HeadSha, string? BaseRef, string? MergeCommitSha);

public sealed record PublicationObservation(bool RepositoryExists, bool Archived, string? BaseTipSha, string? HeadBranchSha,
    IReadOnlyList<PullObservation> Pulls, WorkflowFileState FileOnHead, WorkflowFileState FileOnBase);

public interface IGitHubPublicationObserver
{
    // Only GET requests, only the one repository. `pullNumber` is the number the ledger recorded (it is read directly when the list for the branch does not show it);
    // `replacedWorkflowSha256` is the hash of the text an update replaced, so that text can be told from "somebody else's file".
    Task<PublicationObservation> ObserveAsync(string organization, string repository, string baseBranch, string headBranch, string workflowPath,
        string workflowSha256, string? replacedWorkflowSha256, int? pullNumber, string token, CancellationToken ct);

    // One pull request by number, read with a GET: null when it does not exist or is not a pull request from `headBranch` (a number that belongs to something else is not the attempt's).
    Task<PullObservation?> ReadPullAsync(string organization, string repository, int number, string headBranch, string token, CancellationToken ct);
}

public static class ReconciliationVerdicts
{
    public const string TargetUnreadable = "TargetUnreadable";
    public const string RepositoryNotFound = "RepositoryNotFound";
    public const string Merged = "Merged";
    public const string MergedBaseDiffers = "MergedBaseDiffers";
    public const string OpenPullRequest = "OpenPullRequest";
    public const string OpenPullRequestChanged = "OpenPullRequestChanged";
    public const string ClosedWithoutMerge = "ClosedWithoutMerge";
    public const string BranchWithoutPullRequest = "BranchWithoutPullRequest";
    public const string AlreadyOnBase = "AlreadyOnBase";
    public const string NothingFound = "NothingFound";
}

// One attempt of the ledger set beside what is at the destination. A view, not an action: nothing is written, no state is changed, and the product does not
// clean up or retry on the strength of it. It says what a person would find.
public sealed record PublicationReconciliation(Guid PublicationId, Guid ProposalId, string Verdict, IReadOnlyList<PublicationFinding> Findings,
    PublicationObservation? Observed, string LedgerState, bool LedgerWritesPossible, string? LedgerCommitSha, int? LedgerPullNumber, DateTimeOffset ReadAtUtc,
    bool LedgerReleased = false, bool LedgerBlocksPlan = false);

public static class PublicationReconciler
{
    public static PublicationReconciliation Assess(PublicationRecord record, PublicationObservation? observed, bool unreadable, DateTimeOffset now)
    {
        var findings = new List<PublicationFinding>();
        void Add(string code, string severity, string message) => findings.Add(new(code, severity, message));

        string verdict;
        if (unreadable || observed is null)
        {
            verdict = ReconciliationVerdicts.TargetUnreadable;
            Add("TargetUnreadable", "Warning", "The destination could not be read just now: nothing here says what exists.");
        }
        else if (!observed.RepositoryExists)
        {
            verdict = ReconciliationVerdicts.RepositoryNotFound;
            Add("RepositoryNotFound", "Warning", "The repository does not exist, or the destination credential cannot see it: nothing recorded in the ledger can be checked.");
        }
        else
        {
            var o = observed;
            if (o.Archived) Add("RepositoryArchived", "Info", "The repository is archived and read-only.");
            if (o.BaseTipSha is null) Add("BaseBranchNotFound", "Warning", "The base branch recorded for this attempt no longer exists: the file on the base branch could not be checked.");
            else if (!string.Equals(o.BaseTipSha, record.BaseSha, StringComparison.Ordinal)) Add("BaseMoved", "Info", "The base branch has moved since this attempt was planned (this is expected once something was merged).");

            // the pull request the ledger recorded, or else the only one that uses the branch
            var pull = record.PullNumber is { } recorded ? o.Pulls.FirstOrDefault(p => p.Number == recorded) : o.Pulls.Count == 1 ? o.Pulls[0] : null;
            if (record.PullNumber is { } expected && pull is null)
                Add("PullRequestMissing", "Warning", $"The ledger records pull request #{expected}, but the destination does not show one with that number from this attempt's branch.");
            if (record.PullNumber is null && o.Pulls.Count > 1)
            {
                Add("MultiplePullRequests", "Warning", $"More than one pull request uses this branch (#{string.Join(", #", o.Pulls.Select(p => p.Number).Take(5))}) and the ledger did not record which one is this attempt's.");
                pull = o.Pulls[0];
            }
            else if (record.PullNumber is not null && o.Pulls.Any(p => p.Number != record.PullNumber))
                Add("OtherPullRequests", "Info", $"Other pull requests also use this branch (#{string.Join(", #", o.Pulls.Where(p => p.Number != record.PullNumber).Select(p => p.Number).Take(5))}).");

            var moved = record.CommitSha is not null && o.HeadBranchSha is not null && !string.Equals(o.HeadBranchSha, record.CommitSha, StringComparison.Ordinal);
            if (moved) Add("BranchMoved", "Warning", "The branch is not at the commit the product wrote: somebody has pushed to it since.");

            if (pull is not null)
            {
                if (pull.Merged)
                {
                    verdict = o.FileOnBase == WorkflowFileState.Identical ? ReconciliationVerdicts.Merged : ReconciliationVerdicts.MergedBaseDiffers;
                    if (verdict == ReconciliationVerdicts.MergedBaseDiffers)
                        Add("MergedBaseDiffers", "Warning", o.FileOnBase == WorkflowFileState.Absent
                            ? "The pull request was merged, but the base branch has no file at the workflow path now (it was removed after, or the merge did not bring it)."
                            : "The pull request was merged, but the file at the workflow path on the base branch is not the approved text (it was changed after, or the merge did not bring it).");
                }
                else if (pull.State == "open")
                {
                    var intact = !moved && o.FileOnHead == WorkflowFileState.Identical && (record.CommitSha is null || pull.HeadSha is null || pull.HeadSha == record.CommitSha);
                    verdict = intact ? ReconciliationVerdicts.OpenPullRequest : ReconciliationVerdicts.OpenPullRequestChanged;
                    if (!intact)
                        Add("OpenPullRequestChanged", "Warning", o.FileOnHead == WorkflowFileState.Identical
                            ? "The pull request is open, but its head is not the commit the product wrote."
                            : "The pull request is open, but the file on its branch is not the approved text: do not merge it believing it is.");
                }
                else
                {
                    verdict = ReconciliationVerdicts.ClosedWithoutMerge;
                    Add("ClosedWithoutMerge", "Info", "The pull request was closed without being merged: nothing from this attempt reached the base branch. The branch may still exist.");
                }
            }
            else if (o.HeadBranchSha is not null)
            {
                verdict = ReconciliationVerdicts.BranchWithoutPullRequest;
                Add("BranchWithoutPullRequest", "Warning", o.FileOnHead switch
                {
                    WorkflowFileState.Identical => "The branch exists and holds the approved text, but no pull request uses it.",
                    WorkflowFileState.Absent => "The branch exists but has no workflow file at the path, and no pull request uses it.",
                    _ => "The branch exists and its file at the workflow path is not the approved text, and no pull request uses it."
                });
            }
            else if (o.FileOnBase == WorkflowFileState.Identical)
            {
                verdict = ReconciliationVerdicts.AlreadyOnBase;
                Add("AlreadyOnBase", "Info", "The base branch already has the approved text, and nothing from this attempt (branch or pull request) remains.");
            }
            else
            {
                verdict = ReconciliationVerdicts.NothingFound;
                Add("NothingFound", "Info", "Neither a branch nor a pull request from this attempt exists at the destination.");
            }

            // what the ledger says, against what was found
            if (record.State == PublicationStates.Published && pull is null && record.PullNumber is null)
                Add("PullRequestMissing", "Warning", "The ledger says this attempt was published, but no pull request from its branch is shown.");
            if (record.State is not (PublicationStates.Published or PublicationStates.Failed))
                Add("AttemptNotFinished", "Warning", $"The ledger shows this attempt stopped in the state {record.State}. If the product is not running it, treat it as an attempt that may have written: what exists is above.");
            if (record.State == PublicationStates.Failed && !record.WritesPossible && (o.HeadBranchSha is not null || o.Pulls.Count > 0))
                Add("UnexpectedObjects", "Warning", "The ledger says nothing was written, but a branch or pull request with this attempt's name exists: it was not made by this attempt, or the ledger is wrong.");
            if (record.ReleasedAtUtc is { } releasedAt)
                Add("AttemptReleased", "Info", $"A person released this attempt on {releasedAt:yyyy-MM-dd HH:mm} UTC after the product had checked the destination: the ledger no longer keeps its plan blocked.");
            else if (record.State == PublicationStates.Failed && record.WritesPossible)
            {
                if (verdict is ReconciliationVerdicts.NothingFound or ReconciliationVerdicts.ClosedWithoutMerge && o.HeadBranchSha is null)
                    Add("LedgerStillBlocksPlan", "Info", "The attempt left nothing behind, but the ledger still blocks this exact plan. A superadmin can release it (the product checks the destination again first), or the base branch can move: that makes a different plan.");
                else
                    Add("ProductWillNotRetry", "Info", "The product will not retry this attempt or clean up after it: a person decides what to do with what exists.");
            }
            else if (record.State == PublicationStates.Published && record.BlocksItsPlan && o.HeadBranchSha is null && pull is { Merged: false, State: "closed" })
                Add("LedgerStillBlocksPlan", "Info", "The pull request was closed without merging and its branch is gone, but the ledger still blocks this exact plan. A superadmin can release it (the product checks the destination again first).");
        }
        return new(record.Id, record.ProposalId, verdict, findings, observed, record.State, record.WritesPossible, record.CommitSha, record.PullNumber, now,
            record.ReleasedAtUtc is not null, record.BlocksItsPlan);
    }
}

// Reads the destination for ONE recorded attempt, with the destination connection's read-only credential (never the publishing App's), and sets what it
// finds beside the ledger. The connection must be the one for the organization the attempt wrote to: reading another would only say "not found".
public sealed class PublicationReconciliationService(IPublicationLedger ledger, IConnectionRepository connections, IConnectionSecretStore secrets,
    IGitHubPublicationObserver observer, TimeProvider clock)
{
    public async Task<PublicationReconciliation> ReconcileAsync(Guid proposalId, Guid publicationId, Guid destinationId, CancellationToken ct)
    {
        var record = await ledger.GetAsync(publicationId, ct);
        if (record is null || record.ProposalId != proposalId) throw new WorkflowPublicationException("PublicationNotFound");

        var token = await PublicationDestinationAccess.TokenAsync(connections, secrets, destinationId, record.Organization, ct);

        PublicationObservation? observed = null;
        var unreadable = false;
        try
        {
            observed = await observer.ObserveAsync(record.Organization, record.Repository, record.BaseBranch, record.HeadBranch, record.WorkflowPath, record.WorkflowSha256,
                record.ReplacedWorkflowSha256, record.PullNumber, token, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { unreadable = true; }
        return PublicationReconciler.Assess(record, observed, unreadable, clock.GetUtcNow());
    }
}

// The read-only credential for the organization an attempt wrote to: a verified GitHub destination of that organization with a stored credential, nothing else.
internal static class PublicationDestinationAccess
{
    public static async Task<string> TokenAsync(IConnectionRepository connections, IConnectionSecretStore secrets, Guid destinationId, string organization, CancellationToken ct)
    {
        var destination = await connections.GetAsync(destinationId, ct) ?? throw new WorkloadInputException("DestinationNotFound");
        if (destination.Direction != ConnectionDirection.Destination || !ProviderKeys.IsGitHubDestination(destination.ProviderKey) || destination.ValidationCode != "Verified")
            throw new WorkloadInputException("UnsupportedDestination");
        if (!string.Equals(destination.Organization, organization, StringComparison.OrdinalIgnoreCase)) throw new WorkflowPublicationException("DestinationMismatch");
        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        return string.IsNullOrEmpty(token) ? throw new WorkloadInputException("DestinationCredentialMissing") : token;
    }
}
