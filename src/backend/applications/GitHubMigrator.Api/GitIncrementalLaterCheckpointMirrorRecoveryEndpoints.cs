using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// #122: explicit superadmin-only human review of *quarantined* v2
// source Git. Neither route reclaims a lease, moves a file, performs
// GitHub provider I/O or grants F-095/remote migration authority.
public static class GitIncrementalLaterCheckpointMirrorRecoveryEndpoints
{
    public static void MapGitIncrementalLaterCheckpointMirrorRecoveryEndpoints(
        this WebApplication app)
    {
        var group = app.MapGroup(
            "/api/incremental-preparations/{id:guid}/operation-plans/" +
            "{planId:guid}/source-sync-checkpoints/{checkpointId:guid}/" +
            "mirror-recovery")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute {
                Roles = "superadmin"
            })
            .RequireRateLimiting("staging-sync");

        // No caller-supplied ref, OID, local path, lease token or worker
        // identity. Never return the original opaque lease token.
        group.MapGet("/candidate", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            Guid originalBundleActorId, string? sourceRepositoryId,
            string? expectedTargetReceiptSha256,
            ClaimsPrincipal user,
            PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector inspector,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            Actor(user);
            var c = await inspector.ReadAsync(id, planId, checkpointId,
                targetObservationId, workpackId, sourceRepositoryId ?? "",
                expectedTargetReceiptSha256 ?? "",
                originalBundleActorId, ct);
            return Results.Ok(new {
                c.PreparationId, c.PlanId, c.CheckpointId,
                c.SourceSnapshotId, c.PhysicalMirrorId, c.WorkpackId,
                c.PhysicalState, c.ObjectCount, c.ObjectSetSha256,
                c.LeaseGeneration, c.LeaseEventCount, c.RenewedEventCount,
                c.LeaseEventHistorySha256, c.EvidenceSha256,
                reviewCandidateSha256 =
                    GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                        .ReviewCandidateSha256(c),
                canReAdmit = false, canMaterialize = false,
                canPush = false, canRetry = false,
                canDispatch = false, migrationComplete = false
            });
        }));

        // #129: independent second-review CANDIDATE, NOT a second
        // decision writer. The existing route group enforces changed-
        // password JWT and superadmin role. No caller-selected disk
        // path, Git OID, lease token, worker identity or reviewer GUID.
        // The physical inspection is separately OFF by default.
        group.MapGet("/second-review/candidate", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            Guid originalBundleActorId, string? sourceRepositoryId,
            string? expectedTargetReceiptSha256,
            ClaimsPrincipal user,
            PostgresGitIncrementalLaterCheckpointMirrorPostIsolationInspector inspector,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            var secondReviewerId = Actor(user);
            var checkedAssessment = await inspector.ReadAssessedAsync(
                id, planId, checkpointId, targetObservationId, workpackId,
                sourceRepositoryId ?? "",
                expectedTargetReceiptSha256 ?? "", originalBundleActorId, ct);
            var x = checkedAssessment.Inspection;
            if (secondReviewerId == x.FirstReviewerId ||
                secondReviewerId == x.OriginalBundleActorId ||
                secondReviewerId == x.OriginalWorkerId)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorSecondReviewIndependentReviewerRequired");

            // No original F-061 lease token, local directory path,
            // reviewer access token, or bearer credential in response.
            // Candidate is NOT a review decision or new-generation CAS.
            return Results.Ok(new {
                x.EvidenceVersion, x.PhysicalMirrorId, x.WorkpackId,
                x.CheckpointId, x.SourceSnapshotId,
                x.StagingEvidenceSha256, x.IsolationReceiptSha256,
                x.FirstReviewSha256, x.PreparedIntentSha256,
                x.ObjectSetSha256, x.ObjectCount, x.PhysicalState,
                x.ObservedAtUtc,
                checkedAssessment.AssessmentEvidenceSha256,
                checkedAssessment.SuggestedNextGeneration,
                checkedAssessment.SourceReviewedAtUtc,
                checkedAssessment.AssessedAtUtc,
                independentReviewerRequired = true,
                durableSecondReviewPersisted = false,
                canReAdmit = false, canMaterialize = false,
                canPush = false, canRetry = false, canDispatch = false,
                migrationComplete = false
            });
        }));

        group.MapPost("/decision", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            LaterCheckpointMirrorRecoveryDecisionRequest req,
            ClaimsPrincipal user,
            PostgresGitIncrementalLaterCheckpointMirrorRecoveryReviewJournal journal,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            var x = await journal.RecordAsync(
                id, planId, checkpointId,
                req.TargetObservationId, req.WorkpackId,
                req.SourceRepositoryId ?? "",
                req.ExpectedTargetReceiptSha256 ?? "",
                req.OriginalBundleActorId, Actor(user),
                req.Decision ?? "", req.ExpectedInspectionSha256 ?? "",
                ct);
            // This is a human judgment ONLY; never expose opaque F-061
            // lease tokens or imply approval to adopt/migrate Git.
            return Results.Ok(new {
                x.EvidenceVersion, x.PhysicalMirrorId, x.WorkpackId,
                x.CheckpointId, x.SourceSnapshotId,
                x.ReviewerId, x.Decision, x.InspectionEvidenceSha256,
                x.LeaseEventHistorySha256, x.EvidenceSha256,
                canReAdmit = false, canMaterialize = false,
                canPush = false, canRetry = false,
                canDispatch = false, migrationComplete = false
            });
        }));
    }

    private static Guid Actor(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier),
            out var id) && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException();

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (GitSourceSnapshotMirrorException ex)
        {
            return ex.Code.EndsWith("Invalid", StringComparison.Ordinal) ||
                   ex.Code.EndsWith("ScopeInvalid", StringComparison.Ordinal)
                ? Results.BadRequest(new { error = ex.Code })
                : Results.Conflict(new { error = ex.Code });
        }
        catch (GitSourceBundleException)
        {
            return Results.Conflict(new {
                error = "LaterCheckpointMirrorRecoveryBundleInvalid"
            });
        }
    }
}

public sealed record LaterCheckpointMirrorRecoveryDecisionRequest(
    Guid TargetObservationId, Guid WorkpackId,
    Guid OriginalBundleActorId, string? SourceRepositoryId,
    string? ExpectedTargetReceiptSha256, string? ExpectedInspectionSha256,
    string? Decision);
