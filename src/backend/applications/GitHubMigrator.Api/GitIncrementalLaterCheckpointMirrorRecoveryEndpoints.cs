using System.Security.Claims;
using System.Security.Cryptography;
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
            GitIncrementalLaterCheckpointMirrorSecondReviewActorPreflight actorPreflight,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            var reviewerSession = await actorPreflight.RequireSessionAsync(user);
            var secondReviewerId = reviewerSession.ActorId;
            var checkedAssessment = await inspector.ReadAssessedAsync(
                id, planId, checkpointId, targetObservationId, workpackId,
                sourceRepositoryId ?? "",
                expectedTargetReceiptSha256 ?? "", originalBundleActorId, ct,
                authenticatedSecondReviewerId: secondReviewerId);
            var x = checkedAssessment.Inspection;
            if (secondReviewerId == x.FirstReviewerId ||
                secondReviewerId == x.OriginalBundleActorId ||
                secondReviewerId == x.OriginalWorkerId)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorSecondReviewIndependentReviewerRequired");
            // A current superadmin may lose their role or have their
            // security stamp rotated during a long double Git fsck.
            // Revalidate identity before returning even a read-only
            // candidate. This still does NOT make a durable CAS.
            if (await actorPreflight.RequireSessionAsync(user) !=
                reviewerSession)
                throw new UnauthorizedAccessException();

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
                checkedAssessment.ApprovedPreviewEvidenceSha256,
                checkedAssessment.RejectedPreviewEvidenceSha256,
                checkedAssessment.PreviewReviewedAtUtc,
                independentReviewerRequired = true,
                durableSecondReviewPersisted = false,
                canReAdmit = false, canMaterialize = false,
                canPush = false, canRetry = false, canDispatch = false,
                migrationComplete = false
            });
        }));

        // #129: explicit POST for re-auditing a *proposed* human
        // decision. This is NOT /decision and NEVER persists a review,
        // consumes a JWT session, re-admits a lease or grants authority.
        // A future writer may only use independently rebuilt evidence
        // after an atomic, source-first, POSIX-fenced SQL commit.
        group.MapPost("/second-review/preflight", async Task<IResult> (
            Guid id, Guid planId, Guid checkpointId,
            LaterCheckpointMirrorSecondReviewPreflightRequest req,
            ClaimsPrincipal user,
            GitIncrementalLaterCheckpointMirrorSecondReviewActorPreflight actorPreflight,
            PostgresGitIncrementalLaterCheckpointMirrorPostIsolationInspector inspector,
            PostgresGitIncrementalLaterCheckpointMirrorSecondReviewSessionReadiness readiness,
            TimeProvider clock,
            CancellationToken ct) => await GuardAsync(async () =>
        {
            var reviewer = await actorPreflight.RequireSessionAsync(user);
            // #130: reject impossible one-use session/nonce and stale
            // client previews BEFORE the expensive POSIX-locked Git
            // double-fsck. A successful read-only preflight is never
            // a durable decision or an authorization to recover.
            var now = clock.GetUtcNow();
            if (req.DecisionNonce == Guid.Empty ||
                req.DecisionNonce == reviewer.SessionId ||
                req.TargetObservationId == Guid.Empty ||
                req.WorkpackId == Guid.Empty ||
                req.OriginalBundleActorId == Guid.Empty ||
                req.Decision is not (
                    GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                        .ApprovedForIndependentReview or
                    GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                        .RejectedForIndependentReview) ||
                !GitSourceBundleStorageCheck.ValidSha(
                    req.ExpectedTargetReceiptSha256) ||
                !GitSourceBundleStorageCheck.ValidSha(
                    req.ExpectedAssessmentEvidenceSha256) ||
                !GitSourceBundleStorageCheck.ValidSha(
                    req.ExpectedReviewEvidenceSha256) ||
                req.ReviewedAtUtc.Offset != TimeSpan.Zero ||
                req.ReviewedAtUtc != DateTimeOffset.FromUnixTimeMilliseconds(
                    req.ReviewedAtUtc.ToUnixTimeMilliseconds()) ||
                req.ReviewedAtUtc < reviewer.IssuedAtUtc ||
                req.ReviewedAtUtc >= reviewer.IssuedAtUtc.AddMinutes(5) ||
                req.ReviewedAtUtc > now ||
                now - req.ReviewedAtUtc > TimeSpan.FromMinutes(2) ||
                req.SourceRepositoryId is not { Length: >= 1 and <= 256 } ||
                string.IsNullOrWhiteSpace(req.SourceRepositoryId))
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorSecondReviewPreflightInvalid");

            // Fail closed on the *database* clock and known replay
            // before costly POSIX-locked double physical fsck. No
            // transaction/CAS and no session row is written here.
            await readiness.RequireUnusedAsync(
                reviewer.SessionId, req.DecisionNonce,
                reviewer.IssuedAtUtc, req.ReviewedAtUtc,
                Guid.Empty, ct);

            var fresh = await inspector.ReadAssessedAsync(
                id, planId, checkpointId, req.TargetObservationId,
                req.WorkpackId, req.SourceRepositoryId,
                req.ExpectedTargetReceiptSha256,
                req.OriginalBundleActorId, ct,
                authenticatedSecondReviewerId: reviewer.ActorId,
                requestedReviewAtUtc: req.ReviewedAtUtc,
                authenticatedReviewSessionId: reviewer.SessionId,
                decisionNonce: req.DecisionNonce);
            var selected = req.Decision ==
                GitIncrementalLaterCheckpointMirrorSecondReviewIntegrity
                    .ApprovedForIndependentReview
                ? fresh.ApprovedPreviewEvidenceSha256
                : fresh.RejectedPreviewEvidenceSha256;
            static bool SameDigest(string? a, string? b) =>
                GitSourceBundleStorageCheck.ValidSha(a) &&
                GitSourceBundleStorageCheck.ValidSha(b) &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(a!),
                    Convert.FromHexString(b!));
            if (!SameDigest(req.ExpectedAssessmentEvidenceSha256,
                    fresh.AssessmentEvidenceSha256) ||
                !SameDigest(req.ExpectedReviewEvidenceSha256, selected) ||
                fresh.PreviewReviewedAtUtc != req.ReviewedAtUtc ||
                await actorPreflight.RequireSessionAsync(user) != reviewer)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorSecondReviewPreflightChanged");

            // #130: bind the already re-audited, Identity-backed
            // non-authorizing preview to this login session and nonce.
            // Re-read the genuine DB clock and all three one-use
            // uniqueness scopes, now including the SERVER-derived
            // physical mirror. This still cannot consume a session:
            // only a future atomic source-locked writer can do that.
            var proposedAt = await readiness.RequireUnusedAsync(
                reviewer.SessionId, req.DecisionNonce,
                reviewer.IssuedAtUtc, req.ReviewedAtUtc,
                fresh.Inspection.PhysicalMirrorId, ct);
            if (proposedAt < req.ReviewedAtUtc ||
                proposedAt >= reviewer.IssuedAtUtc.AddMinutes(5))
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorSecondReviewPreflightExpired");
            var sessionIntent =
                GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
                    .Seal(fresh.Inspection.PhysicalMirrorId, reviewer.ActorId,
                        req.Decision, selected!, reviewer.SessionId,
                        req.DecisionNonce, reviewer.IssuedAtUtc,
                        req.ReviewedAtUtc, proposedAt);
            GitIncrementalLaterCheckpointMirrorSecondReviewSessionIntentIntegrity
                .Verify(sessionIntent);

            // #130: the final PostgreSQL-clock/replay read happens AFTER
            // the first post-fsck Identity recheck. Role removal, account
            // lockout or security-stamp rotation during that last query
            // must still refuse even this non-authorizing preflight.
            // This is NOT a durable session consumption or SQL CAS.
            if (await actorPreflight.RequireSessionAsync(user) != reviewer)
                throw new UnauthorizedAccessException();

            return Results.Ok(new {
                preflightMatched = true,
                reviewPreviewEvidenceSha256 = selected,
                readOnlySessionIntentSha256 = sessionIntent.EvidenceSha256,
                readOnlySessionIntentExpiresAtUtc = sessionIntent.ExpiresAtUtc,
                durableSessionIntentPersisted = false,
                fresh.AssessmentEvidenceSha256,
                fresh.PreviewReviewedAtUtc,
                durableSecondReviewPersisted = false,
                sessionConsumed = false,
                canReAdmit = false, canMaterialize = false,
                canPush = false, canRetry = false,
                canDispatch = false, migrationComplete = false
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


// #129 preflight is NOT the decision writer. Caller supplies only
// a candidate digest/timestamp from the authenticated GET and a fresh
// unique nonce; the server re-seals all scope and actor evidence.
public sealed record LaterCheckpointMirrorSecondReviewPreflightRequest(
    Guid TargetObservationId, Guid WorkpackId, Guid OriginalBundleActorId,
    string? SourceRepositoryId, string? ExpectedTargetReceiptSha256,
    string? ExpectedAssessmentEvidenceSha256,
    string? ExpectedReviewEvidenceSha256, string? Decision,
    DateTimeOffset ReviewedAtUtc, Guid DecisionNonce);
