using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class IncrementalPreparationEndpoints
{
    public static void MapIncrementalPreparationEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        routes.MapPost("", async Task<IResult> (
            PrepareIncrementalRequest request, ClaimsPrincipal principal,
            IncrementalPreparationService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            try
            {
                var run = await service.EnqueueAsync(
                    request.SnapshotId, request.DestinationConnectionId, actor, ct);
                return Results.Accepted($"/api/incremental-preparations/{run.Id}", run);
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationDependencyNotFound" }); }
            catch (IncrementalPreparationException ex) when (
                ex.Code is "IncrementalSnapshotSuperseded" or "IncrementalJournalUnavailable")
            { return Results.Conflict(new { error = ex.Code }); }
            catch (IncrementalPreparationException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (StagingChangeJournalUnavailableException)
            { return Results.Conflict(new { error = "IncrementalJournalUnavailable" }); }
            catch (StagingChangeIntegrityException)
            { return Results.Conflict(new { error = "IncrementalJournalMismatch" }); }
        }).RequireRateLimiting("staging-sync");

        routes.MapGet("/{id:guid}", async Task<IResult> (
            Guid id, IncrementalPreparationService service, CancellationToken ct) =>
        {
            var run = await service.GetAsync(id, ct);
            return run is null
                ? Results.NotFound(new { error = "PreparationNotFound" })
                : Results.Ok(run);
        });

        routes.MapGet("/{id:guid}/items", async Task<IResult> (
            Guid id, string? afterType, string? afterId, int? take,
            IncrementalPreparationService service, CancellationToken ct) =>
        {
            try
            {
                var page = await service.ListItemsAsync(id, afterType, afterId, take ?? 50, ct);
                return page is null
                    ? Results.NotFound(new { error = "PreparationNotFound" })
                    : Results.Ok(page);
            }
            catch (IncrementalPreparationException ex)
                when (ex.Code == "PreparationNotReady")
            { return Results.Conflict(new { error = ex.Code }); }
            catch (IncrementalPreparationException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
        });

        // F-027 dry-run is purely PostgreSQL-backed. It cannot contact,
        // approve execution on, or write to GitHub.
        routes.MapPost("/{id:guid}/dry-run", async Task<IResult> (
            Guid id, ClaimsPrincipal principal,
            IncrementalReviewService reviews, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            try { return Results.Ok(await reviews.DryRunAsync(id, actor, ct)); }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalReviewException ex)
            { return ReviewError(ex); }
        }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/review", async Task<IResult> (
            Guid id, IncrementalReviewService reviews, CancellationToken ct) =>
        {
            var review = await reviews.GetAsync(id, ct);
            return review is null
                ? Results.NotFound(new { error = "ReviewNotFound" })
                : Results.Ok(review);
        });

        routes.MapPost("/{id:guid}/review/decision", async Task<IResult> (
            Guid id, IncrementalReviewDecisionRequest request,
            ClaimsPrincipal principal, IncrementalReviewService reviews,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            if (request.Decision == "ApproveForPlanning" && !principal.IsInRole("superadmin"))
                return Results.Forbid();
            try
            {
                return Results.Ok(await reviews.DecideAsync(id, actor, request.Decision ?? "",
                    request.ExpectedDryRunSha256 ?? "", request.AcknowledgeSensitive,
                    request.ReasonCode, ct));
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalReviewException ex)
            { return ReviewError(ex); }
        }).RequireRateLimiting("incremental-review");

        routes.MapPost("/{id:guid}/review/revoke", async Task<IResult> (
            Guid id, RevokeIncrementalReviewRequest request,
            ClaimsPrincipal principal, IncrementalReviewService reviews,
            CancellationToken ct) =>
        {
            if (!principal.IsInRole("superadmin")) return Results.Forbid();
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            try
            {
                return Results.Ok(await reviews.RevokeAsync(id, actor,
                    request.ExpectedDryRunSha256 ?? "", request.ReasonCode ?? "", ct));
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalReviewException ex)
            { return ReviewError(ex); }
        }).RequireRateLimiting("incremental-review");

        // F-028: explicitly requested, fixed-host GitHub GET probes.
        // The result can only be Blocked/Unverified, never executable.
        routes.MapPost("/{id:guid}/target-preflight", async Task<IResult> (
            Guid id, LiveTargetPreflightRequest request, ClaimsPrincipal principal,
            IncrementalTargetPreflightService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor))
                return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.CheckAsync(id, actor,
                    request.ExpectedReviewSha256 ?? "",
                    request.Repositories, ct));
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (PlanInputException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (IncrementalTargetPreflightException ex)
                when (ex.Code is "InvalidTargetPreflightRequest" or
                    "InvalidTargetRepositorySelection" or
                    "InvalidTargetPreflightEvidence")
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        routes.MapGet("/{id:guid}/target-preflight", async Task<IResult> (
            Guid id, IncrementalTargetPreflightService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.GetAsync(id, ct);
                return result is null
                    ? Results.NotFound(new { error = "TargetPreflightNotFound" })
                    : Results.Ok(result);
            }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        });

        // F-029: immutable, non-executable operational manifest. Only the
        // denied-only authorization audit can be written, never a target.
        routes.MapPost("/{id:guid}/operation-plans", async Task<IResult> (
            Guid id, CreateOperationPlanRequest request,
            ClaimsPrincipal principal, IncrementalOperationPlanService service,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor))
                return Results.Unauthorized();
            try
            {
                var plan = await service.CreateAsync(id, actor,
                    request.ExpectedPreflightSha256 ?? "", ct);
                return Results.Ok(plan);
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalOperationPlanException ex)
            { return OperationPlanError(ex); }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans", async Task<IResult> (
            Guid id, IncrementalOperationPlanService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ListAsync(id, ct)); }
            catch (IncrementalOperationPlanException ex)
            { return OperationPlanError(ex); }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}",
            async Task<IResult> (Guid id, Guid planId,
                IncrementalOperationPlanService service, CancellationToken ct) =>
            {
                try
                {
                    var plan = await service.GetAsync(id, planId, ct);
                    return plan is null
                        ? Results.NotFound(new { error = "OperationPlanNotFound" })
                        : Results.Ok(plan);
                }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/execution-authorization",
            async Task<IResult> (Guid id, Guid planId,
                AuthorizeOperationPlanRequest request, ClaimsPrincipal principal,
                IncrementalOperationPlanService service, CancellationToken ct) =>
            {
                if (!principal.IsInRole("superadmin")) return Results.Forbid();
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                        out var actor))
                    return Results.Unauthorized();
                try
                {
                    // Always denied with a safe code; the attempt is audited.
                    var attempt = await service.RequestAuthorizationAsync(
                        id, planId, actor, request.ExpectedManifestSha256 ?? "",
                        request.AcknowledgeBlockedOperations, ct);
                    return Results.Conflict(attempt);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "OperationPlanNotFound" }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/authorization-attempts",
            async Task<IResult> (Guid id, Guid planId,
                IncrementalOperationPlanService service, CancellationToken ct) =>
            {
                if (await service.GetAsync(id, planId, ct) is null)
                    return Results.NotFound(new { error = "OperationPlanNotFound" });
                return Results.Ok(await service.EventsAsync(id, planId, ct));
            });

        routes.MapGet("/{id:guid}/target-preflight/events", async Task<IResult> (
            Guid id, IncrementalTargetPreflightService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "TargetPreflightNotFound" });
            return Results.Ok(await service.EventsAsync(id, ct));
        });

        routes.MapGet("/{id:guid}/review/events", async Task<IResult> (
            Guid id, IncrementalReviewService reviews, CancellationToken ct) =>
        {
            if (await reviews.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "ReviewNotFound" });
            return Results.Ok(await reviews.EventsAsync(id, ct));
        });

        routes.MapGet("/{id:guid}/events", async Task<IResult> (
            Guid id, IncrementalPreparationService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "PreparationNotFound" });
            return Results.Ok(await service.EventsAsync(id, ct));
        });
    }

    private static IResult OperationPlanError(IncrementalOperationPlanException ex) =>
        ex.Code is "InvalidOperationPlanRequest" or
            "InvalidExecutionAuthorizationRequest" or
            "ExplicitRiskAcknowledgmentRequired"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult ReviewError(IncrementalReviewException ex) =>
        ex.Code is "InvalidReviewContext" or "InvalidReviewDecision"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });
}

public sealed record PrepareIncrementalRequest(Guid SnapshotId, Guid DestinationConnectionId);
public sealed record IncrementalReviewDecisionRequest(
    string? Decision, string? ExpectedDryRunSha256,
    bool AcknowledgeSensitive, string? ReasonCode);
public sealed record RevokeIncrementalReviewRequest(
    string? ExpectedDryRunSha256, string? ReasonCode);
public sealed record LiveTargetPreflightRequest(
    string? ExpectedReviewSha256, IReadOnlyList<PlanSelectionInput>? Repositories);
public sealed record CreateOperationPlanRequest(string? ExpectedPreflightSha256);
public sealed record AuthorizeOperationPlanRequest(
    string? ExpectedManifestSha256, bool AcknowledgeBlockedOperations);
