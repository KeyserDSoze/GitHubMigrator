using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// F-050 is exclusively a PostgreSQL coordination / review endpoint.
// No GitHub API client, token minter or Git subprocess is ever injected.
public static class GitHubRemoteSandboxFenceEndpoints
{
    public static void MapGitHubRemoteSandboxFenceEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-fence");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-write-fence";

        routes.MapPost(root + "/propose", (
            Guid id, Guid planId, Guid scopeId,
            RemoteSandboxFenceProposalRequest request,
            ClaimsPrincipal principal, GitHubRemoteSandboxFenceService service,
            CancellationToken ct) => GuardAsync(async () =>
                await service.ProposeAsync(id, planId, scopeId,
                    request.AttemptId, request.ExpectedPacketSha256 ?? "",
                    Actor(principal), ct)));

        routes.MapPost(root + "/{requestId:guid}/review", (
            Guid id, Guid planId, Guid scopeId, Guid requestId,
            RemoteSandboxFenceReviewRequest request,
            ClaimsPrincipal principal, GitHubRemoteSandboxFenceService service,
            CancellationToken ct) => GuardAsync(async () =>
                await service.ReviewAsync(id, planId, scopeId,
                    request.AttemptId, requestId, request.Generation,
                    request.ExpectedPacketSha256 ?? "",
                    Actor(principal), ct)));

        routes.MapPost(root + "/{requestId:guid}/release", (
            Guid id, Guid planId, Guid scopeId, Guid requestId,
            RemoteSandboxFenceReleaseRequest request,
            ClaimsPrincipal principal, GitHubRemoteSandboxFenceService service,
            CancellationToken ct) => GuardAsync(async () =>
            {
                var released = await service.ReleaseAsync(id, planId, scopeId,
                    request.AttemptId, requestId, request.Generation,
                    request.FenceToken, Actor(principal), ct);
                if (!released)
                    throw new GitHubRemoteSandboxFenceException("RemoteFenceReleaseLost");
                return new { releasedWithoutRemoteWrites = true };
            }));

        routes.MapPost(root + "/recover", (
            Guid id, Guid planId, Guid scopeId,
            RemoteSandboxFenceRecoveryRequest request,
            ClaimsPrincipal principal, GitHubRemoteSandboxFenceService service,
            CancellationToken ct) => GuardAsync(async () =>
            {
                var recovered = await service.RecoverAsync(id, planId,
                    scopeId, request.AttemptId, request.OldRequestId,
                    request.Generation, request.ExpectedPacketSha256 ?? "",
                    request.AcknowledgementText ?? "",
                    Actor(principal), ct);
                if (!recovered)
                    throw new GitHubRemoteSandboxFenceException("RemoteFenceRecoveryLost");
                return new { recoveredNoWrites = true };
            }));

        routes.MapGet(root, (
            Guid id, Guid planId, Guid scopeId,
            GitHubRemoteSandboxFenceService service,
            CancellationToken ct) => GuardAsync(async () =>
                await service.GetAsync(id, planId, scopeId, ct)
                    ?? throw new KeyNotFoundException("RemoteFenceNotFound")));

        routes.MapGet(root + "/events", (
            Guid id, Guid planId, Guid scopeId,
            GitHubRemoteSandboxFenceService service,
            CancellationToken ct) => GuardAsync(async () =>
                await service.EventsAsync(id, planId, scopeId, ct)));
    }

    private static Guid Actor(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
            out var actor) ? actor : throw new UnauthorizedAccessException();

    private static async Task<IResult> GuardAsync(Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "RemoteFenceDependencyNotFound" }); }
        catch (GitHubRemoteSandboxFenceException ex)
            { return ex.Code is "RemoteFenceRequestInvalid" or
                "RemoteFenceHandleInvalid" or
                "RemoteFenceRecoveryAttestationRequired"
                ? Results.BadRequest(new { error = ex.Code })
                : Results.Conflict(new { error = ex.Code }); }
        catch (GitHubTargetPreviewLeaseException ex)
        { return Results.Conflict(new { error = ex.Code }); }
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

public sealed record RemoteSandboxFenceProposalRequest(
    Guid AttemptId, string? ExpectedPacketSha256);

public sealed record RemoteSandboxFenceReviewRequest(
    Guid AttemptId, long Generation, string? ExpectedPacketSha256);

public sealed record RemoteSandboxFenceReleaseRequest(
    Guid AttemptId, long Generation, Guid FenceToken);

public sealed record RemoteSandboxFenceRecoveryRequest(
    Guid AttemptId, Guid OldRequestId, long Generation,
    string? ExpectedPacketSha256, string? AcknowledgementText);
