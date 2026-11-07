using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// F-066: superadmin-only operational observation; no raw queue records,
// source refs, credential providers, workers or GitHub write capability.
public static class IncrementalPreparationQueueHealthEndpoints
{
    public static void MapIncrementalPreparationQueueHealthEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/operations/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute {
                Roles = "superadmin"
            })
            .RequireRateLimiting("incremental-queue-health");

        routes.MapGet("/{sourceConnectionId:guid}/health",
            async Task<IResult> (
                Guid sourceConnectionId,
                IIncrementalPreparationQueueHealthReader reader,
                CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(
                        await reader.ReadAsync(sourceConnectionId, ct));
                }
                catch (IncrementalPreparationException ex) when
                    (ex.Code == "IncrementalQueueHealthDisabled")
                {
                    return Results.NotFound();
                }
                catch (IncrementalPreparationException ex) when
                    (ex.Code == "IncrementalQueueHealthSourceInvalid")
                {
                    return Results.BadRequest(new { error = ex.Code });
                }
            });
    }
}
