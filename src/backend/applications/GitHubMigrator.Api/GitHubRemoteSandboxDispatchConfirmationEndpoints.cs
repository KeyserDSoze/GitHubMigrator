using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-081: two independent humans leave immutable read-only approval
// evidence. This is neither an execution endpoint nor a GitHub sender.
public static class GitHubRemoteSandboxDispatchConfirmationEndpoints
{
    public static void MapGitHubRemoteSandboxDispatchConfirmationEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-dispatch-confirmation");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-review-sessions" +
            "/{sessionId:guid}/dispatch-intents/{intentId:guid}/confirmations";

        // Accept no body: no requested write flag, OID, ref, URL, token,
        // command or other caller-supplied authority.
        routes.MapPost(root, (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            Guid intentId, ClaimsPrincipal principal,
            GitHubRemoteTransmitHazardOptions options,
            PostgresGitHubRemoteSandboxDispatchConfirmationJournal journal,
            PostgresGitHubRemoteSandboxDispatchIntentJournal intents,
            CancellationToken ct) => GuardAsync(async () =>
        {
            RequireEnabled(options);
            var scope = await intents.InspectScopedAsync(
                intentId, id, planId, scopeId, ct) ??
                throw new KeyNotFoundException("SandboxDispatchConfirmationNotFound");
            if (scope.Intent.SessionId != sessionId)
                throw new KeyNotFoundException("SandboxDispatchConfirmationNotFound");
            return await journal.ConfirmAsync(intentId, id, planId, scopeId,
                Actor(principal), ct);
        }));

        routes.MapGet(root + "/{confirmationId:guid}", (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            Guid intentId, Guid confirmationId,
            GitHubRemoteTransmitHazardOptions options,
            PostgresGitHubRemoteSandboxDispatchConfirmationJournal journal,
            CancellationToken ct) => GuardAsync(async () =>
        {
            RequireEnabled(options);
            var result = await journal.InspectScopedAsync(
                confirmationId, intentId, id, planId, scopeId, ct) ??
                throw new KeyNotFoundException("SandboxDispatchConfirmationNotFound");
            if (result.Confirmation.SessionId != sessionId)
                throw new KeyNotFoundException("SandboxDispatchConfirmationNotFound");
            return result;
        }));
    }

    private static void RequireEnabled(GitHubRemoteTransmitHazardOptions o)
    {
        if (!o.Enabled || o.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(o.AllowedRepository))
            throw new GitHubRemoteSandboxDispatchConfirmationException(
                "SandboxDispatchConfirmationDisabled");
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
        { return Results.NotFound(new { error = "SandboxDispatchConfirmationNotFound" }); }
        catch (GitHubRemoteSandboxDispatchConfirmationException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteSandboxDispatchIntentException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteSandboxReviewSessionException ex)
        { return Results.Conflict(new { error = ex.Code }); }
    }
}
