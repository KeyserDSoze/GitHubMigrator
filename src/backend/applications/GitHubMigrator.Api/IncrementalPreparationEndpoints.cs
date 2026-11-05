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
