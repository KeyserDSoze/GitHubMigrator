using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// No route accepts a PAT, private key, remote URL, Git ref, or Git
// command. This endpoint ONLY produces a read-only SHA evidence receipt.
public static class GitHubRemoteAppPreflightEndpoints
{
    public static void MapGitHubRemoteAppPreflightEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-remote-app-preflight");

        routes.MapPost(
            "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/remote-sandbox-write-fence" +
            "/{requestId:guid}/app-preflight",
            (Guid id, Guid planId, Guid scopeId, Guid requestId,
                GitHubRemoteAppPreflightRequest request,
                GitHubRemoteAppPreflightService service,
                CancellationToken ct) => GuardAsync(() =>
                service.InspectAsync(id, planId, scopeId,
                    requestId, request.Generation,
                    request.ExpectedPacketSha256 ?? "", ct)));
    }

    private static async Task<IResult> GuardAsync(
        Func<Task<GitHubRemoteAppPreflightReceipt>> work)
    {
        try { return Results.Ok(await work()); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "RemotePreflightDependencyNotFound" }); }
        catch (GitHubRemoteAppPreflightException ex)
        {
            return ex.Code is "RemotePreflightDisabledOrInvalid"
                or "RemotePreflightNotAllowlisted" ? Results.Conflict(new {
                    error = ex.Code }) : Results.Conflict(new { error = ex.Code });
        }
        catch (GitHubAppWriteScopeException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRefObservationException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubCandidatePolicyException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubRemoteRefFenceException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubCreateOnlyPacketException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitSourceBundleException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubImportReadinessException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubImportIntentException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitImportRehearsalException ex)
        { return Results.Conflict(new { error = ex.Code }); }
    }
}

public sealed record GitHubRemoteAppPreflightRequest(
    long Generation, string? ExpectedPacketSha256);
