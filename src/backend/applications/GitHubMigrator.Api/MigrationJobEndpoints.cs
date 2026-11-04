using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class MigrationJobEndpoints
{
    public static void MapMigrationJobEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/jobs")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        routes.MapPost("/staging", async Task<IResult> (
            StageSnapshotJobRequest request, ClaimsPrincipal principal,
            MigrationJobService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
                return Results.Unauthorized();
            try
            {
                var job = await service.EnqueueStageAsync(request.SnapshotId, actorId, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "SnapshotNotFound" }); }
            catch (MigrationJobInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireRateLimiting("staging-sync");

        routes.MapPost("/discovery", async Task<IResult> (
            DiscoverSourceJobRequest request, ClaimsPrincipal principal,
            MigrationJobService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
                return Results.Unauthorized();
            try
            {
                var job = await service.EnqueueDiscoveryAsync(
                    request.ConnectionId, actorId, request.RequestId, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "ConnectionNotFound" }); }
            catch (MigrationJobInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireRateLimiting("discovery-queue");

        routes.MapGet("/{jobId:guid}", async Task<IResult> (
            Guid jobId, MigrationJobService service, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            return job is null ? Results.NotFound(new { error = "JobNotFound" }) : Results.Ok(job);
        });

        routes.MapGet("/{jobId:guid}/events", async Task<IResult> (
            Guid jobId, MigrationJobService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(jobId, ct) is null)
                return Results.NotFound(new { error = "JobNotFound" });
            return Results.Ok(await service.EventsAsync(jobId, ct));
        });

        routes.MapGet("/connections/{connectionId:guid}", async (
            Guid connectionId, MigrationJobService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(connectionId, ct)));
    }
}

public sealed record StageSnapshotJobRequest(Guid SnapshotId);
public sealed record DiscoverSourceJobRequest(Guid ConnectionId, Guid RequestId);
