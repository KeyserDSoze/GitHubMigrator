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

        routes.MapGet("/{id:guid}/events", async Task<IResult> (
            Guid id, IncrementalPreparationService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "PreparationNotFound" });
            return Results.Ok(await service.EventsAsync(id, ct));
        });
    }
}

public sealed record PrepareIncrementalRequest(Guid SnapshotId, Guid DestinationConnectionId);
