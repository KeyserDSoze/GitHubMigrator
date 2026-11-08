using System.Security.Claims;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Authorization;

// F-079: a superadmin may request an authenticated GET-only target review
// and inspect its immutable PostgreSQL evidence. No transport, refspec,
// GitHub mutation, execution capability or automatic retry route exists.
public static class GitHubRemoteSandboxReviewSessionEndpoints
{
    public static void MapGitHubRemoteSandboxReviewSessionEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-sandbox-session");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-review-sessions";

        // Request body carries ONLY the F-055 hazard ID. Source packet,
        // destination and historic preflight are reloaded by the trusted
        // F-076/F-077/F-075 chain; current refs come from authenticated
        // GitHub App GETs. The caller cannot submit their own evidence.
        routes.MapPost(root, (
            Guid id, Guid planId, Guid scopeId,
            SandboxReviewSessionRequest request,
            ClaimsPrincipal principal,
            GitHubRemoteTransmitHazardOptions options,
            GitHubRemoteTrustedDispatchReadOnlyOrchestrator orchestrator,
            CancellationToken ct) => GuardAsync(async () =>
        {
            RequireEnabled(options);
            return await orchestrator.ReviewAndRecordSessionFromJournalAsync(
                id, planId, scopeId, request.HazardId, Actor(principal), ct);
        }));

        // The GUID is NOT enough to read someone else's review. Validate
        // the exact persisted preparation/plan/scope/target lineage before
        // returning even a read-only evidence receipt.
        routes.MapGet(root + "/{sessionId:guid}", (
            Guid id, Guid planId, Guid scopeId, Guid sessionId,
            GitHubRemoteTransmitHazardOptions options,
            PostgresGitHubRemoteSandboxReviewSessionJournal journal,
            CancellationToken ct) => GuardAsync(async () =>
        {
            RequireEnabled(options);
            return await journal.InspectScopedAsync(
                sessionId, id, planId, scopeId,
                options.AllowedRepositoryId, ct) ??
                throw new KeyNotFoundException("SandboxReviewSessionNotFound");
        }));
    }

    private static void RequireEnabled(GitHubRemoteTransmitHazardOptions o)
    {
        if (!o.Enabled || o.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(o.AllowedRepository))
            throw new GitHubRemoteSandboxReviewSessionException(
                "SandboxReviewSessionDisabled");
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
        { return Results.NotFound(new { error = "SandboxReviewSessionNotFound" }); }
        catch (GitHubRemoteSandboxReviewSessionException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteCreateOnlyDispatchException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteAppPreflightException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubCreateOnlyPacketException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubAppWriteScopeException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteRefFenceException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteTestGrantException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteTransmitHazardException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteSandboxFenceException ex)
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
        catch (GitHubCandidatePolicyException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRefObservationException ex)
        { return Results.Conflict(new { error = ex.Code }); }
    }
}

public sealed record SandboxReviewSessionRequest(Guid HazardId);
