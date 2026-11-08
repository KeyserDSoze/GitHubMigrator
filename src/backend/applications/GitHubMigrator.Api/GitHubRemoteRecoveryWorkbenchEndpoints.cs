using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-088: GET-only, bounded, superadmin monitoring of durable F-082 epochs.
// Never reads GitHub or runs F-083. Actions use the separately reviewed
// F-087/F-085 endpoints; this page supplies only historic identifiers.
public static class GitHubRemoteRecoveryWorkbenchEndpoints
{
    public static void MapGitHubRemoteRecoveryWorkbenchEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/incremental-preparations/{id:guid}/operation-plans/" +
            "{planId:guid}/remote-recovery-workbench",
            async (Guid id, Guid planId, int? take, Guid? after,
                PostgresGitHubRemoteRecoveryWorkbenchReader reader,
                CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await reader.ListAsync(
                        id, planId, after, take ?? 12, ct));
                }
                catch (GitHubRemoteRecoveryWorkbenchException ex)
                {
                    return ex.Code is "RecoveryWorkbenchQueryInvalid"
                        or "RecoveryWorkbenchCursorInvalid"
                        ? Results.BadRequest(new { error = ex.Code })
                        : Results.Conflict(new { error = ex.Code });
                }
                catch (GitHubRemotePostSendRecoveryException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubRemoteSandboxPreSendBarrierException ex)
                { return Results.Conflict(new { error = ex.Code }); }
                catch (GitHubRemoteTypedRecoveryBaselineException ex)
                { return Results.Conflict(new { error = ex.Code }); }
            })
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-dispatch-confirmation");
    }
}
