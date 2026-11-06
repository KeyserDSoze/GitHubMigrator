using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// F-046 POST is a request to perform GET-only inspection at GitHub.
// It cannot submit refs, Git commands, OAuth tokens or write proposals.
public static class GitHubCandidatePolicyEndpoints
{
    public static void MapGitHubCandidatePolicyEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("github-candidate-policy");

        routes.MapPost(
            "/{id:guid}/operation-plans/{planId:guid}/github-app-scopes/{scopeId:guid}/create-only-packet/candidate-policy",
            async Task<IResult> (Guid id, Guid planId, Guid scopeId,
                GitHubCandidatePolicyRequest request,
                GitHubCandidatePolicyService service, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await service.ReviewAsync(
                        id, planId, scopeId, request.ExpectedPacketSha256 ?? "", ct));
                }
                catch (KeyNotFoundException)
                { return Results.NotFound(new { error = "CandidatePolicyDependencyMissing" }); }
                catch (GitHubCandidatePolicyException ex)
                {
                    return ex.Code == "CandidatePolicyRequestInvalid"
                        ? Results.BadRequest(new { error = ex.Code })
                        : Results.Conflict(new { error = ex.Code });
                }
                catch (GitHubCreateOnlyPacketException ex)
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
            });
    }
}

public sealed record GitHubCandidatePolicyRequest(string? ExpectedPacketSha256);
