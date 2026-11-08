using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-080 does NOT enqueue or dispatch work. The only operations are an
// immutable write-disabled handoff and a historical audit inspection.
public static class GitHubRemoteSandboxDispatchIntentEndpoints
{
    public static void MapGitHubRemoteSandboxDispatchIntentEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-dispatch-intent");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-review-sessions" +
            "/{sessionId:guid}/dispatch-intents";

        // The caller supplies no refs, refspecs, credentials, target URL,
        // packet SHA, read-only receipt, write flag or command.
        routes.MapPost(root, (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            ClaimsPrincipal principal,
            PostgresGitHubRemoteSandboxDispatchIntentJournal journal,
            CancellationToken ct) => GuardAsync(async () =>
                await journal.RecordAsync(sessionId, id, planId, scopeId,
                    Actor(principal), ct)));

        routes.MapGet(root + "/{intentId:guid}", (
            Guid id, Guid planId, Guid scopeId, Guid sessionId, Guid intentId,
            PostgresGitHubRemoteSandboxDispatchIntentJournal journal,
            CancellationToken ct) => GuardAsync(async () =>
        {
            var inspected = await journal.InspectScopedAsync(
                intentId, id, planId, scopeId, ct) ??
                throw new KeyNotFoundException("SandboxDispatchIntentNotFound");
            if (inspected.Intent.SessionId != sessionId)
                throw new KeyNotFoundException("SandboxDispatchIntentNotFound");
            return inspected;
        }));
    }

    private static Guid Actor(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
            out var actor) && actor != Guid.Empty
            ? actor : throw new UnauthorizedAccessException();

    private static async Task<IResult> GuardAsync(Func<Task<object>> work)
    {
        try { return Results.Ok(await work()); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "SandboxDispatchIntentNotFound" }); }
        catch (GitHubRemoteSandboxDispatchIntentException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteSandboxReviewSessionException ex)
        { return Results.Conflict(new { error = ex.Code }); }
    }
}
