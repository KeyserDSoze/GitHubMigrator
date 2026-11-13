using System.Security.Cryptography;
using System.Text;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Business;

// A proposal that was kept, and what a person decided about it. Proposed: the gates let a person review it. Blocked: it was
// kept as evidence only, WITHOUT its text (a blocked answer may be exactly the credential the gates stopped). Approved: a
// person accepted this exact text, identified by its SHA-256, after the gates were run again. Approved is not "published" and
// not "equivalent": it is the only state from which a later step may take the text to a pull request.
public enum ProposalState { Proposed, Blocked, Approved, Rejected, Revoked }

public sealed record ProposalActor(Guid Id, string Name);

public sealed record StoredGate(string Name, string Status, string? Detail);
public sealed record StoredFinding(string Gate, string Code, string Severity, string Message, int? Line);

// Everything that explains a proposal except its text. No source text, no values: the plan holds names only and the
// provenance holds hashes and counts.
public sealed record ProposalEvidence(IReadOnlyList<StoredGate> Gates, IReadOnlyList<StoredFinding> Findings, ConversionPlan Plan,
    IReadOnlyList<string> SourceTasks, ConversionProvenance? Provenance);

public sealed record WorkflowProposal(Guid Id, Guid SourceConnectionId, string ProjectId, int PipelineId, Guid AiConnectionId,
    ProposalState State, string? Reason, string? WorkflowYaml, string? WorkflowSha256, string? ModelNotes, ProposalEvidence Evidence,
    Guid CreatedById, string CreatedBy, DateTimeOffset CreatedAtUtc, string? ApprovedSha256)
{
    // The stored text still hashes to the stored hash. Checked on every read that matters, not only when it was written.
    public bool IntegrityOk => WorkflowYaml is null || (WorkflowSha256 is not null && WorkflowHash.Sha256(WorkflowYaml) == WorkflowSha256);
}

public sealed record ProposalSummary(Guid Id, ProposalState State, string? Reason, string? WorkflowSha256, string CreatedBy,
    DateTimeOffset CreatedAtUtc, Guid AiConnectionId);

public sealed record ProposalEventDetail(string? ReportSha256, string? PolicyVersion, IReadOnlyList<string> AcknowledgedWarnings);

public sealed record ProposalEvent(long Id, Guid ProposalId, string Type, Guid ActorId, string Actor, DateTimeOffset OccurredAtUtc,
    string? WorkflowSha256, string? Note, ProposalEventDetail? Detail);

public sealed record ApprovedWorkflow(Guid ProposalId, string WorkflowYaml, string WorkflowSha256);

public sealed record WorkflowProposalOptions
{
    // The person who asked the model for a workflow is not the only one who may accept it. An operator with a single
    // reviewer can switch it off explicitly; the default is the safe one.
    public bool RequireSecondApprover { get; init; } = true;
}

public sealed class WorkflowProposalException(string code, IReadOnlyList<string>? details = null) : Exception("Workflow proposal rejected")
{
    public string Code { get; } = code;
    // Finding or warning codes that explain the refusal. Never text from the workflow or the model.
    public IReadOnlyList<string> Details { get; } = details ?? [];
}

public interface IWorkflowProposalRepository
{
    Task AddAsync(WorkflowProposal proposal, ProposalEvent created, CancellationToken ct);
    Task<WorkflowProposal?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ProposalSummary>> ListAsync(Guid sourceConnectionId, string projectId, int pipelineId, int limit, CancellationToken ct);
    Task<IReadOnlyList<ProposalEvent>> EventsAsync(Guid id, CancellationToken ct);

    // Moves a proposal from one state to another and appends the event in ONE transaction. False when the proposal is not
    // in `from` any more (someone else decided first): nothing is written then.
    Task<bool> TransitionAsync(Guid id, ProposalState from, ProposalState to, string? approvedSha256, ProposalEvent evt, CancellationToken ct);
}

// Keeps what the converter returned and records human decisions on it. It never calls a model, never publishes, and never
// trusts what was stored: an approval runs the gates again on the stored text.
public sealed class WorkflowProposalService(IWorkflowProposalRepository repository, IWorkflowGatePipelineFactory gates, TimeProvider clock,
    WorkflowProposalOptions? options = null, WorkflowGatePolicy? policy = null)
{
    public const int NoteMaxLength = 500;
    private readonly WorkflowProposalOptions _options = options ?? new();
    private readonly WorkflowGatePolicy _policy = policy ?? WorkflowGatePolicy.Default;

    public bool RequiresSecondApprover => _options.RequireSecondApprover;

    // Only an answer a model actually produced is kept. Nothing was sent (consent, size, redaction) or the request failed:
    // there is nothing to review and nothing to audit beyond the log line.
    public async Task<WorkflowProposal?> RecordAsync(ProposalActor actor, Guid sourceConnectionId, string projectId, int pipelineId,
        Guid aiConnectionId, ConversionProposal proposal, CancellationToken ct)
    {
        if (proposal.Status == ConversionStatus.NotAttempted) return null;
        var proposed = proposal.Status == ConversionStatus.ReadyForReview && proposal.Gates is { Approvable: true } && proposal.WorkflowYaml is not null;
        var evidence = new ProposalEvidence(
            proposal.Gates?.Gates.Select(g => new StoredGate(g.Gate, g.Status.ToString(), g.Detail)).ToArray() ?? [],
            proposal.Gates?.Findings.Select(f => new StoredFinding(f.Gate, f.Code, f.Severity.ToString(), f.Message, f.Line)).ToArray() ?? [],
            proposal.Plan, proposal.SourceTasks, proposal.Provenance);
        var now = clock.GetUtcNow();
        var stored = new WorkflowProposal(Guid.NewGuid(), sourceConnectionId, projectId, pipelineId, aiConnectionId,
            proposed ? ProposalState.Proposed : ProposalState.Blocked, proposed ? null : proposal.Reason ?? "Blocked",
            proposed ? proposal.WorkflowYaml : null, proposal.WorkflowSha256, proposed ? proposal.ModelNotes : null, evidence,
            actor.Id, actor.Name, now, null);
        await repository.AddAsync(stored, new(0, stored.Id, "Created", actor.Id, actor.Name, now, stored.WorkflowSha256, null, null), ct);
        return stored;
    }

    public Task<WorkflowProposal?> GetAsync(Guid id, CancellationToken ct) => repository.GetAsync(id, ct);
    public Task<IReadOnlyList<ProposalEvent>> EventsAsync(Guid id, CancellationToken ct) => repository.EventsAsync(id, ct);
    public Task<IReadOnlyList<ProposalSummary>> ListAsync(Guid sourceConnectionId, string projectId, int pipelineId, CancellationToken ct) =>
        repository.ListAsync(sourceConnectionId, projectId, pipelineId, 50, ct);

    // Approval is bound to a text, not to a proposal: the approver sends the hash of what they read, and every check below
    // is made against the stored text, never against a stored verdict.
    public async Task<WorkflowProposal> ApproveAsync(ProposalActor actor, Guid id, string? workflowSha256,
        IReadOnlyCollection<string>? acknowledgedWarnings, string? note, CancellationToken ct)
    {
        var proposal = await repository.GetAsync(id, ct) ?? throw new WorkflowProposalException("ProposalNotFound");
        if (proposal.State != ProposalState.Proposed) throw new WorkflowProposalException("ProposalNotPending");
        if (proposal.WorkflowYaml is null || proposal.WorkflowSha256 is null) throw new WorkflowProposalException("ProposalHasNoWorkflow");
        if (!proposal.IntegrityOk) throw new WorkflowProposalException("ProposalIntegrityFailure");
        if (string.IsNullOrWhiteSpace(workflowSha256) || !string.Equals(workflowSha256.Trim(), proposal.WorkflowSha256, StringComparison.Ordinal))
            throw new WorkflowProposalException("WorkflowHashMismatch");
        if (_options.RequireSecondApprover && actor.Id == proposal.CreatedById) throw new WorkflowProposalException("SecondApproverRequired");

        var report = await gates.Create(_policy).RunAsync(proposal.WorkflowYaml, proposal.Evidence.Plan.ToGateContextFor(proposal.Evidence.SourceTasks), ct);
        if (report.WorkflowSha256 != proposal.WorkflowSha256) throw new WorkflowProposalException("ProposalIntegrityFailure");
        if (!report.Approvable)
        {
            var reasons = report.Findings.Where(f => f.Severity == GateSeverity.Blocker).Select(f => f.Code)
                .Concat(report.Gates.Where(g => g.Status == GateStatus.NotRun).Select(g => g.Gate + ":NotRun"))
                .Distinct().Order(StringComparer.Ordinal).ToArray();
            throw new WorkflowProposalException("GatesNoLongerPass", reasons);
        }

        // Every warning of THIS run has to be accepted by name; warnings are what a reviewer is asked to weigh.
        var warnings = report.Findings.Where(f => f.Severity == GateSeverity.Warning).Select(f => f.Code).Distinct().Order(StringComparer.Ordinal).ToArray();
        var accepted = (acknowledgedWarnings ?? []).ToHashSet(StringComparer.Ordinal);
        var missing = warnings.Where(w => !accepted.Contains(w)).ToArray();
        if (missing.Length > 0) throw new WorkflowProposalException("WarningsNotAcknowledged", missing);

        var detail = new ProposalEventDetail(ReportHash(report), report.PolicyVersion, warnings);
        var applied = await repository.TransitionAsync(id, ProposalState.Proposed, ProposalState.Approved, proposal.WorkflowSha256,
            new(0, id, "Approved", actor.Id, actor.Name, clock.GetUtcNow(), proposal.WorkflowSha256, CleanNote(note), detail), ct);
        if (!applied) throw new WorkflowProposalException("ProposalNotPending");
        return (await repository.GetAsync(id, ct))!;
    }

    public Task<WorkflowProposal> RejectAsync(ProposalActor actor, Guid id, string? note, CancellationToken ct) =>
        DecideAsync(actor, id, ProposalState.Proposed, ProposalState.Rejected, "Rejected", note, ct);

    public Task<WorkflowProposal> RevokeAsync(ProposalActor actor, Guid id, string? note, CancellationToken ct) =>
        DecideAsync(actor, id, ProposalState.Approved, ProposalState.Revoked, "Revoked", note, ct);

    private async Task<WorkflowProposal> DecideAsync(ProposalActor actor, Guid id, ProposalState from, ProposalState to, string type, string? note, CancellationToken ct)
    {
        var proposal = await repository.GetAsync(id, ct) ?? throw new WorkflowProposalException("ProposalNotFound");
        if (proposal.State != from) throw new WorkflowProposalException(from == ProposalState.Proposed ? "ProposalNotPending" : "ProposalNotApproved");
        var clean = CleanNote(note);
        if (clean is null || clean.Length < 3) throw new WorkflowProposalException("NoteRequired");
        var applied = await repository.TransitionAsync(id, from, to, to == ProposalState.Revoked ? proposal.ApprovedSha256 : null,
            new(0, id, type, actor.Id, actor.Name, clock.GetUtcNow(), proposal.WorkflowSha256, clean, null), ct);
        if (!applied) throw new WorkflowProposalException(from == ProposalState.Proposed ? "ProposalNotPending" : "ProposalNotApproved");
        return (await repository.GetAsync(id, ct))!;
    }

    // The one door a later step (pull request publication) must go through: the text it publishes is the text a person
    // approved, byte for byte, and that approval has not been revoked. It does not re-run the gates: publishing re-checks
    // what it needs at the destination; this only answers "what exactly was approved".
    public async Task<ApprovedWorkflow> RequireApprovedAsync(Guid id, CancellationToken ct)
    {
        var proposal = await repository.GetAsync(id, ct) ?? throw new WorkflowProposalException("ProposalNotFound");
        if (proposal.State != ProposalState.Approved) throw new WorkflowProposalException("ProposalNotApproved");
        if (proposal.WorkflowYaml is null || proposal.WorkflowSha256 is null || proposal.ApprovedSha256 != proposal.WorkflowSha256 || !proposal.IntegrityOk)
            throw new WorkflowProposalException("ProposalIntegrityFailure");
        return new(proposal.Id, proposal.WorkflowYaml, proposal.WorkflowSha256);
    }

    // Stable over the order findings were produced in, so the same verdict always has the same hash.
    public static string ReportHash(WorkflowGateReport report)
    {
        var lines = report.Gates.OrderBy(g => g.Gate, StringComparer.Ordinal).Select(g => $"gate|{g.Gate}|{g.Status}")
            .Concat(report.Findings.Select(f => $"finding|{f.Gate}|{f.Code}|{f.Severity}|{f.Line}").Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{report.WorkflowSha256}\n{report.PolicyVersion}\n{string.Join('\n', lines)}")));
    }

    private static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var clean = new string(note.Where(c => !char.IsControl(c) || c is '\n').ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > NoteMaxLength ? clean[..NoteMaxLength] : clean;
    }
}
