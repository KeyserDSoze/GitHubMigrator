using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// #109 phase 1: read-only eligibility preview; not GitHub GET authority.
public static class GitCheckpointTargetReadPreflightEndpoints
{
    public static void MapGitCheckpointTargetReadPreflightEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/incremental-preparations/{id:guid}/operation-plans/" +
            "{planId:guid}/source-sync-checkpoints/{checkpointId:guid}/target-read-preflight",
            async Task<IResult> (
                Guid id, Guid planId, Guid checkpointId,
                string? sourceRepositoryId, string? expectedCandidateEvidenceSha256,
                string? expectedHumanReviewEvidenceSha256, ClaimsPrincipal principal,
                PostgresGitCheckpointTargetReadPreflight preflight,
                CancellationToken ct) =>
            {
                if (!Guid.TryParse(
                        principal.FindFirstValue(ClaimTypes.NameIdentifier),
                        out var reviewerId) || reviewerId == Guid.Empty)
                    return Results.Unauthorized();
                try
                {
                    var witness = await preflight.ReadAsync(
                        id, planId, checkpointId, sourceRepositoryId ?? "",
                        reviewerId, expectedCandidateEvidenceSha256 ?? "",
                        expectedHumanReviewEvidenceSha256 ?? "", ct);
                    return Results.Ok(new {
                        witness.EvidenceVersion, witness.PreparationId,
                        witness.PlanId, witness.CheckpointId,
                        witness.SourceSnapshotId, witness.SourceRepositoryId,
                        witness.TargetRepositoryName, witness.Organization,
                        witness.ObjectFormat, witness.SourceRefCount,
                        witness.CandidateEvidenceSha256,
                        witness.HumanReviewEvidenceSha256,
                        witness.ValidatedAtUtc, witness.ExpiresAtUtc,
                        witness.EvidenceSha256,
                        providerReadAuthorized = false,
                        canObserveTarget = false, canPush = false,
                        canRetry = false, canDispatch = false,
                        migrationComplete = false
                    });
                }
                catch (GitCheckpointTargetReadPreflightException ex)
                {
                    return ex.Code == "CheckpointTargetReadPreflightInvalidRequest"
                        ? Results.BadRequest(new { error = ex.Code })
                        : Results.Conflict(new { error = ex.Code });
                }
            })
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("staging-sync");
    }
}
