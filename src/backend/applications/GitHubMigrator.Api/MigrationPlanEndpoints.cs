using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class MigrationPlanEndpoints
{
    public static void MapMigrationPlanEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/migration-plans")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        routes.MapPost("/preview", async Task<IResult> (
            MigrationPlanPreviewRequest request, MigrationPlanReviewService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.PreviewAsync(request.SnapshotId,
                    request.DestinationConnectionId, request.Repositories, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "PlanContextNotFound" }); }
            catch (PlanInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        });

        routes.MapPost("/preflight", async Task<IResult> (
            MigrationPlanPreviewRequest request, GitHubTargetPreflightService service,
            CancellationToken ct) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                return Results.Ok(await service.CheckAsync(request.SnapshotId,
                    request.DestinationConnectionId, request.Repositories, deadline.Token));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "PlanContextNotFound" }); }
            catch (PlanInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (TargetPreflightInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.UnprocessableEntity(new { error = "TargetPreflightTimeout" });
            }
        }).RequireRateLimiting("external");

        routes.MapGet("/drafts/{snapshotId:guid}", async Task<IResult> (
            Guid snapshotId, Guid destinationConnectionId,
            MigrationPlanDraftService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.GetAsync(snapshotId, destinationConnectionId, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "PlanContextNotFound" }); }
            catch (PlanInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        });

        routes.MapPut("/drafts/{snapshotId:guid}", async Task<IResult> (
            Guid snapshotId, MigrationPlanDraftUpdate request,
            MigrationPlanDraftService service, ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            try
            {
                var draft = await service.SaveAsync(snapshotId, request.DestinationConnectionId,
                    request.ExpectedRevision, request.Repositories, ct);
                loggerFactory.CreateLogger("MigrationPlans").LogInformation(
                    "Plan draft saved SnapshotId={SnapshotId} DestinationId={DestinationId} Revision={Revision} Selections={Count}",
                    draft.SnapshotId, draft.DestinationConnectionId, draft.Revision, draft.Repositories.Count);
                return Results.Ok(draft);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "PlanContextNotFound" }); }
            catch (PlanInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (PlanRevisionConflictException) { return Results.Conflict(new { error = "PlanRevisionConflict" }); }
        });
    }
}

public sealed record MigrationPlanPreviewRequest(
    Guid SnapshotId, Guid DestinationConnectionId,
    IReadOnlyList<PlanSelectionInput>? Repositories);

public sealed record MigrationPlanDraftUpdate(
    Guid DestinationConnectionId, int ExpectedRevision,
    IReadOnlyList<PlanSelectionInput>? Repositories);
