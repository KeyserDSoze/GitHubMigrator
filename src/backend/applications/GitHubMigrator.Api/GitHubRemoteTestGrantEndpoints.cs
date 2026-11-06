using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// F-052 does NOT define an execute/push route. Only a separately reviewed
// no-write intent, cancellation, quarantine and manual recovery are exposed.
public static class GitHubRemoteTestGrantEndpoints
{
    public static void MapGitHubRemoteTestGrantEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-test-grant");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/transfer-attempts/{attemptId:guid}" +
            "/remote-test-grants";

        routes.MapPost(root, (
            Guid id, Guid planId, Guid scopeId, Guid attemptId,
            RemoteTestGrantProposalRequest request,
            ClaimsPrincipal principal, GitHubRemoteTestGrantService service,
            CancellationToken ct) => GuardAsync(async () =>
            {
                var claim = await service.ProposeAsync(id, planId, scopeId,
                    attemptId, request.FenceRequestId, request.Generation,
                    request.ExpectedPacketSha256 ?? "", Actor(principal), ct);
                return new {
                    grant = claim.Status,
                    cancellationToken = claim.CancellationToken
                };
            }));

        routes.MapPost(root + "/{grantId:guid}/review", (
            Guid id, Guid planId, Guid scopeId, Guid attemptId, Guid grantId,
            RemoteTestGrantProposalRequest request,
            ClaimsPrincipal principal, GitHubRemoteTestGrantService service,
            CancellationToken ct) => GuardAsync(async () =>
                await service.ReviewAsync(id, planId, scopeId, attemptId,
                    request.FenceRequestId, request.Generation, grantId,
                    request.ExpectedPacketSha256 ?? "",
                    Actor(principal), ct)));

        routes.MapPost(root + "/{grantId:guid}/cancel", (
            Guid id, Guid planId, Guid scopeId, Guid attemptId, Guid grantId,
            RemoteTestGrantCancellationRequest request,
            ClaimsPrincipal principal, GitHubRemoteTestGrantService service,
            CancellationToken ct) => GuardAsync(async () =>
            {
                if (!await service.CancelAsync(id, planId, scopeId,
                    attemptId, grantId, request.CancellationToken,
                    Actor(principal), ct))
                    throw new GitHubRemoteTestGrantException(
                        "RemoteTestGrantCancelLost");
                return new { cancelledWithoutRemoteTransport = true };
            }));

        routes.MapPost(root + "/{grantId:guid}/recover", (
            Guid id, Guid planId, Guid scopeId, Guid attemptId, Guid grantId,
            RemoteTestGrantRecoveryRequest request,
            ClaimsPrincipal principal, GitHubRemoteTestGrantService service,
            CancellationToken ct) => GuardAsync(async () =>
            {
                if (!await service.RecoverAsync(id, planId, scopeId,
                    attemptId, grantId, Actor(principal),
                    request.AcknowledgementText ?? "", ct))
                    throw new GitHubRemoteTestGrantException(
                        "RemoteTestGrantRecoveryLost");
                return new { recoveredNoRemoteTransport = true };
            }));

        routes.MapGet(root + "/{grantId:guid}", (
            Guid id, Guid planId, Guid scopeId, Guid attemptId, Guid grantId,
            GitHubRemoteTestGrantService service, CancellationToken ct) =>
            GuardAsync(async () =>
                await service.GetAsync(id, planId, scopeId, attemptId,
                    grantId, ct) ??
                throw new KeyNotFoundException("RemoteTestGrantNotFound")));

        routes.MapGet(root + "/{grantId:guid}/events", (
            Guid id, Guid planId, Guid scopeId, Guid attemptId, Guid grantId,
            GitHubRemoteTestGrantService service, CancellationToken ct) =>
            GuardAsync(async () =>
                await service.EventsAsync(id, planId, scopeId, attemptId,
                    grantId, ct)));
    }

    private static Guid Actor(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
            out var actor) ? actor : throw new UnauthorizedAccessException();

    private static async Task<IResult> GuardAsync(Func<Task<object>> work)
    {
        try { return Results.Ok(await work()); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "RemoteTestGrantDependencyNotFound" }); }
        catch (GitHubRemoteTestGrantException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteAppPreflightException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubCreateOnlyPacketException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubAppWriteScopeException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRefObservationException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubCandidatePolicyException ex)
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

public sealed record RemoteTestGrantProposalRequest(
    Guid FenceRequestId, long Generation, string? ExpectedPacketSha256);
public sealed record RemoteTestGrantCancellationRequest(Guid CancellationToken);
public sealed record RemoteTestGrantRecoveryRequest(string? AcknowledgementText);
