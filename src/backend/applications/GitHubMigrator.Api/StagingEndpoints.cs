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

        // Immutable per-snapshot delta evidence. Never take target-side
        // migration actions from these raw, unreviewed change records.
        routes.MapGet("/snapshots/{snapshotId:guid}/change-summary",
            async Task<IResult> (Guid snapshotId, StagingSyncService service,
                CancellationToken ct) =>
        {
            try
            {
                var summary = await service.GetChangeSummaryAsync(snapshotId, ct);
                return summary is null
                    ? Results.NotFound(new { error = "StagingSyncNotFound" })
                    : Results.Ok(summary);
            }
            catch (StagingSyncInputException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (StagingChangeJournalUnavailableException)
            { return Results.Conflict(new { error = "StagingChangeJournalUnavailable" }); }
            catch (StagingChangeIntegrityException)
            { return Results.Conflict(new { error = "StagingChangeIntegrityFailed" }); }
        });

        routes.MapGet("/snapshots/{snapshotId:guid}/changes/{assetType}",
            async Task<IResult> (Guid snapshotId, string assetType, string? after, int? take,
                StagingSyncService service, CancellationToken ct) =>
        {
            try
            {
                var page = await service.ListChangesAsync(
                    snapshotId, assetType, after, take ?? 50, ct);
                return page is null
                    ? Results.NotFound(new { error = "StagingSyncNotFound" })
                    : Results.Ok(page);
            }
            catch (StagingSyncInputException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (StagingChangeJournalUnavailableException)
            { return Results.Conflict(new { error = "StagingChangeJournalUnavailable" }); }
        });

        routes.MapGet("/connections/{connectionId:guid}/assets/{assetType}", async Task<IResult> (
            Guid connectionId, string assetType, string? after, int? take,
            StagingSyncService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.ListAssetsAsync(connectionId,
                    assetType, after, take ?? 50, ct));
            }
            catch (StagingSyncInputException ex)
            {
                return Results.BadRequest(new { error = ex.Code });
            }
        });

        routes.MapGet("/connections/{connectionId:guid}/summary", async (
            Guid connectionId, StagingSyncService service, CancellationToken ct) =>
            Results.Ok(await service.GetSummaryAsync(connectionId, ct)));
    }
}
