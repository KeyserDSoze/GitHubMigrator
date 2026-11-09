using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// #109 later F-031: explicit independently authorized fixed-host GET-only
// provider observation. POST only writes an immutable PostgreSQL receipt
// and audit; never grants write, retry, dispatch or completion authority.
public static class GitCheckpointTargetObservationEndpoints
{
    public static void MapGitCheckpointTargetObservationEndpoints(
        this WebApplication app)
    {
        var group = app.MapGroup(
            "/api/incremental-preparations/{id:guid}/operation-plans/" +
            "{planId:guid}/source-sync-checkpoints/{checkpointId:guid}/" +
            "target-ref-observations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("external");

        // A real GitHub GET is possible ONLY if BOTH the independent
        // explicit switches and all #106/#108/current-source conditions
        // pass; never accept actor UUID, token, URL, OIDs, org or target.
        group.MapPost("", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            CheckpointTargetObservationRequest request,
            ClaimsPrincipal principal,
            PostgresGitCheckpointTargetObservationJournal journal,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            var receipt = await journal.CollectAsync(
                id, planId, checkpointId, request.SourceRepositoryId ?? "",
                Actor(principal), request.ExpectedCandidateEvidenceSha256 ?? "",
                request.ExpectedHumanReviewEvidenceSha256 ?? "", ct);
            return Results.Ok(Projection(receipt));
        }));

        // Historical readback verifies full receipt SHA, target Git ref
        // hash and the append-only audit event. Never re-enables F-029.
        group.MapGet("", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            string? sourceRepositoryId, ClaimsPrincipal principal,
            PostgresGitCheckpointTargetObservationJournal journal,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            Actor(principal);
            var saved = await journal.ReadScopedAsync(id, planId,
                checkpointId, sourceRepositoryId ?? "", ct);
            return saved is null
                ? Results.NotFound(new { error = "CheckpointTargetObservationNotFound" })
                : Results.Ok(Projection(saved));
        }));
    }

    private static Guid Actor(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
            out var actor) && actor != Guid.Empty
            ? actor : throw new UnauthorizedAccessException();

    private static object Projection(GitCheckpointTargetObservation proof) =>
        new {
            proof.Id, proof.EvidenceVersion, proof.PreparationId,
            proof.PlanId, proof.CheckpointId, proof.SourceSnapshotId,
            proof.SourceRepositoryId, proof.TargetRepositoryName,
            proof.Organization, proof.ObjectFormat, proof.ReviewedBy,
            proof.CandidateEvidenceSha256, proof.HumanReviewEvidenceSha256,
            proof.TargetEvidenceSha256, proof.ObservedAtUtc, proof.RefCount,
            proof.EvidenceSha256,
            canObserveTarget = false, canPush = false, canRetry = false,
            canDispatch = false, migrationComplete = false
        };

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> work)
    {
        try { return await work(); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (GitCheckpointTargetObservationException ex)
        {
            return ex.Code == "CheckpointTargetObservationInvalidRequest"
                ? Results.BadRequest(new { error = ex.Code })
                : Results.Conflict(new { error = ex.Code });
        }
    }
}

public sealed record CheckpointTargetObservationRequest(
    string? SourceRepositoryId, string? ExpectedCandidateEvidenceSha256,
    string? ExpectedHumanReviewEvidenceSha256);
