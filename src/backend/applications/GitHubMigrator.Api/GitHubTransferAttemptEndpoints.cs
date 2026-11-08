using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// F-047 routes only persist independent read-only preparation and inspection.
// The body never accepts a ref, command, target name or credential.
public static class GitHubTransferAttemptEndpoints
{
    public static void MapGitHubTransferAttemptEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-transfer-attempts");
        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}";

        routes.MapPost(root + "/transfer-attempts",
            (Guid id, Guid planId, Guid scopeId,
                PrepareGitHubTransferAttemptRequest request,
                ClaimsPrincipal principal,
                GitHubTransferAttemptService service,
                CancellationToken ct) => GuardAsync(async () =>
                await service.PrepareAsync(id, planId, scopeId,
                    request.ExpectedPacketSha256 ?? "",
                    request.ExpectedPolicySha256 ?? "",
                    request.LeaseToken, request.LeaseGeneration,
                    Actor(principal), ct)));

        routes.MapPost(root + "/transfer-attempts/{attemptId:guid}/review",
            (Guid id, Guid planId, Guid scopeId, Guid attemptId,
                ReviewGitHubTransferAttemptRequest request,
                ClaimsPrincipal principal,
                GitHubTransferAttemptService service,
                CancellationToken ct) => GuardAsync(async () =>
                await service.DecideAsync(id, planId, scopeId,
                    attemptId, Actor(principal),
                    request.Decision ?? "", ct)));

        routes.MapGet(root + "/transfer-attempts/{attemptId:guid}",
            (Guid id, Guid planId, Guid scopeId, Guid attemptId,
                GitHubTransferAttemptService service,
                CancellationToken ct) => GuardAsync(async () =>
                await service.GetAsync(id, planId, scopeId, attemptId, ct)
                    ?? throw new KeyNotFoundException("TransferPreparationNotFound")));

        routes.MapGet(root + "/transfer-attempts/{attemptId:guid}/events",
            (Guid id, Guid planId, Guid scopeId, Guid attemptId,
                GitHubTransferAttemptService service,
                CancellationToken ct) => GuardAsync(async () =>
                await service.EventsAsync(id, planId, scopeId,
                    attemptId, ct)));
    }

    private static Guid Actor(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)
            ? actor : throw new UnauthorizedAccessException();

    private static async Task<IResult> GuardAsync(Func<Task<object>> callback)
    {
        try { return Results.Ok(await callback()); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "TransferPreparationDependencyNotFound" }); }
        catch (GitHubTransferAttemptException ex)
        {
            return ex.Code is "TransferPreparationRequestInvalid" or
                "TransferPreparationDecisionInvalid"
                ? Results.BadRequest(new { error = ex.Code })
                : Results.Conflict(new { error = ex.Code });
        }
        catch (GitHubCreateOnlyPacketException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (GitHubCandidatePolicyException ex)
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

public sealed record PrepareGitHubTransferAttemptRequest(
    string? ExpectedPacketSha256, string? ExpectedPolicySha256,
    Guid LeaseToken, long LeaseGeneration);

public sealed record ReviewGitHubTransferAttemptRequest(
    string? Decision);
