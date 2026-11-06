using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// Superadmin-only control plane for an allowlisted LOCAL sandbox. No endpoint
// accepts a Git URL, filesystem path, refspec or execution credential.
public static class GitSandboxDispatchEndpoints
{
    public static void MapGitSandboxDispatchEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/incremental-preparations")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" })
            .RequireRateLimiting("git-local-sandbox-dispatch");

        const string root = "/{id:guid}/operation-plans/{planId:guid}" +
            "/github-app-scopes/{scopeId:guid}/transfer-attempts/{attemptId:guid}" +
            "/local-sandbox";

        routes.MapPost(root, async Task<IResult> (
            Guid id, Guid planId, Guid scopeId, Guid attemptId,
            GitSandboxDispatchRequest request,
            GitSandboxDispatchService service,
            IGitHubTransferAttemptStore attempts, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                await RequireScopedAttemptAsync(
                    attempts, id, planId, scopeId, attemptId, ct);
                var view = await service.EnqueueAsync(
                    id, planId, scopeId, attemptId, request.SandboxId, ct);
                return SafeView(view);
            }));

        routes.MapGet(root, async Task<IResult> (
            Guid id, Guid planId, Guid scopeId, Guid attemptId,
            IGitSandboxDispatchStore store,
            IGitHubTransferAttemptStore attempts, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                await RequireScopedAttemptAsync(
                    attempts, id, planId, scopeId, attemptId, ct);
                var job = await store.GetAsync(attemptId, ct)
                    ?? throw new KeyNotFoundException("SandboxDispatchNotFound");
                return SafeView(job);
            }));

        routes.MapGet(root + "/events", async Task<IResult> (
            Guid id, Guid planId, Guid scopeId, Guid attemptId,
            IGitSandboxDispatchStore store,
            IGitHubTransferAttemptStore attempts, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                await RequireScopedAttemptAsync(
                    attempts, id, planId, scopeId, attemptId, ct);
                return await store.EventsAsync(attemptId, ct);
            }));
    }

    // Never return the internal claim token or sealed refs payload.
    private static object SafeView(GitSandboxDispatchJob job) => new
    {
        job.Id, job.AttemptId, job.SandboxId,
        job.PacketSha256, job.PayloadSha256, job.State,
        job.CreatedAtUtc, job.UpdatedAtUtc,
        job.OutcomeSha256, job.OutcomeCode,
        job.RemoteWritesPerformed, job.ExecutionAuthorized,
        job.ReadyForExecution
    };

    private static async Task RequireScopedAttemptAsync(
        IGitHubTransferAttemptStore attempts, Guid preparationId,
        Guid planId, Guid scopeId, Guid attemptId, CancellationToken ct)
    {
        if (await attempts.GetAsync(preparationId, planId, scopeId,
            attemptId, ct) is null)
            throw new KeyNotFoundException("TransferPreparationNotFound");
    }

    private static async Task<IResult> GuardAsync(Func<Task<object>> callback)
    {
        try { return Results.Ok(await callback()); }
        catch (KeyNotFoundException)
        { return Results.NotFound(new { error = "SandboxDependencyNotFound" }); }
        catch (GitSandboxDispatchException ex)
        {
            return ex.Code is "SandboxDispatchRequestInvalid" or
                "SandboxNotAllowlisted"
                ? Results.BadRequest(new { error = ex.Code })
                : Results.Conflict(new { error = ex.Code });
        }
        catch (GitHubCreateOnlyPacketException ex)
        { return Results.Conflict(new { error = ex.Code }); }
        catch (LocalGitCreateOnlyTransferException ex)
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

public sealed record GitSandboxDispatchRequest(Guid SandboxId);
