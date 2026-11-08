using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class IncrementalPreparationEndpoints
{
    public static void MapIncrementalPreparationEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        routes.MapPost("", async Task<IResult> (
            PrepareIncrementalRequest request, ClaimsPrincipal principal,
            IncrementalPreparationService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            try
            {
                var run = await service.EnqueueAsync(
                    request.SnapshotId, request.DestinationConnectionId, actor, ct);
                return Results.Accepted($"/api/incremental-preparations/{run.Id}", run);
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationDependencyNotFound" }); }
            catch (IncrementalPreparationException ex) when (
                ex.Code is "IncrementalSnapshotSuperseded" or "IncrementalJournalUnavailable")
            { return Results.Conflict(new { error = ex.Code }); }
            catch (IncrementalPreparationException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (StagingChangeJournalUnavailableException)
            { return Results.Conflict(new { error = "IncrementalJournalUnavailable" }); }
            catch (StagingChangeIntegrityException)
            { return Results.Conflict(new { error = "IncrementalJournalMismatch" }); }
        }).RequireRateLimiting("staging-sync");

        routes.MapGet("/{id:guid}", async Task<IResult> (
            Guid id, IncrementalPreparationService service, CancellationToken ct) =>
        {
            var run = await service.GetAsync(id, ct);
            return run is null
                ? Results.NotFound(new { error = "PreparationNotFound" })
                : Results.Ok(run);
        });

        routes.MapGet("/{id:guid}/items", async Task<IResult> (
            Guid id, string? afterType, string? afterId, int? take,
            IncrementalPreparationService service, CancellationToken ct) =>
        {
            try
            {
                var page = await service.ListItemsAsync(id, afterType, afterId, take ?? 50, ct);
                return page is null
                    ? Results.NotFound(new { error = "PreparationNotFound" })
                    : Results.Ok(page);
            }
            catch (IncrementalPreparationException ex)
                when (ex.Code == "PreparationNotReady")
            { return Results.Conflict(new { error = ex.Code }); }
            catch (IncrementalPreparationException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
        });

        // F-027 dry-run is purely PostgreSQL-backed. It cannot contact,
        // approve execution on, or write to GitHub.
        routes.MapPost("/{id:guid}/dry-run", async Task<IResult> (
            Guid id, ClaimsPrincipal principal,
            IncrementalReviewService reviews, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            try { return Results.Ok(await reviews.DryRunAsync(id, actor, ct)); }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalReviewException ex)
            { return ReviewError(ex); }
        }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/review", async Task<IResult> (
            Guid id, IncrementalReviewService reviews, CancellationToken ct) =>
        {
            var review = await reviews.GetAsync(id, ct);
            return review is null
                ? Results.NotFound(new { error = "ReviewNotFound" })
                : Results.Ok(review);
        });

        routes.MapPost("/{id:guid}/review/decision", async Task<IResult> (
            Guid id, IncrementalReviewDecisionRequest request,
            ClaimsPrincipal principal, IncrementalReviewService reviews,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            if (request.Decision == "ApproveForPlanning" && !principal.IsInRole("superadmin"))
                return Results.Forbid();
            try
            {
                return Results.Ok(await reviews.DecideAsync(id, actor, request.Decision ?? "",
                    request.ExpectedDryRunSha256 ?? "", request.AcknowledgeSensitive,
                    request.ReasonCode, ct));
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalReviewException ex)
            { return ReviewError(ex); }
        }).RequireRateLimiting("incremental-review");

        routes.MapPost("/{id:guid}/review/revoke", async Task<IResult> (
            Guid id, RevokeIncrementalReviewRequest request,
            ClaimsPrincipal principal, IncrementalReviewService reviews,
            CancellationToken ct) =>
        {
            if (!principal.IsInRole("superadmin")) return Results.Forbid();
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
                return Results.Unauthorized();
            try
            {
                return Results.Ok(await reviews.RevokeAsync(id, actor,
                    request.ExpectedDryRunSha256 ?? "", request.ReasonCode ?? "", ct));
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalReviewException ex)
            { return ReviewError(ex); }
        }).RequireRateLimiting("incremental-review");

        // F-028: explicitly requested, fixed-host GitHub GET probes.
        // The result can only be Blocked/Unverified, never executable.
        routes.MapPost("/{id:guid}/target-preflight", async Task<IResult> (
            Guid id, LiveTargetPreflightRequest request, ClaimsPrincipal principal,
            IncrementalTargetPreflightService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor))
                return Results.Unauthorized();
            try
            {
                return Results.Ok(await service.CheckAsync(id, actor,
                    request.ExpectedReviewSha256 ?? "",
                    request.Repositories, ct));
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (PlanInputException ex)
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (IncrementalTargetPreflightException ex)
                when (ex.Code is "InvalidTargetPreflightRequest" or
                    "InvalidTargetRepositorySelection" or
                    "InvalidTargetPreflightEvidence")
            { return Results.BadRequest(new { error = ex.Code }); }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        routes.MapGet("/{id:guid}/target-preflight", async Task<IResult> (
            Guid id, IncrementalTargetPreflightService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.GetAsync(id, ct);
                return result is null
                    ? Results.NotFound(new { error = "TargetPreflightNotFound" })
                    : Results.Ok(result);
            }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        });

        // F-029: immutable, non-executable operational manifest. Only the
        // denied-only authorization audit can be written, never a target.
        routes.MapPost("/{id:guid}/operation-plans", async Task<IResult> (
            Guid id, CreateOperationPlanRequest request,
            ClaimsPrincipal principal, IncrementalOperationPlanService service,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor))
                return Results.Unauthorized();
            try
            {
                var plan = await service.CreateAsync(id, actor,
                    request.ExpectedPreflightSha256 ?? "", ct);
                return Results.Ok(plan);
            }
            catch (KeyNotFoundException)
            { return Results.NotFound(new { error = "PreparationNotFound" }); }
            catch (IncrementalOperationPlanException ex)
            { return OperationPlanError(ex); }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans", async Task<IResult> (
            Guid id, IncrementalOperationPlanService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ListAsync(id, ct)); }
            catch (IncrementalOperationPlanException ex)
            { return OperationPlanError(ex); }
            catch (IncrementalTargetPreflightException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}",
            async Task<IResult> (Guid id, Guid planId,
                IncrementalOperationPlanService service, CancellationToken ct) =>
            {
                try
                {
                    var plan = await service.GetAsync(id, planId, ct);
                    return plan is null
                        ? Results.NotFound(new { error = "OperationPlanNotFound" })
                        : Results.Ok(plan);
                }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-031: GET-only GitHub Git-data collection for a current F-029
        // mapping. The only POST side effect is immutable PostgreSQL evidence.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/target-ref-evidence",
            async Task<IResult> (Guid id, Guid planId,
                CollectTargetRefEvidenceRequest request, ClaimsPrincipal principal,
                GitHubTargetRefEvidenceService evidence, CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                        out var actor)) return Results.Unauthorized();
                try
                {
                    return Results.Ok(await evidence.CollectAsync(
                        id, planId, request.SourceRepositoryId ?? "", actor,
                        request.ExpectedManifestSha256 ?? "", ct));
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "OperationPlanNotFound" }); }
                catch (GitHubRefObservationException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubTargetRefEvidenceException ex)
                { return TargetRefEvidenceError(ex); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("external");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/target-ref-evidence",
            async Task<IResult> (Guid id, Guid planId,
                GitHubTargetRefEvidenceService evidence, CancellationToken ct) =>
            {
                try { return Results.Ok(await evidence.ListAsync(id, planId, ct)); }
                catch (GitHubTargetRefEvidenceException ex)
                { return TargetRefEvidenceError(ex); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/target-ref-evidence/{observationId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid observationId,
                string? after, int? take, GitHubTargetRefEvidenceService evidence,
                CancellationToken ct) =>
            {
                try
                {
                    var page = await evidence.PageAsync(id, planId, observationId,
                        after, take ?? 50, ct);
                    return page is null
                        ? Results.NotFound(new { error = "TargetRefEvidenceNotFound" })
                        : Results.Ok(page);
                }
                catch (GitHubTargetRefEvidenceException ex)
                { return TargetRefEvidenceError(ex); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-032 is a PostgreSQL-only immutable comparison of F-014 source
        // and F-031 GitHub GET snapshots. No network call or target write.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/ref-reconciliations",
            async Task<IResult> (Guid id, Guid planId,
                CreateGitRefReconciliationRequest request,
                ClaimsPrincipal principal, IncrementalGitRefReconciliationService service,
                CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                try
                {
                    return Results.Ok(await service.CreateAsync(id, planId,
                        request.SourceRepositoryId ?? "",
                        request.TargetObservationId,
                        request.ExpectedManifestSha256 ?? "",
                        request.ExpectedTargetEvidenceSha256 ?? "",
                        actor, ct));
                }
                catch (KeyNotFoundException ex)
                { return Results.NotFound(new { error = ex.Message }); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (DiscoveryRefManifestException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/ref-reconciliations",
            async Task<IResult> (Guid id, Guid planId,
                IncrementalGitRefReconciliationService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.ListAsync(id, planId, ct)); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/ref-reconciliations/{reconciliationId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid reconciliationId,
                string? after, int? take, IncrementalGitRefReconciliationService service,
                CancellationToken ct) =>
            {
                try
                {
                    var page = await service.PageAsync(id, planId,
                        reconciliationId, after, take ?? 50, ct);
                    return page is null
                        ? Results.NotFound(new { error = "RefReconciliationNotFound" })
                        : Results.Ok(page);
                }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-034: enqueue an evidence-only durable transfer workpack. No
        // endpoint can authorize a Git push, target write or deletion.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/ref-reconciliations/{reconciliationId:guid}/transfer-workpacks",
            async Task<IResult> (Guid id, Guid planId, Guid reconciliationId,
                CreateGitTransferWorkpackRequest request, ClaimsPrincipal principal,
                GitTransferWorkpackService service, CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                try
                {
                    var pack = await service.EnqueueAsync(
                        id, planId, reconciliationId,
                        request.ExpectedManifestSha256 ?? "",
                        request.ExpectedReconciliationSha256 ?? "",
                        actor, ct);
                    return Results.Accepted(
                        $"/api/incremental-preparations/{id}/operation-plans/{planId}/transfer-workpacks/{pack.Id}", pack);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "TransferEvidenceNotFound" }); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks",
            async Task<IResult> (Guid id, Guid planId,
                GitTransferWorkpackService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.ListAsync(id, planId, ct)); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                GitTransferWorkpackService service, CancellationToken ct) =>
            {
                try
                {
                    var pack = await service.GetAsync(id, planId, workpackId, ct);
                    return pack is null
                        ? Results.NotFound(new { error = "TransferWorkpackNotFound" })
                        : Results.Ok(pack);
                }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/events",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                GitTransferWorkpackService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.EventsAsync(id, planId, workpackId, ct)); }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "TransferWorkpackNotFound" }); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-035 local-only Git source export. No caller-supplied source URI,
        // filesystem path, Git arguments or destination GitHub credentials.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/source-bundle",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                CreateGitSourceBundleRequest request,
                GitSourceBundleService bundles, CancellationToken ct) =>
            {
                try
                {
                    var bundle = await bundles.AcquireAsync(id, planId, workpackId,
                        request.ExpectedReconciliationSha256 ?? "", ct);
                    return Results.Ok(bundle);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "BundleDependencyNotFound" }); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (DiscoveryRefManifestException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/source-bundle",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                GitSourceBundleService bundles, CancellationToken ct) =>
            {
                try
                {
                    var result = await bundles.GetAsync(id, planId, workpackId, ct);
                    return result is null
                        ? Results.NotFound(new { error = "SourceBundleNotFound" })
                        : Results.Ok(result);
                }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-043: create and immediately discard a one-repository GitHub
        // App token. The GitHub POST ONLY mints a short-lived credential:
        // no push, branch update or repository mutation is ever issued.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/remote-fences/{fenceId:guid}/github-app-scope",
            async Task<IResult> (Guid id, Guid planId, Guid fenceId,
                CreateGitHubAppWriteScopeRequest request,
                ClaimsPrincipal principal, GitHubAppWriteScopeService service,
                CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                if (request.AcknowledgementText !=
                    "I UNDERSTAND THIS TOKEN IS DISCARDED AND DOES NOT AUTHORIZE A PUSH")
                    return Results.BadRequest(new { error = "AppScopeAcknowledgementRequired" });
                try
                {
                    return Results.Ok(await service.InspectAsync(
                        id, planId, fenceId,
                        request.ExpectedFenceEvidenceSha256 ?? "", actor, ct));
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "AppScopeDependencyNotFound" }); }
                catch (GitHubAppWriteScopeException ex)
                { return AppScopeError(ex); }
                catch (GitHubRemoteRefFenceException ex)
                { return RemoteFenceError(ex); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
              .RequireRateLimiting("github-app-scope");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/github-app-scopes/{scopeId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid scopeId,
                GitHubAppWriteScopeService service, CancellationToken ct) =>
            {
                try
                {
                    var record = await service.GetAsync(id, planId, scopeId, ct);
                    return record is null
                        ? Results.NotFound(new { error = "AppScopeNotFound" })
                        : Results.Ok(record);
                }
                catch (GitHubAppWriteScopeException ex)
                { return AppScopeError(ex); }
                catch (GitHubRemoteRefFenceException ex)
                { return RemoteFenceError(ex); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/remote-fences/{fenceId:guid}/github-app-scopes",
            async Task<IResult> (Guid id, Guid planId, Guid fenceId,
                GitHubAppWriteScopeService service, CancellationToken ct) =>
            {
                try
                { return Results.Ok(await service.ListAsync(id, planId, fenceId, ct)); }
                catch (GitHubAppWriteScopeException ex)
                { return AppScopeError(ex); }
                catch (GitHubRemoteRefFenceException ex)
                { return RemoteFenceError(ex); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-044: read-only, deterministic, SHA-256-sealed candidate packet.
        // It rehashes physical Git bytes and pages the full reconciliation.
        // The response never contains a credential, Git command or write grant.
        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/github-app-scopes/{scopeId:guid}/create-only-packet",
            async Task<IResult> (Guid id, Guid planId, Guid scopeId,
                GitHubCreateOnlyPacketService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.PreviewAsync(id, planId, scopeId, ct)); }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "PacketDependencyNotFound" }); }
                catch (GitHubCreateOnlyPacketException ex)
                { return ex.Code == "PacketRequestInvalid"
                    ? Results.BadRequest(new { error = ex.Code })
                    : Results.Conflict(new { error = ex.Code }); }
                catch (GitHubAppWriteScopeException ex)
                { return AppScopeError(ex); }
                catch (GitHubRemoteRefFenceException ex)
                { return RemoteFenceError(ex); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
            }).RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
              .RequireRateLimiting("github-create-packet");

        // F-042: fresh GitHub GET-only ref fence. This does not authorize
        // transport, does not create a remote ref and never issues a push.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/git-rehearsals/{rehearsalId:guid}/remote-fences",
            async Task<IResult> (Guid id, Guid planId, Guid rehearsalId,
                CreateGitHubRemoteRefFenceRequest request, ClaimsPrincipal principal,
                GitHubRemoteRefFenceService service, CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                try
                {
                    var proof = await service.CheckAsync(id, planId, rehearsalId,
                        request.ExpectedRehearsalEvidenceSha256 ?? "", actor, ct);
                    return Results.Ok(proof);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "RemoteFenceDependencyNotFound" }); }
                catch (GitHubRemoteRefFenceException ex)
                { return RemoteFenceError(ex); }
                catch (GitHubRefObservationException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("github-remote-fence");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/remote-fences/{fenceId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid fenceId,
                GitHubRemoteRefFenceService service, CancellationToken ct) =>
            {
                try
                {
                    var value = await service.GetAsync(id, planId, fenceId, ct);
                    return value is null
                        ? Results.NotFound(new { error = "RemoteFenceNotFound" })
                        : Results.Ok(value);
                }
                catch (GitHubRemoteRefFenceException ex)
                { return RemoteFenceError(ex); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/git-rehearsals/{rehearsalId:guid}/remote-fences",
            async Task<IResult> (Guid id, Guid planId, Guid rehearsalId,
                GitHubRemoteRefFenceService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.ListAsync(id, planId, rehearsalId, ct)); }
                catch (GitHubRemoteRefFenceException ex)
                { return RemoteFenceError(ex); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-041: runs an explicitly enabled, OFFLINE Git import rehearsal
        // against a disposable local bare repo. NEVER writes to GitHub.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/import-intents/{intentId:guid}/git-rehearsals",
            async Task<IResult> (Guid id, Guid planId, Guid intentId,
                CreateGitImportRehearsalRequest request, ClaimsPrincipal principal,
                GitImportRehearsalService service, CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                try
                {
                    var evidence = await service.RehearseAsync(id, planId, intentId,
                        request.ExpectedIntentEvidenceSha256 ?? "", actor, ct);
                    return Results.Accepted(
                        $"/api/incremental-preparations/{id}/operation-plans/{planId}/git-rehearsals/{evidence.Id}",
                        evidence);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "GitRehearsalDependencyNotFound" }); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("git-rehearsal");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/git-rehearsals/{rehearsalId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid rehearsalId,
                GitImportRehearsalService service, CancellationToken ct) =>
            {
                try
                {
                    var record = await service.GetAsync(id, planId, rehearsalId, ct);
                    return record is null
                        ? Results.NotFound(new { error = "GitRehearsalNotFound" })
                        : Results.Ok(record);
                }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/git-rehearsals",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                GitImportRehearsalService service, CancellationToken ct) =>
            {
                try
                { return Results.Ok(await service.ListAsync(id, planId, workpackId, ct)); }
                catch (GitImportRehearsalException ex)
                { return GitRehearsalError(ex); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-040: two-person, expiring acknowledgment of a non-destructive
        // Git REF CREATE plan. It is NOT a GitHub write authorization.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/import-readiness/{readinessId:guid}/intents",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                Guid readinessId, CreateGitHubImportIntentRequest request,
                ClaimsPrincipal principal, GitHubImportIntentService service,
                CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                try
                {
                    var intent = await service.CreateAsync(id, planId,
                        workpackId, readinessId,
                        request.ExpectedReadinessSha256 ?? "", actor, ct);
                    return Results.Accepted(
                        $"/api/incremental-preparations/{id}/operation-plans/{planId}/import-intents/{intent.Id}",
                        intent);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "ImportIntentDependencyNotFound" }); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("import-intent");

        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/import-intents/{intentId:guid}/decision",
            async Task<IResult> (Guid id, Guid planId, Guid intentId,
                DecideGitHubImportIntentRequest request, ClaimsPrincipal principal,
                GitHubImportIntentService service, CancellationToken ct) =>
            {
                // Distinct superadmin is required. Even an Acknowledged result
                // is a human review record, NEVER an execution authorization.
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                if (request.Decision == "Acknowledge" &&
                    request.AcknowledgementText !=
                        "I UNDERSTAND THIS DOES NOT AUTHORIZE A GITHUB PUSH")
                    return Results.BadRequest(new { error = "ImportIntentAcknowledgementRequired" });
                try
                {
                    return Results.Ok(await service.DecideAsync(
                        id, planId, intentId, actor,
                        request.ExpectedReadinessSha256 ?? "",
                        request.Decision ?? "", ct));
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "ImportIntentNotFound" }); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
              .RequireRateLimiting("import-intent");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/import-intents",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                GitHubImportIntentService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.ListAsync(id, planId, workpackId, ct)); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/import-intents/{intentId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid intentId,
                GitHubImportIntentService service, CancellationToken ct) =>
            {
                try
                {
                    var intent = await service.GetAsync(id, planId, intentId, ct);
                    return intent is null ? Results.NotFound(new { error = "ImportIntentNotFound" })
                        : Results.Ok(intent);
                }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/import-intents/{intentId:guid}/events",
            async Task<IResult> (Guid id, Guid planId, Guid intentId,
                GitHubImportIntentService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.EventsAsync(id, planId, intentId, ct)); }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "ImportIntentNotFound" }); }
                catch (GitHubImportIntentException ex)
                { return ImportIntentError(ex); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-039: check a SOURCE bundle and GET-only GitHub destination
        // metadata; returns a non-authorizing, immutable PostgreSQL review.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/import-readiness",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                CreateGitHubImportReadinessRequest request, ClaimsPrincipal principal,
                GitHubImportReadinessService service, CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                try
                {
                    var review = await service.CheckAsync(
                        id, planId, workpackId,
                        request.ExpectedReconciliationSha256 ?? "",
                        request.ExpectedBundleSha256 ?? "", actor, ct);
                    return Results.Ok(review);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "ImportReadinessDependencyNotFound" }); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("import-readiness");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/import-readiness",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                GitHubImportReadinessService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.ListAsync(id, planId, workpackId, ct)); }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/import-readiness/{reviewId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid reviewId,
                GitHubImportReadinessService service, CancellationToken ct) =>
            {
                try
                {
                    var result = await service.GetAsync(id, planId, reviewId, ct);
                    return result is null
                        ? Results.NotFound(new { error = "ImportReadinessNotFound" })
                        : Results.Ok(result);
                }
                catch (GitHubImportReadinessException ex)
                { return ImportReadinessError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-038: live disk attestation is separate from the immutable DB
        // proof. No source/target API calls, no GitHub writes and no path input.
        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/source-bundle/storage-integrity",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                GitSourceBundleService bundles, CancellationToken ct) =>
            {
                try
                {
                    var check = await bundles.VerifyStoredAsync(
                        id, planId, workpackId, ct);
                    return check is null
                        ? Results.NotFound(new { error = "SourceBundleNotFound" })
                        : Results.Ok(check);
                }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        // F-036: exact F-014 source snapshot -> HTTPS dev.azure.com read-only
        // Git fetch -> F-035 verified bundle; no caller-supplied URI/PAT/path.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/source-bundle/fetch-azure",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                CreateGitSourceBundleRequest request,
                AzureGitSourceAcquisitionService acquisitions,
                IConfiguration config, CancellationToken ct) =>
            {
                // When durable acquisition is enabled, no synchronous fetch
                // may bypass the leased worker or its global concurrency cap.
                if (config.GetValue("AzureGitQueue:Enabled", false))
                    return Results.Conflict(new { error = "UseAzureGitAcquisitionQueue" });
                try
                {
                    var result = await acquisitions.FetchAndBundleAsync(
                        id, planId, workpackId,
                        request.ExpectedReconciliationSha256 ?? "", ct);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "AzureGitSourceDependencyNotFound" }); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (DiscoveryRefManifestException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("incremental-review");

        // F-037: enqueue instead of performing remote network I/O on the
        // HTTP request thread. One immutable queued source job per workpack.
        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/transfer-workpacks/{workpackId:guid}/source-acquisitions",
            async Task<IResult> (Guid id, Guid planId, Guid workpackId,
                CreateGitSourceBundleRequest request, ClaimsPrincipal principal,
                IConfiguration config, AzureGitFetchJobService service,
                CancellationToken ct) =>
            {
                if (!config.GetValue("Jobs:Enabled", true) ||
                    !config.GetValue("GitSourceBundles:Enabled", false) ||
                    !config.GetValue("AzureGitFetch:Enabled", false) ||
                    !config.GetValue("AzureGitQueue:Enabled", false))
                    return Results.Conflict(new { error = "AzureGitQueueDisabled" });
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor)) return Results.Unauthorized();
                try
                {
                    var job = await service.EnqueueAsync(id, planId, workpackId,
                        request.ExpectedReconciliationSha256 ?? "", actor, ct);
                    return Results.Accepted(
                        $"/api/incremental-preparations/{id}/operation-plans/{planId}/source-acquisitions/{job.Id}",
                        job);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "AzureGitAcquisitionDependencyNotFound" }); }
                catch (AzureGitFetchJobException ex)
                { return AzureGitQueueError(ex); }
                catch (GitTransferWorkpackException ex)
                { return TransferWorkpackError(ex); }
                catch (GitSourceBundleException ex)
                { return BundleError(ex); }
                catch (IncrementalGitRefReconciliationException ex)
                { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/source-acquisitions",
            async Task<IResult> (Guid id, Guid planId,
                AzureGitFetchJobService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.ListAsync(id, planId, ct)); }
                catch (AzureGitFetchJobException ex) { return AzureGitQueueError(ex); }
                catch (GitTransferWorkpackException ex) { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex) { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex) { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/source-acquisitions/{jobId:guid}",
            async Task<IResult> (Guid id, Guid planId, Guid jobId,
                AzureGitFetchJobService service, CancellationToken ct) =>
            {
                try
                {
                    var job = await service.GetAsync(id, planId, jobId, ct);
                    return job is null ? Results.NotFound(new { error = "AzureGitFetchJobNotFound" })
                        : Results.Ok(job);
                }
                catch (AzureGitFetchJobException ex) { return AzureGitQueueError(ex); }
                catch (GitTransferWorkpackException ex) { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex) { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex) { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/source-acquisitions/{jobId:guid}/events",
            async Task<IResult> (Guid id, Guid planId, Guid jobId,
                AzureGitFetchJobService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.EventsAsync(id, planId, jobId, ct)); }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "AzureGitFetchJobNotFound" }); }
                catch (AzureGitFetchJobException ex) { return AzureGitQueueError(ex); }
                catch (GitTransferWorkpackException ex) { return TransferWorkpackError(ex); }
                catch (IncrementalGitRefReconciliationException ex) { return ReconciliationError(ex); }
                catch (GitHubTargetRefEvidenceException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (IncrementalOperationPlanException ex) { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            });

        routes.MapPost("/{id:guid}/operation-plans/{planId:guid}/execution-authorization",
            async Task<IResult> (Guid id, Guid planId,
                AuthorizeOperationPlanRequest request, ClaimsPrincipal principal,
                IncrementalOperationPlanService service, CancellationToken ct) =>
            {
                if (!principal.IsInRole("superadmin")) return Results.Forbid();
                if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
                        out var actor))
                    return Results.Unauthorized();
                try
                {
                    // Always denied with a safe code; the attempt is audited.
                    var attempt = await service.RequestAuthorizationAsync(
                        id, planId, actor, request.ExpectedManifestSha256 ?? "",
                        request.AcknowledgeBlockedOperations, ct);
                    return Results.Conflict(attempt);
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "OperationPlanNotFound" }); }
                catch (IncrementalOperationPlanException ex)
                { return OperationPlanError(ex); }
                catch (IncrementalTargetPreflightException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            }).RequireRateLimiting("incremental-review");

        routes.MapGet("/{id:guid}/operation-plans/{planId:guid}/authorization-attempts",
            async Task<IResult> (Guid id, Guid planId,
                IncrementalOperationPlanService service, CancellationToken ct) =>
            {
                if (await service.GetAsync(id, planId, ct) is null)
                    return Results.NotFound(new { error = "OperationPlanNotFound" });
                return Results.Ok(await service.EventsAsync(id, planId, ct));
            });

        routes.MapGet("/{id:guid}/target-preflight/events", async Task<IResult> (
            Guid id, IncrementalTargetPreflightService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "TargetPreflightNotFound" });
            return Results.Ok(await service.EventsAsync(id, ct));
        });

        routes.MapGet("/{id:guid}/review/events", async Task<IResult> (
            Guid id, IncrementalReviewService reviews, CancellationToken ct) =>
        {
            if (await reviews.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "ReviewNotFound" });
            return Results.Ok(await reviews.EventsAsync(id, ct));
        });

        routes.MapGet("/{id:guid}/events", async Task<IResult> (
            Guid id, IncrementalPreparationService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(id, ct) is null)
                return Results.NotFound(new { error = "PreparationNotFound" });
            return Results.Ok(await service.EventsAsync(id, ct));
        });
    }

    private static IResult AzureGitQueueError(AzureGitFetchJobException ex) =>
        ex.Code == "InvalidAzureGitFetchJobRequest"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult AppScopeError(GitHubAppWriteScopeException ex) =>
        ex.Code is "AppScopeRequestInvalid" or "AppScopeInspectionInvalid"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult RemoteFenceError(GitHubRemoteRefFenceException ex) =>
        ex.Code == "RemoteFenceRequestInvalid"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult GitRehearsalError(GitImportRehearsalException ex) =>
        ex.Code is "GitRehearsalRequestInvalid" or "GitRehearsalEvidenceInvalid"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult ImportIntentError(GitHubImportIntentException ex) =>
        ex.Code is "InvalidImportIntentRequest" or "InvalidImportIntentDecision"
            or "ImportPolicyInvalidDestination"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult ImportReadinessError(GitHubImportReadinessException ex) =>
        ex.Code is "InvalidImportReadinessRequest" or "ImportReadinessProbeInvalid"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult BundleError(GitSourceBundleException ex) =>
        ex.Code is "InvalidBundleRequest" or "InvalidBundleRefs"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult TransferWorkpackError(GitTransferWorkpackException ex) =>
        ex.Code == "InvalidTransferWorkpackRequest"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult ReconciliationError(
        IncrementalGitRefReconciliationException ex) =>
        ex.Code is "InvalidReconciliationRequest" or "InvalidReconciliationPage"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult TargetRefEvidenceError(
        GitHubTargetRefEvidenceException ex) =>
        ex.Code is "InvalidRefEvidenceRequest" or "InvalidRefEvidencePage"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult OperationPlanError(IncrementalOperationPlanException ex) =>
        ex.Code is "InvalidOperationPlanRequest" or
            "InvalidExecutionAuthorizationRequest" or
            "ExplicitRiskAcknowledgmentRequired"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });

    private static IResult ReviewError(IncrementalReviewException ex) =>
        ex.Code is "InvalidReviewContext" or "InvalidReviewDecision"
            ? Results.BadRequest(new { error = ex.Code })
            : Results.Conflict(new { error = ex.Code });
}

public sealed record PrepareIncrementalRequest(Guid SnapshotId, Guid DestinationConnectionId);
public sealed record IncrementalReviewDecisionRequest(
    string? Decision, string? ExpectedDryRunSha256,
    bool AcknowledgeSensitive, string? ReasonCode);
public sealed record RevokeIncrementalReviewRequest(
    string? ExpectedDryRunSha256, string? ReasonCode);
public sealed record LiveTargetPreflightRequest(
    string? ExpectedReviewSha256, IReadOnlyList<PlanSelectionInput>? Repositories);
public sealed record CreateOperationPlanRequest(string? ExpectedPreflightSha256);
public sealed record AuthorizeOperationPlanRequest(
    string? ExpectedManifestSha256, bool AcknowledgeBlockedOperations);

public sealed record CollectTargetRefEvidenceRequest(
    string? ExpectedManifestSha256, string? SourceRepositoryId);

public sealed record CreateGitRefReconciliationRequest(
    string? ExpectedManifestSha256, string? SourceRepositoryId,
    Guid TargetObservationId, string? ExpectedTargetEvidenceSha256);

public sealed record CreateGitTransferWorkpackRequest(
    string? ExpectedManifestSha256, string? ExpectedReconciliationSha256);

public sealed record CreateGitSourceBundleRequest(string? ExpectedReconciliationSha256);

public sealed record CreateGitHubImportReadinessRequest(
    string? ExpectedReconciliationSha256, string? ExpectedBundleSha256);

public sealed record CreateGitHubImportIntentRequest(string? ExpectedReadinessSha256);
public sealed record DecideGitHubImportIntentRequest(
    string? ExpectedReadinessSha256, string? Decision, string? AcknowledgementText);

public sealed record CreateGitImportRehearsalRequest(
    string? ExpectedIntentEvidenceSha256);

public sealed record CreateGitHubRemoteRefFenceRequest(
    string? ExpectedRehearsalEvidenceSha256);

public sealed record CreateGitHubAppWriteScopeRequest(
    string? ExpectedFenceEvidenceSha256, string? AcknowledgementText);
