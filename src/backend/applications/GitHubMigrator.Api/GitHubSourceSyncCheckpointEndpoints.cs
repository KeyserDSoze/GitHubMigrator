using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public sealed record RecordSourceSyncCheckpointRequest(
    string? ExpectedManifestSha256, Guid? ExpectedPreviousCheckpointId);

// F-090 has no provider calls, no remote Git writes, no background worker.
// POST is explicit and only appends ONE verified F-025 source epoch.
public static class GitHubSourceSyncCheckpointEndpoints
{
    public static void MapGitHubSourceSyncCheckpointEndpoints(
        this WebApplication app)
    {
        var group = app.MapGroup("/api/incremental-preparations/" +
            "{id:guid}/operation-plans/{planId:guid}/source-sync-checkpoints")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("staging-sync");

        group.MapGet("", async Task<IResult> (
            Guid id, Guid planId, Guid? after, int? take,
            IGitHubSourceSyncCheckpointJournal journal,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await journal.ListAsync(
                id, planId, after, take ?? 10, ct)); }
            catch (GitHubSourceSyncCheckpointException ex)
            { return Failure(ex); }
        });

        group.MapGet("/{checkpointId:guid}/ref-changes",
            async Task<IResult> (Guid id, Guid planId,
                Guid checkpointId, string? after, int? take,
                IGitHubSourceSyncCheckpointJournal journal,
                CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await journal.ListRefChangesAsync(
                    id, planId, checkpointId, after, take ?? 50, ct));
            }
            catch (GitHubSourceSyncCheckpointException ex)
            { return Failure(ex); }
        });

        group.MapGet("/{checkpointId:guid}/target-ref-review/{targetObservationId:guid}",
            async Task<IResult> (Guid id, Guid planId,
                Guid checkpointId, Guid targetObservationId,
                string? sourceRepositoryId, string? expectedTargetEvidenceSha256,
                string? after, int? take,
                IGitHubSourceSyncCheckpointJournal journal,
                CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await journal.ReviewTargetRefNamesAsync(
                    id, planId, checkpointId, targetObservationId,
                    sourceRepositoryId ?? "",
                    expectedTargetEvidenceSha256 ?? "",
                    after, take ?? 50, ct));
            }
            catch (GitHubSourceSyncCheckpointException ex)
            { return Failure(ex); }
        });

        group.MapPost("/{snapshotId:guid}", async Task<IResult> (
            Guid id, Guid planId, Guid snapshotId,
            RecordSourceSyncCheckpointRequest request,
            ClaimsPrincipal principal,
            IGitHubSourceSyncCheckpointJournal journal,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(
                    ClaimTypes.NameIdentifier), out var actor) ||
                actor == Guid.Empty)
                return Results.Unauthorized();
            try
            {
                return Results.Ok(await journal.CaptureAsync(
                    id, planId, snapshotId,
                    request.ExpectedPreviousCheckpointId,
                    request.ExpectedManifestSha256 ?? "", actor, ct));
            }
            catch (GitHubSourceSyncCheckpointException ex)
            { return Failure(ex); }
        });
    }

    private static IResult Failure(GitHubSourceSyncCheckpointException ex)
        => ex.Code is "SourceSyncCheckpointInvalidRequest" or
            "SourceSyncCheckpointInvalidPage" or
            "SourceSyncCheckpointCursorInvalid" or
            "SourceSyncCheckpointRefChangesInvalid" or
            "SourceSyncCheckpointTargetReviewInvalid"
            ? Results.BadRequest(new { error = ex.Code })
            : ex.Code is "SourceSyncCheckpointPlanNotFound" or
                "SourceSyncCheckpointNotFound" or
                "SourceSyncCheckpointTargetObservationNotFound"
                ? Results.NotFound(new { error = ex.Code })
                : Results.Conflict(new { error = ex.Code });
}
