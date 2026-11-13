using System.Security.Claims;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// #111: one authenticated, GET-only historical integrity receipt, not a
// current admission, provider call, F-035 acquisition or worker dispatch.
public static class GitIncrementalLaterCheckpointWorkpackHistoryEndpoints
{
    public static void MapGitIncrementalLaterCheckpointWorkpackHistoryEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/incremental-preparations/{id:guid}/operation-plans/" +
            "{planId:guid}/source-sync-checkpoints/{checkpointId:guid}/" +
            "later-workpacks/{workpackId:guid}/history",
            async Task<IResult> (Guid id, Guid planId, Guid checkpointId,
                Guid workpackId, Guid? targetObservationId,
                string? sourceRepositoryId, string? expectedTargetReceiptSha256,
                ClaimsPrincipal principal,
                PostgresGitIncrementalLaterCheckpointWorkpackHistory reader,
                CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(
                    ClaimTypes.NameIdentifier), out var actor) ||
                    actor == Guid.Empty)
                    return Results.Unauthorized();

                try
                {
                    var receipt = await reader.ReadAsync(
                        id, planId, checkpointId, workpackId,
                        targetObservationId ?? Guid.Empty,
                        sourceRepositoryId ?? "",
                        expectedTargetReceiptSha256 ?? "", ct);
                    return receipt is null
                        ? Results.NotFound(new {
                            error = "LaterCheckpointHistoryNotFound"
                        })
                        : Results.Ok(receipt);
                }
                catch (GitIncrementalLaterCheckpointHistoryException ex)
                {
                    return ex.Code == "LaterCheckpointHistoryInvalidRequest"
                        ? Results.BadRequest(new { error = ex.Code })
                        : Results.Conflict(new { error = ex.Code });
                }
            })
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("staging-sync");
    }
}
