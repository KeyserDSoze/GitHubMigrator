using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// Kept conversion proposals and the human decisions on them. Approving records WHO accepted WHICH text (its SHA-256) after the
// gates were run again; it never publishes and never creates a branch or pull request. The original Azure triggers come back only
// through a derived proposal of their own (derive-triggers), which is approved separately.
public static class WorkflowProposalEndpoints
{
    public static void MapWorkflowProposalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workloads/pipelines/proposals")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        // Whether this deployment may write pull requests at all, and where. Names only: never the app, the installation or the key reference.
        app.MapGet("/api/workloads/pipelines/publication/status", (WorkflowPublicationExecutor executor) =>
                Results.Ok(new PublicationStatusResponse(executor.Enabled, executor.AllowedRepositories)))
            .RequireAuthorization("PasswordChanged").RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        // Mints and checks the publication credential for one allow-listed repository and discards it. Writes nothing.
        app.MapPost("/api/workloads/pipelines/publication/credential-check", async Task<IResult> (CredentialCheckInput input, WorkflowPublicationExecutor executor,
                ILoggerFactory logs, CancellationToken ct) =>
            {
                var parts = (input.Repository ?? "").Split('/');
                if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace)) return Results.BadRequest(new { error = "InvalidRepository" });
                try
                {
                    var seen = await executor.CheckCredentialAsync(parts[0], parts[1], ct);
                    logs.CreateLogger("WorkflowPublication").LogInformation("Publication credential checked Repository={Repository} Installation={Installation}", input.Repository, seen.InstallationId);
                    return Results.Ok(new { ok = true, installationId = seen.InstallationId, repositoryId = seen.RepositoryId, permissions = seen.Permissions, tokenExpiresAtUtc = seen.TokenExpiresAtUtc });
                }
                catch (WorkflowPublicationException ex)
                {
                    return ex.Code is "PublicationDisabled" or "PublicationRepositoryNotAllowed"
                        ? Results.Json(new { error = ex.Code }, statusCode: StatusCodes.Status403Forbidden) : Results.UnprocessableEntity(new { error = ex.Code });
                }
            }).RequireAuthorization("PasswordChanged").RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" }).RequireRateLimiting("external");

        group.MapGet("", async Task<IResult> (Guid sourceConnectionId, string? projectId, int pipelineId, WorkflowProposalService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 100 || pipelineId < 1) return Results.BadRequest(new { error = "InvalidProposalQuery" });
            var rows = await service.ListAsync(sourceConnectionId, projectId, pipelineId, ct);
            return Results.Ok(rows.Select(r => new ProposalSummaryResponse(r.Id, r.State.ToString(), r.Reason, r.WorkflowSha256, r.CreatedBy, r.CreatedAtUtc, r.Kind)));
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

        // Converts the Azure triggers of an APPROVED conversion into a proposal of its own. The pipeline's YAML is read here and mapped by fixed rules:
        // no model sees it. The result is only a proposal; it is approved (by its own hash) and published separately, and it starts by itself once merged.
        group.MapPost("/{id:guid}/derive-triggers", async Task<IResult> (Guid id, PipelineTriggerService service, WorkflowProposalService proposals,
            ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = ActorOf(user);
            if (actor is null) return Results.Forbid();
            try
            {
                var derived = await service.DeriveAsync(actor, id, ct);
                logs.CreateLogger("WorkflowProposals").LogInformation(
                    "Workflow trigger proposal Parent={ParentId} Proposal={ProposalId} State={State} Reason={Reason} Sha={Sha} By={Actor}",
                    id, derived.Id, derived.State, derived.Reason, derived.WorkflowSha256, actor.Id);
                return Results.Ok(await DetailAsync(derived, proposals, user, ct));
            }
            catch (TriggerDerivationException ex)
            {
                logs.CreateLogger("WorkflowProposals").LogWarning("Workflow trigger derivation refused Proposal={ProposalId} Code={Code} By={Actor}", id, ex.Code, actor.Id);
                return ex.Code switch
                {
                    "NoAutomaticTriggers" or "ParentIsNotAConversion" => Results.Conflict(new { error = ex.Code }),
                    "SourceYamlUnreadable" or "SourceYamlUnavailable" or "WorkflowNotReadable" or "OnSectionNotFound" or "WorkflowHasOtherTriggers" or "TriggerShapeUnsupported"
                        or "DerivedWorkflowInvalid" => Results.UnprocessableEntity(new { error = ex.Code }),
                    _ => Results.BadRequest(new { error = ex.Code })
                };
            }
            catch (WorkflowProposalException ex) { return Refused(ex, logs, id, actor, "derive-triggers"); }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (HttpRequestException) { return Results.UnprocessableEntity(new { error = "ProviderReadFailed" }); }
        }).RequireRateLimiting("external");

        // Plans the pull request for an APPROVED proposal: reads the destination repository (GET only) and returns where the workflow
        // would go, what the description says, what is in the way and a hash of the whole plan. It writes nothing anywhere.
        group.MapPost("/{id:guid}/publication-plan", async Task<IResult> (Guid id, PublicationPlanInput input, WorkflowPublicationService service,
            ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = ActorOf(user);
            if (actor is null) return Results.Forbid();
            try
            {
                var (plan, yaml) = await service.PlanAsync(id, input.DestinationConnectionId, input.Repository ?? "", string.IsNullOrWhiteSpace(input.BaseBranch) ? null : input.BaseBranch.Trim(), ct);
                logs.CreateLogger("WorkflowProposals").LogInformation(
                    "Workflow publication planned Proposal={ProposalId} Repository={Organization}/{Repository} Ready={Ready} Plan={Plan} By={Actor}",
                    id, plan.Organization, plan.Repository, plan.Ready, plan.PlanSha256, actor.Id);
                return Results.Ok(PublicationPlanResponse.From(plan, yaml));
            }
            catch (WorkflowProposalException ex) { return Refused(ex, logs, id, actor, "plan"); }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        // The only place the product writes to a destination. Superadmin only, off unless the operator enabled it, one allow-listed repository, and only
        // the plan the person confirms by its hash: the plan is made again here and must still be the same, with nothing in the way.
        group.MapPost("/{id:guid}/publish", async Task<IResult> (Guid id, PublishInput input, WorkflowPublicationExecutor executor,
            ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = ActorOf(user);
            if (actor is null) return Results.Forbid();
            var log = logs.CreateLogger("WorkflowPublication");
            try
            {
                var record = await executor.PublishAsync(actor, id, input.DestinationConnectionId, input.Repository ?? "",
                    string.IsNullOrWhiteSpace(input.BaseBranch) ? null : input.BaseBranch.Trim(), input.PlanSha256, ct);
                log.LogInformation("Workflow published Proposal={ProposalId} Repository={Organization}/{Repository} Pull={Pull} Commit={Commit} Plan={Plan} By={Actor}",
                    id, record.Organization, record.Repository, record.PullNumber, record.CommitSha, record.PlanSha256, actor.Id);
                return Results.Ok(PublicationResponse.From(record));
            }
            catch (WorkflowPublicationException ex)
            {
                log.LogWarning("Workflow publication refused Proposal={ProposalId} Code={Code} Written={Written} By={Actor}", id, ex.Code, ex.Record?.WritesPossible, actor.Id);
                return ex.Code switch
                {
                    "PublicationDisabled" or "PublicationRepositoryNotAllowed" => Results.Json(new { error = ex.Code }, statusCode: StatusCodes.Status403Forbidden),
                    "PlanConfirmationRequired" => Results.BadRequest(new { error = ex.Code }),
                    "PublicationPlanChanged" or "PublicationPlanNotReady" or "PublicationAlreadyAttempted" or "PublicationLedgerConflict" =>
                        Results.Conflict(new { error = ex.Code, details = ex.Details }),
                    _ => Results.UnprocessableEntity(new { error = ex.Code, details = ex.Details, publication = ex.Record is null ? null : PublicationResponse.From(ex.Record) })
                };
            }
            catch (WorkflowProposalException ex) { return Refused(ex, logs, id, actor, "publish"); }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" }).RequireRateLimiting("external");

        group.MapGet("/{id:guid}/publications", async Task<IResult> (Guid id, WorkflowPublicationExecutor executor, CancellationToken ct) =>
            Results.Ok((await executor.ListAsync(id, ct)).Select(PublicationResponse.From)));

        // Sets one recorded attempt beside what is at the destination now: GET requests only, with the destination connection's read-only credential. It changes
        // nothing at the destination and nothing in the ledger, and it does not retry or clean up: it says what a person would find.
        group.MapPost("/{id:guid}/publications/{publicationId:guid}/reconcile", async Task<IResult> (Guid id, Guid publicationId, ReconcileInput input,
            PublicationReconciliationService service, ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = ActorOf(user);
            if (actor is null) return Results.Forbid();
            try
            {
                var result = await service.ReconcileAsync(id, publicationId, input.DestinationConnectionId, ct);
                logs.CreateLogger("WorkflowPublication").LogInformation(
                    "Workflow publication reconciled Proposal={ProposalId} Publication={PublicationId} Verdict={Verdict} LedgerState={State} By={Actor}",
                    id, publicationId, result.Verdict, result.LedgerState, actor.Id);
                return Results.Ok(ReconciliationResponse.From(result));
            }
            catch (WorkflowPublicationException ex)
            {
                return ex.Code switch
                {
                    "PublicationNotFound" => Results.NotFound(),
                    "DestinationMismatch" => Results.Conflict(new { error = ex.Code }),
                    _ => Results.UnprocessableEntity(new { error = ex.Code })
                };
            }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        // Frees the exact plan an attempt kept blocked, after a person cleaned the destination: the product reads it again (GET only, read-only credential) and releases the attempt only if
        // no branch, no open pull request and no merged one remains. Superadmin only, with a note. It writes nothing at the destination; the attempt stays on record as it was.
        group.MapPost("/{id:guid}/publications/{publicationId:guid}/release", async Task<IResult> (Guid id, Guid publicationId, ReleaseInput input,
            PublicationReleaseService service, ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var actor = ActorOf(user);
            if (actor is null) return Results.Forbid();
            var log = logs.CreateLogger("WorkflowPublication");
            try
            {
                var record = await service.ReleaseAsync(actor, id, publicationId, input.DestinationConnectionId, input.Note, ct);
                log.LogInformation("Workflow publication released Proposal={ProposalId} Publication={PublicationId} State={State} By={Actor}", id, publicationId, record.State, actor.Id);
                return Results.Ok(PublicationResponse.From(record));
            }
            catch (WorkflowPublicationException ex)
            {
                log.LogWarning("Workflow publication release refused Proposal={ProposalId} Publication={PublicationId} Code={Code} By={Actor}", id, publicationId, ex.Code, actor.Id);
                var body = new { error = ex.Code, details = ex.Details, publication = ex.Record is null ? null : PublicationResponse.From(ex.Record) };
                return ex.Code switch
                {
                    "PublicationNotFound" => Results.NotFound(),
                    "NoteRequired" => Results.BadRequest(new { error = ex.Code }),
                    "DestinationUnreadable" => Results.UnprocessableEntity(body),
                    _ => Results.Conflict(body) // still running, already released, already free, leftovers at the destination, lost the race, wrong organization
                };
            }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" }).RequireRateLimiting("external");

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
                e.Detail?.PolicyVersion, e.Detail?.AcknowledgedWarnings ?? [])).ToArray(),
            p.Kind, p.ParentProposalId,
            p.Evidence.Triggers is null ? null : new TriggerMappingResponse(p.Evidence.Triggers.Mapped,
                p.Evidence.Triggers.Differences.Select(d => new TriggerDifferenceResponse(d.Code, d.Message)).ToArray()));
    }
}

public sealed record ApprovalInput(string? WorkflowSha256, string[]? AcknowledgedWarnings, string? Note);
public sealed record DecisionInput(string? Note);
public sealed record ProposalSummaryResponse(Guid Id, string State, string? Reason, string? WorkflowSha256, string CreatedBy, DateTimeOffset CreatedAtUtc, string Kind);
public sealed record TriggerDifferenceResponse(string Code, string Message);
public sealed record TriggerMappingResponse(IReadOnlyList<string> Mapped, IReadOnlyList<TriggerDifferenceResponse> Differences);
public sealed record ProposalEventResponse(long Id, string Type, string Actor, DateTimeOffset OccurredAtUtc, string? WorkflowSha256, string? Note,
    string? ReportSha256, string? PolicyVersion, IReadOnlyList<string> AcknowledgedWarnings);

// The text is returned so that the person approving reads exactly what the hash names. The findings are the ones recorded when it
// was proposed; an approval runs the gates again and says which warnings are still to be accepted.
public sealed record ProposalDetailResponse(Guid Id, string State, string? Reason, Guid SourceConnectionId, string ProjectId, int PipelineId,
    Guid AiConnectionId, string? WorkflowYaml, string? WorkflowSha256, string? ModelNotes, string? ApprovedSha256, bool IntegrityOk, string CreatedBy,
    DateTimeOffset CreatedAtUtc, bool CreatedByMe, bool RequiresSecondApprover, bool CanApprove, IReadOnlyList<GateResponse> Gates,
    IReadOnlyList<FindingResponse> Findings, PlanResponse Plan, ProvenanceResponse? Provenance, IReadOnlyList<ProposalEventResponse> Events,
    string Kind, Guid? ParentProposalId, TriggerMappingResponse? Triggers);

public sealed record ReconcileInput(Guid DestinationConnectionId);
public sealed record ReleaseInput(Guid DestinationConnectionId, string? Note);
public sealed record ReconciliationPullResponse(int Number, string State, bool Merged, string? HeadSha, string? MergeCommitSha);
public sealed record ReconciliationFactsResponse(bool RepositoryExists, bool Archived, string? BaseTipSha, string? HeadBranchSha,
    IReadOnlyList<ReconciliationPullResponse> Pulls, string FileOnHead, string FileOnBase);

// A view, not an action: WritesPerformed is always false. The facts are what the destination showed; the findings are what the product makes of them.
public sealed record ReconciliationResponse(Guid PublicationId, Guid ProposalId, string Verdict, IReadOnlyList<PublicationFindingResponse> Findings,
    ReconciliationFactsResponse? Observed, string LedgerState, bool LedgerWritesPossible, string? LedgerCommitSha, int? LedgerPullNumber, bool WritesPerformed, DateTimeOffset ReadAtUtc,
    bool LedgerReleased, bool LedgerBlocksPlan)
{
    public static ReconciliationResponse From(PublicationReconciliation r) => new(r.PublicationId, r.ProposalId, r.Verdict,
        r.Findings.Select(f => new PublicationFindingResponse(f.Code, f.Severity, f.Message)).ToArray(),
        r.Observed is null ? null : new(r.Observed.RepositoryExists, r.Observed.Archived, r.Observed.BaseTipSha, r.Observed.HeadBranchSha,
            r.Observed.Pulls.Select(p => new ReconciliationPullResponse(p.Number, p.State, p.Merged, p.HeadSha, p.MergeCommitSha)).ToArray(),
            r.Observed.FileOnHead.ToString(), r.Observed.FileOnBase.ToString()),
        r.LedgerState, r.LedgerWritesPossible, r.LedgerCommitSha, r.LedgerPullNumber, false, r.ReadAtUtc, r.LedgerReleased, r.LedgerBlocksPlan);
}
public sealed record PublishInput(Guid DestinationConnectionId, string? Repository, string? BaseBranch, string? PlanSha256);
public sealed record PublicationStatusResponse(bool Enabled, IReadOnlyList<string> AllowedRepositories);
public sealed record CredentialCheckInput(string? Repository);
public sealed record PublicationResponse(Guid Id, Guid ProposalId, string State, string Organization, string Repository, string BaseBranch, string BaseSha, string HeadBranch,
    string WorkflowPath, string PlanSha256, string? CommitSha, int? PullNumber, string? PullUrl, string? ErrorCode, string? FailedAtState, bool WritesPossible, string RequestedBy,
    DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc, string Mode, string? ReplacedWorkflowSha256, DateTimeOffset? ReleasedAtUtc, string? ReleasedBy, string? ReleaseNote, bool BlocksItsPlan)
{
    public static PublicationResponse From(PublicationRecord r) => new(r.Id, r.ProposalId, r.State, r.Organization, r.Repository, r.BaseBranch, r.BaseSha, r.HeadBranch,
        r.WorkflowPath, r.PlanSha256, r.CommitSha, r.PullNumber, r.PullUrl, r.ErrorCode, r.FailedAtState, r.WritesPossible, r.RequestedBy, r.StartedAtUtc, r.UpdatedAtUtc,
        r.Mode, r.ReplacedWorkflowSha256, r.ReleasedAtUtc, r.ReleasedBy, r.ReleaseNote, r.BlocksItsPlan);
}
public sealed record PublicationPlanInput(Guid DestinationConnectionId, string? Repository, string? BaseBranch);
public sealed record PublicationFindingResponse(string Code, string Severity, string Message);
public sealed record PublicationStepResponse(string Code, string Title, string? Command);

// A plan, not an action: Ready says nothing stands in the way, never that anything was published.
public sealed record PublicationPlanResponse(Guid ProposalId, string WorkflowSha256, string Organization, string Repository, string BaseBranch, string? BaseSha,
    string HeadBranch, string WorkflowPath, string CommitMessage, string PullRequestTitle, string PullRequestBody, bool Ready, bool WritesPerformed,
    IReadOnlyList<PublicationFindingResponse> Findings, IReadOnlyList<PublicationStepResponse> Steps, string PlanSha256, DateTimeOffset ReadAtUtc, string WorkflowYaml,
    string Mode, string? ReplacesWorkflowSha256)
{
    public static PublicationPlanResponse From(PublicationPlan p, string yaml) => new(p.ProposalId, p.WorkflowSha256, p.Organization, p.Repository, p.BaseBranch,
        p.BaseSha, p.HeadBranch, p.WorkflowPath, p.CommitMessage, p.PullRequestTitle, p.PullRequestBody, p.Ready, false,
        p.Findings.Select(f => new PublicationFindingResponse(f.Code, f.Severity, f.Message)).ToArray(),
        p.Steps.Select(s => new PublicationStepResponse(s.Code, s.Title, s.Command)).ToArray(), p.PlanSha256, p.ReadAtUtc, yaml, p.Mode, p.ReplacesWorkflowSha256);
}
