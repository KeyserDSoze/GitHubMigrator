using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// The only F-045 external operations touch PostgreSQL. No HTTP client,
// Git executable, write token or remote destination is injected here.
public static class GitHubTargetPreviewLeaseEndpoints
{
    public static void MapGitHubTargetPreviewLeaseEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-preview-lease");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}";

        routes.MapPost(root + "/target-preview-leases",
            (Guid id, Guid planId, Guid scopeId,
                PreviewLeaseAcquireRequest request,
                ClaimsPrincipal principal,
                GitHubTargetPreviewLeaseService service,
                CancellationToken ct) => GuardAsync(async () =>
            {
                var actor = Actor(principal);
                return await service.AcquireAsync(id, planId, scopeId,
                    actor, request.DurationSeconds, ct);
            }));

        routes.MapPost(root + "/target-preview-leases/{leaseId:guid}/renew",
            (Guid id, Guid planId, Guid scopeId, Guid leaseId,
                PreviewLeaseChangeRequest request,
                ClaimsPrincipal principal,
                GitHubTargetPreviewLeaseService service,
                CancellationToken ct) => GuardAsync(async () =>
            {
                var ok = await service.RenewAsync(id, planId, scopeId,
                    Actor(principal), leaseId, request.Generation,
                    request.ExpectedPacketSha256 ?? "",
                    request.DurationSeconds, ct);
                if (!ok) throw new GitHubTargetPreviewLeaseException("PreviewLeaseLost");
                return new { renewed = true };
            }));

        routes.MapPost(root + "/target-preview-leases/{leaseId:guid}/release",
            (Guid id, Guid planId, Guid scopeId, Guid leaseId,
                PreviewLeaseChangeRequest request,
                ClaimsPrincipal principal,
                GitHubTargetPreviewLeaseService service,
                CancellationToken ct) => GuardAsync(async () =>
            {
                var ok = await service.ReleaseAsync(id, planId, scopeId,
                    Actor(principal), leaseId, request.Generation,
                    request.ExpectedPacketSha256 ?? "", ct);
                if (!ok) throw new GitHubTargetPreviewLeaseException("PreviewLeaseLost");
                return new { released = true };
            }));

        routes.MapPost(root + "/target-preview-leases/recover",
            (Guid id, Guid planId, Guid scopeId,
                PreviewLeaseRecoveryRequest request,
                ClaimsPrincipal principal,
                GitHubTargetPreviewLeaseService service,
                CancellationToken ct) => GuardAsync(async () =>
            {
                var ok = await service.RecoverAsync(id, planId, scopeId,
                    Actor(principal), request.ExpectedGeneration,
                    request.OldPacketSha256 ?? "",
                    request.AcknowledgementText ?? "", ct);
                if (!ok)
                    throw new GitHubTargetPreviewLeaseException("PreviewLeaseRecoveryMismatch");
                return new { recoveredForReadOnlyPreparation = true };
            }));

        routes.MapGet(root + "/target-preview-lease",
            (Guid id, Guid planId, Guid scopeId,
                GitHubTargetPreviewLeaseService service,
                CancellationToken ct) => GuardAsync(async () =>
            {
                return await service.GetAsync(id, planId, scopeId, ct)
                    ?? throw new KeyNotFoundException("PreviewLeaseNotFound");
            }));

        routes.MapGet(root + "/target-preview-lease/events",
            (Guid id, Guid planId, Guid scopeId,
                GitHubTargetPreviewLeaseService service,
                CancellationToken ct) => GuardAsync(async () =>
            {
                return await service.EventsAsync(id, planId, scopeId, ct);
            }));
    }

    private static Guid Actor(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : throw new UnauthorizedAccessException();

    private static async Task<IResult> GuardAsync(Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "PreviewLeaseDependencyNotFound" }); }
        catch (GitHubTargetPreviewLeaseException ex)
        {
            return ex.Code is "PreviewLeaseDurationInvalid" or
                "PreviewLeaseHandleInvalid" or "PreviewLeaseActorInvalid" or
                "PreviewLeaseRecoveryProofInvalid" or
                "PreviewLeaseRecoveryAcknowledgementRequired"
                    ? Results.BadRequest(new { error = ex.Code })
                    : Results.Conflict(new { error = ex.Code });
        }
        catch (GitHubCreateOnlyPacketException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubAppWriteScopeException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteRefFenceException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubImportIntentException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitImportRehearsalException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubImportReadinessException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitSourceBundleException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (IncrementalGitRefReconciliationException ex)
        { return Results.Conflict(new { error = ex.Code }); }
    }
}

public sealed record PreviewLeaseAcquireRequest(int DurationSeconds);
public sealed record PreviewLeaseChangeRequest(
    long Generation, string? ExpectedPacketSha256, int DurationSeconds = 0);
public sealed record PreviewLeaseRecoveryRequest(
    long ExpectedGeneration, string? OldPacketSha256,
    string? AcknowledgementText);
