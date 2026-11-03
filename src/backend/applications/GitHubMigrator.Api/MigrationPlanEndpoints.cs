using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class MigrationPlanEndpoints
{
    public static void MapMigrationPlanEndpoints(this WebApplication app)
    {
        app.MapPost("/api/migration-plans/preview", async Task<IResult> (
            MigrationPlanPreviewRequest request, MigrationPlanReviewService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.PreviewAsync(request.SnapshotId,
                    request.DestinationConnectionId, request.Repositories, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "PlanContextNotFound" });
            }
            catch (PlanInputException ex)
            {
                return Results.BadRequest(new { error = ex.Code });
            }
        })
        .RequireAuthorization("PasswordChanged")
        .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });
    }
}

public sealed record MigrationPlanPreviewRequest(
    Guid SnapshotId, Guid DestinationConnectionId,
    IReadOnlyList<PlanSelectionInput>? Repositories);
