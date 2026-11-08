using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-085: scope-bound status and append-only independent closure evidence.
// The POST ONLY checkpoints an already-persisted F-058 full proof.
// Neither endpoint accepts a body, URL, refspec or GitHub token.
public static class GitHubRemotePostSendRecoveryEndpoints
{
    public static void MapGitHubRemotePostSendRecoveryEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-dispatch-confirmation");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-review-sessions" +
            "/{sessionId:guid}/dispatch-intents/{intentId:guid}/confirmations" +
            "/{confirmationId:guid}/post-send-recovery/{barrierId:guid}";

        routes.MapGet(root, (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            Guid intentId, Guid confirmationId, Guid barrierId,
            GitHubRemoteTransmitHazardOptions options,
            PostgresGitHubRemotePostSendRecoveryJournal journal,
            PostgresGitHubRemoteSandboxPreSendBarrierJournal barriers,
            CancellationToken ct) => GuardAsync(async () =>
        {
            RequireEnabled(options);
            var lineage = await barriers.InspectScopedAsync(
                barrierId, confirmationId, intentId, id, planId, scopeId, ct)
                ?? throw new KeyNotFoundException();
            if (lineage.Barrier.SessionId != sessionId)
                throw new KeyNotFoundException();
            return await journal.InspectScopedAsync(
                barrierId, confirmationId, intentId, id, planId, scopeId, ct)
                ?? throw new KeyNotFoundException();
        }));

        // No request body. A new independent human may only seal
        // already-committed F-054 AND F-058 evidence.
        routes.MapPost(root + "/closure-checkpoints", (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            Guid intentId, Guid confirmationId, Guid barrierId,
            ClaimsPrincipal principal,
            GitHubRemoteTransmitHazardOptions options,
            PostgresGitHubRemotePostSendRecoveryJournal journal,
            PostgresGitHubRemoteSandboxPreSendBarrierJournal barriers,
            CancellationToken ct) => GuardAsync(async () =>
        {
            RequireEnabled(options);
            var lineage = await barriers.InspectScopedAsync(
                barrierId, confirmationId, intentId, id, planId, scopeId, ct)
                ?? throw new KeyNotFoundException();
            if (lineage.Barrier.SessionId != sessionId)
                throw new KeyNotFoundException();
            var actor = Guid.TryParse(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                out var parsed) && parsed != Guid.Empty
                ? parsed : throw new UnauthorizedAccessException();
            return await journal.RecordIndependentClosureAsync(
                barrierId, confirmationId, intentId,
                id, planId, scopeId, actor, ct);
        }));
    }

    private static void RequireEnabled(GitHubRemoteTransmitHazardOptions o)
    {
        if (!o.Enabled || o.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(o.AllowedRepository))
            throw new GitHubRemotePostSendRecoveryException(
                "PostSendRecoveryDisabled");
    }

    private static async Task<IResult> GuardAsync(Func<Task<object>> work)
    {
        try { return Results.Ok(await work()); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "PostSendRecoveryNotFound" }); }
        catch (GitHubRemotePostSendRecoveryException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteSandboxPreSendBarrierException ex)
        { return Results.Conflict(new { error = ex.Code }); }
    }
}
