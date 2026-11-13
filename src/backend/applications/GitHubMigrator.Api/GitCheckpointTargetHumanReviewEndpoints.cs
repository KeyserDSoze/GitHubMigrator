using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-097c.4c.0c: explicitly enabled, authenticated independent human
// checkpoint review. No GitHub GET, credentials, OIDs or destination write.
// Role and subject are claims from the authenticated JWT, not request data.
public static class GitCheckpointTargetHumanReviewEndpoints
{
    public static void MapGitCheckpointTargetHumanReviewEndpoints(
        this WebApplication app)
    {
        var group = app.MapGroup("/api/incremental-preparations/" +
            "{id:guid}/operation-plans/{planId:guid}/source-sync-checkpoints/" +
            "{checkpointId:guid}/target-human-review")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("staging-sync");

        // Return only a sealed optimistic-concurrency handle, not raw
        // refs/OIDs, source paths or a provider-access permission.
        group.MapGet("/candidate", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            string? sourceRepositoryId, ClaimsPrincipal principal,
            PostgresGitCheckpointTargetHumanReviewJournal journal,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            Actor(principal);
            var c = await journal.ReadAuthenticatedCandidateAsync(
                id, planId, checkpointId, sourceRepositoryId ?? "", ct);
            return Results.Ok(new {
                c.PreparationId, c.PlanId, c.CheckpointId,
                c.SourceSnapshotId, c.SourceRepositoryId,
                c.TargetRepositoryName, c.EvidenceSha256,
                canObserveTarget = false, canPush = false,
                canRetry = false, canDispatch = false,
                migrationComplete = false
            });
        }));

        group.MapPost("", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            CheckpointTargetHumanReviewRequest request,
            ClaimsPrincipal principal,
            PostgresGitCheckpointTargetHumanReviewJournal journal,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            var receipt = await journal.RecordAuthenticatedAsync(
                id, planId, checkpointId, request.SourceRepositoryId ?? "",
                Actor(principal), request.Decision ?? "",
                request.ExpectedCandidateEvidenceSha256 ?? "", ct);
            return Results.Ok(receipt);
        }));

        // Historical, immutable, verified audit readback; a persisted
        // approval still does not authorize F-031 or a GitHub GET.
        group.MapGet("", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            string? sourceRepositoryId, ClaimsPrincipal principal,
            PostgresGitCheckpointTargetHumanReviewJournal journal,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            Actor(principal);
            var receipt = await journal.InspectScopedAsync(
                id, planId, checkpointId, sourceRepositoryId ?? "", ct);
            return receipt is null
                ? Results.NotFound(new { error = "CheckpointHumanReviewNotFound" })
                : Results.Ok(receipt);
        }));
    }

    private static Guid Actor(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
            out var actor) && actor != Guid.Empty
            ? actor : throw new UnauthorizedAccessException();

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> work)
    {
        try { return await work(); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (GitCheckpointTargetHumanReviewException ex)
        {
            return ex.Code == "CheckpointHumanReviewInvalidRequest"
                ? Results.BadRequest(new { error = ex.Code })
                : Results.Conflict(new { error = ex.Code });
        }
    }
}

public sealed record CheckpointTargetHumanReviewRequest(
    string? SourceRepositoryId, string? Decision,
    string? ExpectedCandidateEvidenceSha256);
