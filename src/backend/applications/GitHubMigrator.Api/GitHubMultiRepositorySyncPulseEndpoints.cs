using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-089 is only a projection of committed F-025/F-029/F-088 history.
// No POST, no target mutation, no discovery or staging job is started.
public static class GitHubMultiRepositorySyncPulseEndpoints
{
    public static void MapGitHubMultiRepositorySyncPulseEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/incremental-preparations/{id:guid}/operation-plans/" +
            "{planId:guid}/repository-sync-pulse",
            async (Guid id, Guid planId,
                PostgresGitHubMultiRepositorySyncPulseReader reader,
                CancellationToken ct) =>
            {
                try { return Results.Ok(await reader.InspectAsync(
                    id, planId, ct)); }
                catch (GitHubMultiRepositorySyncPulseException ex)
                {
                    return ex.Code == "MultiRepositoryPulseNotFound"
                        ? Results.NotFound(new { error = ex.Code })
                        : ex.Code == "MultiRepositoryPulseQueryInvalid"
                            ? Results.BadRequest(new { error = ex.Code })
                            : Results.Conflict(new { error = ex.Code });
                }
                catch (GitHubRemoteRecoveryWorkbenchException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubRemotePostSendRecoveryException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubRemoteTypedRecoveryBaselineException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubRemoteSandboxPreSendBarrierException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            })
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-dispatch-confirmation");
    }
}
