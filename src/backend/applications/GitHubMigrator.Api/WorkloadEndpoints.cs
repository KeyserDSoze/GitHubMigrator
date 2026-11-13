using GitHubMigrator.Business;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

public static class WorkloadEndpoints
{
    public static void MapWorkloadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workloads")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        group.MapPost("/pipelines/preview", async Task<IResult>(
            PipelinePreviewInput input, WorkloadPreviewService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PreviewPipelineAsync(
                input.SourceConnectionId, input.DestinationConnectionId,
                input.ProjectId ?? "", input.PipelineId, input.TargetRepository ?? "", ct)); }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (HttpRequestException) { return Results.UnprocessableEntity(new { error = "ProviderReadFailed" }); }
        }).RequireRateLimiting("external");

        // A proposal only: an answer a model produced is kept for review, and nothing is approved or published. The conversion reads pipeline source, so it needs an AI
        // connection that is verified AND has source consent (set by a superadmin); without it nothing is read or sent.
        group.MapPost("/pipelines/convert", async Task<IResult>(
            PipelineConversionInput input, PipelineConversionService service, WorkflowProposalService proposals,
            ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = WorkflowProposalEndpoints.ActorOf(user);
            if (actor is null) return Results.Forbid();
            try
            {
                var proposal = await service.ConvertAsync(input.SourceConnectionId, input.ProjectId ?? "", input.PipelineId,
                    input.AiConnectionId, input.MaxRepairs is { } repairs ? new ConversionOptions(repairs) : null, ct);
                var log = logs.CreateLogger("PipelineConversion");
                WorkflowProposal? kept = null;
                try { kept = await proposals.RecordAsync(actor, input.SourceConnectionId, input.ProjectId ?? "", input.PipelineId, input.AiConnectionId, proposal, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // the model was already paid for: show what it returned, but it cannot be approved without being kept
                    log.LogError(ex, "Pipeline conversion proposal could not be stored Pipeline={PipelineId}", input.PipelineId);
                }
                log.LogInformation(
                    "Pipeline conversion proposal Pipeline={PipelineId} AiConnection={AiConnectionId} Status={Status} Reason={Reason} Attempts={Attempts} Sha={Sha} Proposal={ProposalId} By={Actor}",
                    input.PipelineId, input.AiConnectionId, proposal.Status, proposal.Reason, proposal.Provenance?.Attempts.Count ?? 0, proposal.WorkflowSha256, kept?.Id, actor.Id);
                return Results.Ok(ConversionResponse.From(proposal, kept));
            }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (HttpRequestException) { return Results.UnprocessableEntity(new { error = "ProviderReadFailed" }); }
        }).RequireRateLimiting("external");

        group.MapGet("/pipelines/convert/status", (WorkflowAnalyserStatus analysers) => Results.Ok(new ConversionStatusResponse(
            analysers.Actionlint, analysers.Zizmor, analysers.Actionlint && analysers.Zizmor,
            GitHubMigrator.Business.WorkflowGates.WorkflowGatePolicy.Default.Version,
            GitHubMigrator.Business.WorkflowGates.ActionCatalog.VerifiedOn.ToString("yyyy-MM-dd"))));

        group.MapPost("/boards/preview", async Task<IResult>(
            BoardsPreviewInput input, WorkloadPreviewService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PreviewBoardsAsync(
                input.SourceConnectionId, input.ProjectId ?? "",
                input.WorkItemIds, input.KnownGitHubIssues, ct)); }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (HttpRequestException) { return Results.UnprocessableEntity(new { error = "ProviderReadFailed" }); }
        }).RequireRateLimiting("external");
        group.MapPost("/boards/reconcile", async Task<IResult>(
            BoardsReconciliationInput input, BoardReconciliationService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.ReconcileAsync(
                    input.SourceConnectionId, input.DestinationConnectionId,
                    input.ProjectId ?? "", input.TargetRepository ?? "",
                    input.WorkItemIds, input.KnownGitHubIssues,
                    input.KnownGitHubCommits, input.KnownGitHubPullRequests, ct));
            }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (HttpRequestException) { return Results.UnprocessableEntity(new { error = "ProviderReadFailed" }); }
        }).RequireRateLimiting("external");
    }
}

public sealed record BoardsReconciliationInput(Guid SourceConnectionId, Guid DestinationConnectionId,
    string? ProjectId, string? TargetRepository,
    IReadOnlyList<int>? WorkItemIds, IReadOnlyDictionary<int, int>? KnownGitHubIssues,
    IReadOnlyDictionary<string, string>? KnownGitHubCommits,
    IReadOnlyDictionary<int, int>? KnownGitHubPullRequests);

public sealed record PipelinePreviewInput(Guid SourceConnectionId, Guid DestinationConnectionId,
    string? ProjectId, int PipelineId, string? TargetRepository);
public sealed record BoardsPreviewInput(Guid SourceConnectionId, string? ProjectId,
    IReadOnlyList<int>? WorkItemIds, IReadOnlyDictionary<int, int>? KnownGitHubIssues);

public sealed record PipelineConversionInput(Guid SourceConnectionId, string? ProjectId, int PipelineId, Guid AiConnectionId, int? MaxRepairs = null);
public sealed record WorkflowAnalyserStatus(bool Actionlint, bool Zizmor);
public sealed record ConversionStatusResponse(bool Actionlint, bool Zizmor, bool CanBeApprovable, string PolicyVersion, string ActionCatalogVerifiedOn);

public sealed record GateResponse(string Name, string Status, string? Detail);
public sealed record FindingResponse(string Gate, string Code, string Severity, string Message, int? Line);
public sealed record PlanEntryResponse(string Name, string Origin);
public sealed record ConnectionMappingResponse(string Connection, string Kind, string Guidance, IReadOnlyList<string> Secrets, IReadOnlyList<string> Variables);
public sealed record PlanResponse(IReadOnlyList<PlanEntryResponse> Secrets, IReadOnlyList<PlanEntryResponse> Variables,
    IReadOnlyList<ConnectionMappingResponse> Connections, IReadOnlyList<string> Notes);
public sealed record AttemptResponse(int Number, string Outcome, int? PromptTokens, int? CompletionTokens, string? WorkflowSha256, IReadOnlyList<string> BlockerCodes);
public sealed record ProvenanceResponse(Guid AiConnectionId, string Provider, string Host, string Model, string? ReportedModel, string PromptSha256,
    string PlanSha256, string PolicyVersion, string ActionCatalogVerifiedOn, IReadOnlyList<RedactionSummary> Redactions,
    int TotalPromptTokens, int TotalCompletionTokens, IReadOnlyList<AttemptResponse> Attempts, DateTimeOffset GeneratedAtUtc);

// Never carries the source YAML, only the proposed workflow, the evidence and a statement of what this is not.
// Saved means the proposal was kept and can be reviewed (ProposalId); it never means approved or published.
public sealed record ConversionResponse(string Status, string? Reason, string? WorkflowYaml, string? WorkflowSha256, string? ModelNotes,
    bool Approvable, bool ProposalOnly, bool Saved, Guid? ProposalId, string? ProposalState, IReadOnlyList<GateResponse> Gates,
    IReadOnlyList<FindingResponse> Findings, PlanResponse Plan, ProvenanceResponse? Provenance)
{
    public static ConversionResponse From(ConversionProposal p, WorkflowProposal? kept = null) => new(
        p.Status.ToString(), p.Reason, p.WorkflowYaml, p.WorkflowSha256, p.ModelNotes, p.Gates?.Approvable ?? false, true, kept is not null,
        kept?.Id, kept?.State.ToString(),
        p.Gates?.Gates.Select(g => new GateResponse(g.Gate, g.Status.ToString(), g.Detail)).ToArray() ?? [],
        p.Gates?.Findings.Select(f => new FindingResponse(f.Gate, f.Code, f.Severity.ToString(), f.Message, f.Line)).ToArray() ?? [],
        ConversionMapping.Plan(p.Plan), ConversionMapping.Provenance(p.Provenance));
}

public static class ConversionMapping
{
    public static PlanResponse Plan(ConversionPlan plan) => new(
        plan.Secrets.Select(s => new PlanEntryResponse(s.Name, s.Origin)).ToArray(), plan.Variables.Select(v => new PlanEntryResponse(v.Name, v.Origin)).ToArray(),
        plan.Connections.Select(c => new ConnectionMappingResponse(c.Connection, c.Kind, c.Guidance, c.Secrets, c.Variables)).ToArray(), plan.Notes);

    public static ProvenanceResponse? Provenance(ConversionProvenance? p) => p is null ? null : new(p.AiConnectionId, p.Provider, p.Host, p.Model, p.ReportedModel,
        p.PromptSha256, p.PlanSha256, p.PolicyVersion, p.ActionCatalogVerifiedOn, p.Redactions, p.TotalPromptTokens, p.TotalCompletionTokens,
        p.Attempts.Select(a => new AttemptResponse(a.Number, a.Outcome, a.PromptTokens, a.CompletionTokens, a.WorkflowSha256, a.BlockerCodes)).ToArray(), p.GeneratedAtUtc);
}
