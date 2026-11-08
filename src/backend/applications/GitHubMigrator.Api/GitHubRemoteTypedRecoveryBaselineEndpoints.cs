using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

// F-086 exposes ONLY the non-executable SHA/count summary of the
// already-committed F-082-bound snapshot. Original branch/tag refs and
// their OIDs are available only to trusted in-process recovery code.
public static class GitHubRemoteTypedRecoveryBaselineEndpoints
{
    public static void MapGitHubRemoteTypedRecoveryBaselineEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-dispatch-confirmation");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-review-sessions" +
            "/{sessionId:guid}/dispatch-intents/{intentId:guid}/confirmations" +
            "/{confirmationId:guid}/typed-recovery-baseline";

        routes.MapGet(root, async (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            Guid intentId, Guid confirmationId,
            GitHubRemoteTypedRecoveryBaselineOptions options,
            PostgresGitHubRemoteTypedRecoveryBaselineJournal journal,
            AppDbContext db, CancellationToken ct) =>
        {
            if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
                string.IsNullOrWhiteSpace(options.AllowedRepository))
                return Results.Conflict(new {
                    error = "TypedRecoveryBaselineDisabled"
                });
            try
            {
                var status = await journal.InspectScopedAsync(
                    confirmationId, intentId, id, planId, scopeId, ct);
                if (status is null)
                    return Results.NotFound(new {
                        error = "TypedRecoveryBaselineNotFound"
                    });
                var belongsToSession = await db
                    .GitHubRemoteSandboxDispatchConfirmations.AsNoTracking()
                    .AnyAsync(x => x.Id == confirmationId &&
                        x.IntentId == intentId && x.SessionId == sessionId &&
                        x.RepositoryId == status.RepositoryId, ct);
                if (!belongsToSession)
                    return Results.NotFound(new {
                        error = "TypedRecoveryBaselineNotFound"
                    });
                return Results.Ok(status);
            }
            catch (GitHubRemoteTypedRecoveryBaselineException ex)
            { return Results.Conflict(new { error = ex.Code }); }
            catch (GitHubRemoteSandboxPreSendBarrierException ex)
            { return Results.Conflict(new { error = ex.Code }); }
        });
    }

}
