using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class StagingEndpoints
{
    public static void MapStagingEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/staging")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        // Idempotent replay is essential: discovery may have been persisted before an
        // HTTP timeout or previous staging ingestion failure.
        routes.MapPost("/snapshots/{snapshotId:guid}/sync", async Task<IResult> (
            Guid snapshotId, StagingSyncService service, ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            try
            {
                var result = await service.SyncAsync(snapshotId, ct);
                loggerFactory.CreateLogger("Staging").LogInformation(
                    "Staging sync completed SnapshotId={SnapshotId} Source={SourceId} Observed={Observed} Added={Added} Changed={Changed} Missing={Missing}",
                    result.SnapshotId, result.SourceConnectionId, result.Observed,
                    result.Added, result.Changed, result.MarkedMissing);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "SnapshotNotFound" }); }
            catch (StagingSyncInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (StagingStaleSnapshotException)
            { return Results.Conflict(new { error = "StagingSnapshotOutOfOrder" }); }
        }).RequireRateLimiting("staging-sync");

        routes.MapGet("/snapshots/{snapshotId:guid}", async Task<IResult> (
            Guid snapshotId, StagingSyncService service, CancellationToken ct) =>
        {
            var state = await service.GetSyncAsync(snapshotId, ct);
            return state is null ? Results.NotFound(new { error = "StagingSyncNotFound" }) : Results.Ok(state);
        });

        routes.MapGet("/connections/{connectionId:guid}/summary", async (
            Guid connectionId, StagingSyncService service, CancellationToken ct) =>
            Results.Ok(await service.GetSummaryAsync(connectionId, ct)));
    }
}
