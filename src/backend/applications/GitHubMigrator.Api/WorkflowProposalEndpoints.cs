using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// Kept conversion proposals and the human decisions on them. Approving records WHO accepted WHICH text (its SHA-256) after the
// gates were run again; it never publishes, never creates a branch or pull request and never re-enables the original triggers.
public static class WorkflowProposalEndpoints
{
    public static void MapWorkflowProposalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workloads/pipelines/proposals")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        group.MapGet("", async Task<IResult> (Guid sourceConnectionId, string? projectId, int pipelineId, WorkflowProposalService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 100 || pipelineId < 1) return Results.BadRequest(new { error = "InvalidProposalQuery" });
            var rows = await service.ListAsync(sourceConnectionId, projectId, pipelineId, ct);
            return Results.Ok(rows.Select(r => new ProposalSummaryResponse(r.Id, r.State.ToString(), r.Reason, r.WorkflowSha256, r.CreatedBy, r.CreatedAtUtc)));
        });

        group.MapGet("/{id:guid}", async Task<IResult> (Guid id, WorkflowProposalService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var proposal = await service.GetAsync(id, ct);
            return proposal is null ? Results.NotFound() : Results.Ok(await DetailAsync(proposal, service, user, ct));
        });

        group.MapPost("/{id:guid}/approve", async Task<IResult> (Guid id, ApprovalInput input, WorkflowProposalService service,
            ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = ActorOf(user);
            if (actor is null) return Results.Forbid();
            try
            {
                var approved = await service.ApproveAsync(actor, id, input.WorkflowSha256, input.AcknowledgedWarnings, input.Note, ct);
                logs.CreateLogger("WorkflowProposals").LogInformation("Workflow proposal approved Proposal={ProposalId} Sha={Sha} By={Actor}", id, approved.ApprovedSha256, actor.Id);
                return Results.Ok(await DetailAsync(approved, service, user, ct));
            }
            catch (WorkflowProposalException ex) { return Refused(ex, logs, id, actor, "approve"); }
        }).RequireRateLimiting("external");

        group.MapPost("/{id:guid}/reject", (Guid id, DecisionInput input, WorkflowProposalService service, ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
            DecideAsync("reject", id, input, service, user, logs, ct, (a, i, n, c) => service.RejectAsync(a, i, n, c)));

        group.MapPost("/{id:guid}/revoke", (Guid id, DecisionInput input, WorkflowProposalService service, ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
            DecideAsync("revoke", id, input, service, user, logs, ct, (a, i, n, c) => service.RevokeAsync(a, i, n, c)));
    }

    public static ProposalActor? ActorOf(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty && !string.IsNullOrWhiteSpace(user.Identity?.Name)
            ? new(id, user.Identity!.Name!.Length > 160 ? user.Identity.Name[..160] : user.Identity.Name) : null;

    private static async Task<IResult> DecideAsync(string verb, Guid id, DecisionInput input, WorkflowProposalService service, ClaimsPrincipal user,
        ILoggerFactory logs, CancellationToken ct, Func<ProposalActor, Guid, string?, CancellationToken, Task<WorkflowProposal>> decide)
    {
        var actor = ActorOf(user);
        if (actor is null) return Results.Forbid();
        try
        {
            var proposal = await decide(actor, id, input.Note, ct);
            logs.CreateLogger("WorkflowProposals").LogInformation("Workflow proposal {Verb} Proposal={ProposalId} State={State} By={Actor}", verb, id, proposal.State, actor.Id);
            return Results.Ok(await DetailAsync(proposal, service, user, ct));
        }
        catch (WorkflowProposalException ex) { return Refused(ex, logs, id, actor, verb); }
    }

    private static IResult Refused(WorkflowProposalException ex, ILoggerFactory logs, Guid id, ProposalActor actor, string verb)
    {
        logs.CreateLogger("WorkflowProposals").LogWarning("Workflow proposal {Verb} refused Proposal={ProposalId} Code={Code} By={Actor}", verb, id, ex.Code, actor.Id);
        return ex.Code switch
        {
            "ProposalNotFound" => Results.NotFound(),
            "SecondApproverRequired" => Results.Json(new { error = ex.Code }, statusCode: StatusCodes.Status403Forbidden),
            "NoteRequired" => Results.BadRequest(new { error = ex.Code }),
            "ProposalNotPending" or "ProposalNotApproved" or "WorkflowHashMismatch" or "ProposalIntegrityFailure" => Results.Conflict(new { error = ex.Code }),
            _ => Results.UnprocessableEntity(new { error = ex.Code, details = ex.Details })
        };
    }

    private static async Task<ProposalDetailResponse> DetailAsync(WorkflowProposal p, WorkflowProposalService service, ClaimsPrincipal user, CancellationToken ct)
    {
        var events = await service.EventsAsync(p.Id, ct);
        var actor = ActorOf(user);
        var createdByMe = actor is not null && actor.Id == p.CreatedById;
        var pending = p.State == ProposalState.Proposed && p.IntegrityOk;
        return new(p.Id, p.State.ToString(), p.Reason, p.SourceConnectionId, p.ProjectId, p.PipelineId, p.AiConnectionId, p.WorkflowYaml, p.WorkflowSha256,
            p.ModelNotes, p.ApprovedSha256, p.IntegrityOk, p.CreatedBy, p.CreatedAtUtc, createdByMe, service.RequiresSecondApprover,
            pending && !(service.RequiresSecondApprover && createdByMe),
            p.Evidence.Gates.Select(g => new GateResponse(g.Name, g.Status, g.Detail)).ToArray(),
            p.Evidence.Findings.Select(f => new FindingResponse(f.Gate, f.Code, f.Severity, f.Message, f.Line)).ToArray(),
            ConversionMapping.Plan(p.Evidence.Plan), ConversionMapping.Provenance(p.Evidence.Provenance),
            events.Select(e => new ProposalEventResponse(e.Id, e.Type, e.Actor, e.OccurredAtUtc, e.WorkflowSha256, e.Note, e.Detail?.ReportSha256,
                e.Detail?.PolicyVersion, e.Detail?.AcknowledgedWarnings ?? [])).ToArray());
    }
}

public sealed record ApprovalInput(string? WorkflowSha256, string[]? AcknowledgedWarnings, string? Note);
public sealed record DecisionInput(string? Note);
public sealed record ProposalSummaryResponse(Guid Id, string State, string? Reason, string? WorkflowSha256, string CreatedBy, DateTimeOffset CreatedAtUtc);
public sealed record ProposalEventResponse(long Id, string Type, string Actor, DateTimeOffset OccurredAtUtc, string? WorkflowSha256, string? Note,
    string? ReportSha256, string? PolicyVersion, IReadOnlyList<string> AcknowledgedWarnings);

// The text is returned so that the person approving reads exactly what the hash names. The findings are the ones recorded when it
// was proposed; an approval runs the gates again and says which warnings are still to be accepted.
public sealed record ProposalDetailResponse(Guid Id, string State, string? Reason, Guid SourceConnectionId, string ProjectId, int PipelineId,
    Guid AiConnectionId, string? WorkflowYaml, string? WorkflowSha256, string? ModelNotes, string? ApprovedSha256, bool IntegrityOk, string CreatedBy,
    DateTimeOffset CreatedAtUtc, bool CreatedByMe, bool RequiresSecondApprover, bool CanApprove, IReadOnlyList<GateResponse> Gates,
    IReadOnlyList<FindingResponse> Findings, PlanResponse Plan, ProvenanceResponse? Provenance, IReadOnlyList<ProposalEventResponse> Events);
