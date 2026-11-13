using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Business;

// ReadyForReview: every required gate ran and none blocked, so a person MAY now review it. Never "approved", never
// "equivalent". Blocked: a workflow (or the reason there is none) with the findings that stop it. NotAttempted: no model
// answer was obtained, either because nothing was sent (consent, redaction, size, classic pipeline) or because the request
// failed (Reason ModelFailure:*, the provenance says so); the deterministic scaffold of the preview remains the fallback.
public enum ConversionStatus { ReadyForReview, Blocked, NotAttempted }

public sealed record RedactionSummary(string Kind, int Count);

public sealed record ConversionAttempt(int Number, string Outcome, int? PromptTokens, int? CompletionTokens,
    string? WorkflowSha256, IReadOnlyList<string> BlockerCodes);

// What produced a proposal, so a reviewer (and later an audit trail) can reproduce the question that was asked.
public sealed record ConversionProvenance(Guid AiConnectionId, string Provider, string Host, string Model, string? ReportedModel,
    string PromptSha256, string PlanSha256, string PolicyVersion, string ActionCatalogVerifiedOn,
    IReadOnlyList<RedactionSummary> Redactions, int TotalPromptTokens, int TotalCompletionTokens,
    IReadOnlyList<ConversionAttempt> Attempts, DateTimeOffset GeneratedAtUtc);

public sealed record ConversionProposal(ConversionStatus Status, string? Reason, string? WorkflowYaml, string? WorkflowSha256,
    string? ModelNotes, WorkflowGateReport? Gates, ConversionPlan Plan, ConversionProvenance? Provenance)
{
    // The Azure tasks of the source the gates accounted for. Kept so that an approval can run the very same gates again.
    public IReadOnlyList<string> SourceTasks { get; init; } = [];

    // Name of the Azure pipeline, as the source reported it. Untrusted text: only ever shown inside quotes or code.
    public string? PipelineName { get; init; }

    public static ConversionProposal NotAttempted(string reason, ConversionPlan plan, ConversionProvenance? provenance = null) =>
        new(ConversionStatus.NotAttempted, reason, null, null, null, null, plan, provenance);
}

public sealed record ConversionOptions(int MaxRepairs = 1)
{
    public int EffectiveRepairs => Math.Clamp(MaxRepairs, 0, 3);
}

public interface IWorkflowGatePipelineFactory
{
    WorkflowGatePipeline Create(WorkflowGatePolicy policy);
}

// The analysers are optional here on purpose: a missing one yields a gate that did not run, which is not approvable.
public sealed class WorkflowGatePipelineFactory(IWorkflowToolRunner? actionlint, IWorkflowToolRunner? zizmor) : IWorkflowGatePipelineFactory
{
    public WorkflowGatePipeline Create(WorkflowGatePolicy policy) =>
        WorkflowGatePipeline.Create(actionlint, zizmor, policy, [new TaskAccountingGate()]);
}
