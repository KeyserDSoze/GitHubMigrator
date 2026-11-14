using System.Text.RegularExpressions;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// The credential that may write one workflow file and open one pull request, and the repositories it may do it in. Entirely operator
// configuration, off by default: with Enabled false (or anything missing) nothing here can run and the answer says so before anything
// is read. It is a different credential from the read-only destination token and from the F-043 attestation (Contents: write only).
public sealed record WorkflowPublicationOptions(bool Enabled, long AppId, long InstallationId, string PrivateKeySecretReference,
    IReadOnlyList<string> AllowedRepositories)
{
    public static WorkflowPublicationOptions Disabled { get; } = new(false, 0, 0, "", []);

    public bool IsUsable => Enabled && AppId > 0 && InstallationId > 0 && !string.IsNullOrWhiteSpace(PrivateKeySecretReference) && AllowedRepositories.Count > 0;

    public bool Allows(string organization, string repository) =>
        AllowedRepositories.Contains($"{organization}/{repository}", StringComparer.OrdinalIgnoreCase);

    // "org/repo, org/other" from configuration; anything that is not exactly owner/name is dropped, not guessed.
    public static IReadOnlyList<string> ParseRepositories(string? list) => (list ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => Regex.IsMatch(x, @"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

// Everything the writer needs, already decided: it adds nothing of its own and takes nothing from a caller.
// Mode Update replaces ONE existing file whose blob and text are named here (ReplacesBlobSha, ReplacesWorkflowSha256); Create writes a file that must not exist.
public sealed record PublicationExecution(Guid ProposalId, string Organization, string Repository, string BaseBranch, string BaseSha, string HeadBranch,
    string WorkflowPath, string WorkflowYaml, string WorkflowSha256, string CommitMessage, string PullRequestTitle, string PullRequestBody, string PlanSha256,
    string Mode = PublicationModes.Create, string? ReplacesBlobSha = null, string? ReplacesWorkflowSha256 = null);

public sealed record PublicationOutcome(string CommitSha, int PullNumber, string? PullUrl);

public sealed record PublicationCredentialObservation(long InstallationId, long RepositoryId, IReadOnlyList<string> Permissions, DateTimeOffset TokenExpiresAtUtc);

// A refusal of the writer. WritesPossible is true when something may exist at the destination, including when the answer to a write was lost.
public sealed class PublicationWriteException(string code, bool writesPossible) : Exception("Publication step refused")
{
    public string Code { get; } = code;
    public bool WritesPossible { get; } = writesPossible;
}

public interface IPublicationProgress
{
    // Called BEFORE each write with the state about to be attempted, and after it with the state reached, so a crash leaves a trace of what may exist.
    Task RecordAsync(string state, string? commitSha, int? pullNumber, string? pullUrl, CancellationToken ct);
}

public interface IGitHubPublicationWriter
{
    // Mints a one-repository token with exactly contents, pull_requests and workflows write, checks it, and discards it. Writes nothing.
    Task<PublicationCredentialObservation> InspectAsync(long appId, long installationId, string privateKeyPem, string organization, string repository, CancellationToken ct);

    // Creates the branch, commits the one file (a new file, or the replacement of the one approved text the plan names), opens the pull request and reads all of
    // it back. The base branch is never written. Never merges, never deletes, never force-pushes, never retries a write.
    Task<PublicationOutcome> PublishAsync(long appId, long installationId, string privateKeyPem, PublicationExecution plan, IPublicationProgress progress, CancellationToken ct);
}

public static class PublicationStates
{
    public const string Started = "Started";
    public const string BranchCreating = "BranchCreating";
    public const string BranchCreated = "BranchCreated";
    public const string FileCommitting = "FileCommitting";
    public const string FileCommitted = "FileCommitted";
    public const string PullRequestOpening = "PullRequestOpening";
    public const string PullRequestOpened = "PullRequestOpened";
    public const string Published = "Published";
    public const string Failed = "Failed";

    // The only order a publication moves in, one step at a time: the state is written before a write is attempted and again after it, so a step
    // cannot be skipped. Failed is reachable from anywhere before Published; nothing leaves Published or Failed.
    public static readonly IReadOnlyList<string> Chain = [Started, BranchCreating, BranchCreated, FileCommitting, FileCommitted, PullRequestOpening, PullRequestOpened, Published];

    public static bool MayFollow(string from, string to)
    {
        if (to == Failed) return from != Published && from != Failed;
        var index = ((List<string>)Chain.ToList()).IndexOf(from);
        return index >= 0 && index + 1 < Chain.Count && Chain[index + 1] == to;
    }

    // Once the first write was attempted, something may exist at the destination.
    public static bool WritesMayHaveStarted(string state) => state is not Started;

    // Once the branch is recorded as created, something exists at the destination whatever a later refusal says.
    public static bool WritesCertain(string state) => Chain.ToList().IndexOf(state) >= Chain.ToList().IndexOf(BranchCreated);
}

public sealed record PublicationRecord(Guid Id, Guid ProposalId, string PlanSha256, string WorkflowSha256, string Organization, string Repository,
    string BaseBranch, string BaseSha, string HeadBranch, string WorkflowPath, string State, string? CommitSha, int? PullNumber, string? PullUrl,
    string? ErrorCode, string? FailedAtState, bool WritesPossible, Guid RequestedById, string RequestedBy, DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc,
    string Mode = PublicationModes.Create, string? ReplacedBlobSha = null, string? ReplacedWorkflowSha256 = null,
    DateTimeOffset? ReleasedAtUtc = null, Guid? ReleasedById = null, string? ReleasedBy = null, string? ReleaseNote = null)
{
    // The ledger's own definition of "live": everything except an attempt that failed before it wrote anything and one a person released after the product had verified, at the
    // destination, that nothing of it remains. A live attempt keeps its exact plan blocked.
    public bool BlocksItsPlan => ReleasedAtUtc is null && !(State == PublicationStates.Failed && !WritesPossible);
}

public interface IPublicationLedger
{
    // Null when a publication of the same plan is already live (running, published or failed after a write): the unique index is the lock.
    Task<PublicationRecord?> TryStartAsync(PublicationRecord started, CancellationToken ct);
    // Compare-and-swap on the state: false when the record is not in `from`.
    Task<bool> AdvanceAsync(Guid id, string from, string to, string? commitSha, int? pullNumber, string? pullUrl, DateTimeOffset at, CancellationToken ct);
    Task FailAsync(Guid id, string errorCode, bool writesPossible, DateTimeOffset at, CancellationToken ct);
    Task<PublicationRecord?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<PublicationRecord>> ListAsync(Guid proposalId, CancellationToken ct);
    // Every attempt, of any proposal, that wrote (or tried to write) this workflow file in this repository: what a plan has to know about before it adds the same file again.
    Task<IReadOnlyList<PublicationRecord>> ListForPathAsync(string organization, string repository, string workflowPath, CancellationToken ct);
    // An attempt that never reached a final state and has not moved since `staleBefore` (that instant included) is abandoned: it becomes Failed (PublicationAbandoned, writes possible once a write was attempted).
    // Compare-and-swap: false when it is final, or was updated after `staleBefore`.
    Task<bool> AbandonAsync(Guid id, DateTimeOffset staleBefore, DateTimeOffset at, CancellationToken ct);
    // Marks a Failed or Published attempt as released, once, with who, why and what the destination showed. Compare-and-swap: false when it is not final or was already released.
    Task<bool> ReleaseAsync(Guid id, Guid byId, string by, string note, string evidenceJson, DateTimeOffset at, CancellationToken ct);
}

public sealed class WorkflowPublicationException(string code, IReadOnlyList<string>? details = null, PublicationRecord? record = null) : Exception("Workflow publication refused")
{
    public string Code { get; } = code;
    public IReadOnlyList<string> Details { get; } = details ?? [];
    public PublicationRecord? Record { get; } = record;
}

// Publishes ONE approved, planned workflow as a branch and a pull request, if and only if every guard holds. The guards, in order: the
// operator enabled it and allow-listed the repository; the person confirms the hash of the plan they saw; that plan is made again now and is
// still the same, with nothing in the way; the attempt is written to a ledger before any write; the writer re-checks the base commit with its own
// credential. Nothing is retried and nothing is merged: a write whose answer was lost leaves a ledger entry that says so.
public sealed class WorkflowPublicationExecutor(WorkflowPublicationService planner, WorkflowProposalService proposals, IConnectionRepository connections,
    IConnectionSecretStore secrets, IPublicationLedger ledger, IGitHubPublicationWriter writer, WorkflowPublicationOptions options, TimeProvider clock)
{
    private static readonly Regex Sha = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool Enabled => options.IsUsable;
    public IReadOnlyList<string> AllowedRepositories => options.AllowedRepositories;

    public async Task<PublicationRecord> PublishAsync(ProposalActor actor, Guid proposalId, Guid destinationId, string repository, string? baseBranch,
        string? confirmedPlanSha256, CancellationToken ct)
    {
        if (!options.IsUsable) throw new WorkflowPublicationException("PublicationDisabled");
        if (confirmedPlanSha256 is null || !Sha.IsMatch(confirmedPlanSha256)) throw new WorkflowPublicationException("PlanConfirmationRequired");

        // the plan is made again NOW: approval, destination, repository and branch are all validated by it, and what the person saw must be what is true
        var (plan, yaml) = await planner.PlanAsync(proposalId, destinationId, repository, baseBranch, ct);
        if (!options.Allows(plan.Organization, plan.Repository)) throw new WorkflowPublicationException("PublicationRepositoryNotAllowed");
        if (!string.Equals(plan.PlanSha256, confirmedPlanSha256, StringComparison.Ordinal)) throw new WorkflowPublicationException("PublicationPlanChanged");
        if (!plan.Ready || plan.BaseSha is null)
            throw new WorkflowPublicationException("PublicationPlanNotReady", plan.Findings.Where(f => f.Severity == "Blocker").Select(f => f.Code).Distinct().ToArray());

        var destination = await connections.GetAsync(destinationId, ct) ?? throw new WorkflowPublicationException("PublicationDestinationMissing");
        // the credential that writes is never the credential that reads
        if (string.Equals(destination.SecretReference, options.PrivateKeySecretReference, StringComparison.Ordinal))
            throw new WorkflowPublicationException("PublicationCredentialSeparationFailed");
        var pem = await secrets.ReadAsync(options.PrivateKeySecretReference, ct);
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 32_768) throw new WorkflowPublicationException("PublicationSigningKeyMissing");

        var now = clock.GetUtcNow();
        var started = await ledger.TryStartAsync(new(Guid.NewGuid(), proposalId, plan.PlanSha256, plan.WorkflowSha256, plan.Organization, plan.Repository,
            plan.BaseBranch, plan.BaseSha, plan.HeadBranch, plan.WorkflowPath, PublicationStates.Started, null, null, null, null, null, false,
            actor.Id, actor.Name, now, now, plan.Mode, plan.ReplacesBlobSha, plan.ReplacesWorkflowSha256), ct) ?? throw new WorkflowPublicationException("PublicationAlreadyAttempted");

        var progress = new LedgerProgress(ledger, clock, started.Id);
        try
        {
            var outcome = await writer.PublishAsync(options.AppId, options.InstallationId, pem, new(proposalId, plan.Organization, plan.Repository, plan.BaseBranch,
                plan.BaseSha, plan.HeadBranch, plan.WorkflowPath, yaml, plan.WorkflowSha256, plan.CommitMessage, plan.PullRequestTitle, plan.PullRequestBody,
                plan.PlanSha256, plan.Mode, plan.ReplacesBlobSha, plan.ReplacesWorkflowSha256), progress, ct);
            if (!await ledger.AdvanceAsync(started.Id, PublicationStates.PullRequestOpened, PublicationStates.Published, outcome.CommitSha, outcome.PullNumber, outcome.PullUrl, clock.GetUtcNow(), CancellationToken.None))
                throw new WorkflowPublicationException("PublicationLedgerConflict", record: await ledger.GetAsync(started.Id, CancellationToken.None));
        }
        catch (PublicationWriteException ex)
        {
            // the writer knows whether the answer was definite: a refusal before any write leaves nothing behind and the same plan may be tried again
            await ledger.FailAsync(started.Id, ex.Code, ex.WritesPossible || PublicationStates.WritesCertain(progress.Last), clock.GetUtcNow(), CancellationToken.None);
            throw new WorkflowPublicationException(ex.Code, record: await ledger.GetAsync(started.Id, CancellationToken.None));
        }
        catch (WorkflowPublicationException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // whatever it was, what may exist at the destination is unknown once a write was attempted: say so, and never retry on its own
            var code = ex is OperationCanceledException ? "PublicationCancelled" : "PublicationUnexpectedError";
            await ledger.FailAsync(started.Id, code, PublicationStates.WritesMayHaveStarted(progress.Last), clock.GetUtcNow(), CancellationToken.None);
            throw new WorkflowPublicationException(code, record: await ledger.GetAsync(started.Id, CancellationToken.None));
        }
        return (await ledger.GetAsync(started.Id, ct))!;
    }

    // Mints and checks the credential for one allow-listed repository. Writes nothing; answers whether a publication could start.
    public async Task<PublicationCredentialObservation> CheckCredentialAsync(string organization, string repository, CancellationToken ct)
    {
        if (!options.IsUsable) throw new WorkflowPublicationException("PublicationDisabled");
        if (!options.Allows(organization, repository)) throw new WorkflowPublicationException("PublicationRepositoryNotAllowed");
        var pem = await secrets.ReadAsync(options.PrivateKeySecretReference, ct);
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > 32_768) throw new WorkflowPublicationException("PublicationSigningKeyMissing");
        try { return await writer.InspectAsync(options.AppId, options.InstallationId, pem, organization, repository, ct); }
        catch (PublicationWriteException ex) { throw new WorkflowPublicationException(ex.Code); }
    }

    public Task<IReadOnlyList<PublicationRecord>> ListAsync(Guid proposalId, CancellationToken ct) => ledger.ListAsync(proposalId, ct);

    private sealed class LedgerProgress(IPublicationLedger ledger, TimeProvider clock, Guid id) : IPublicationProgress
    {
        public string Last { get; private set; } = PublicationStates.Started;

        public async Task RecordAsync(string state, string? commitSha, int? pullNumber, string? pullUrl, CancellationToken ct)
        {
            if (!PublicationStates.MayFollow(Last, state)) throw new PublicationWriteException("PublicationLedgerOutOfOrder", PublicationStates.WritesMayHaveStarted(Last));
            // not cancellable once a write may have happened: the trace of what was attempted must reach the ledger
            if (!await ledger.AdvanceAsync(id, Last, state, commitSha, pullNumber, pullUrl, clock.GetUtcNow(), PublicationStates.WritesMayHaveStarted(Last) ? CancellationToken.None : ct))
                throw new PublicationWriteException("PublicationLedgerConflict", PublicationStates.WritesMayHaveStarted(Last));
            Last = state;
        }
    }
}
