using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-087: two explicit manual post-send RECOVERY endpoints.
// POST is required to start provider GET / Git upload-pack; no request
// body, no ref/URL/OID/token, no F-083 writer or retry capability.
// GitHub App token creation uses POST /access_tokens only with
// Contents:read; target repository calls are read-only.
public static class GitHubIndependentRecoveryEndpoints
{
    public static void MapGitHubIndependentRecoveryEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-dispatch-confirmation");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-review-sessions" +
            "/{sessionId:guid}/dispatch-intents/{intentId:guid}/confirmations" +
            "/{confirmationId:guid}/independent-recovery/{barrierId:guid}";

        routes.MapPost(root + "/observe-tips", (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            Guid intentId, Guid confirmationId, Guid barrierId,
            ClaimsPrincipal principal,
            GitHubIndependentRecoveryOptions options,
            PostgresGitHubRemoteSandboxPreSendBarrierJournal barriers,
            GitHubIndependentRecoveryAcquisition acquisition,
            CancellationToken ct) => RunAsync(
                id, planId, scopeId, sessionId, intentId, confirmationId,
                barrierId, principal, options, barriers, ct,
                actor => acquisition.AcquireTipsAsync(
                    confirmationId, intentId, id, planId, scopeId,
                    actor, ct)));

        routes.MapPost(root + "/verify-full-objects", (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            Guid intentId, Guid confirmationId, Guid barrierId,
            ClaimsPrincipal principal,
            GitHubIndependentRecoveryOptions options,
            PostgresGitHubRemoteSandboxPreSendBarrierJournal barriers,
            GitHubIndependentRecoveryAcquisition acquisition,
            CancellationToken ct) => RunAsync(
                id, planId, scopeId, sessionId, intentId, confirmationId,
                barrierId, principal, options, barriers, ct,
                actor => acquisition.AcquireClosureAsync(
                    confirmationId, intentId, id, planId, scopeId,
                    actor, ct)));
    }

    private static async Task<IResult> RunAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid sessionId,
        Guid intentId, Guid confirmationId, Guid barrierId,
        ClaimsPrincipal principal, GitHubIndependentRecoveryOptions options,
        PostgresGitHubRemoteSandboxPreSendBarrierJournal barriers,
        CancellationToken ct,
        Func<Guid, Task<GitHubIndependentRecoveryResult>> action)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            return Results.Conflict(new {
                error = "IndependentRecoveryDisabled"
            });
        try
        {
            var historical = await barriers.InspectScopedAsync(
                barrierId, confirmationId, intentId,
                preparationId, planId, scopeId, ct);
            if (historical is null ||
                historical.Barrier.SessionId != sessionId ||
                historical.Barrier.RepositoryId != options.AllowedRepositoryId)
                return Results.NotFound(new {
                    error = "IndependentRecoveryNotFound"
                });
            if (!Guid.TryParse(
                    principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var actor) || actor == Guid.Empty)
                return Results.Unauthorized();
            var result = await action(actor);
            return Results.Ok(result);
        }
        catch (GitHubIndependentRecoveryException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteTypedRecoveryBaselineException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteSandboxPreSendBarrierException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteReadbackException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteObjectReconciliationException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubTrustedRemoteReadException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubReadOnlyObjectMirrorException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubAppWriteScopeException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteTipException ex)
        { return Results.Conflict(new { error = ex.Code }); }
    }
}
