using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class WorkloadEndpoints
{
    public static void MapWorkloadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workloads")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        group.MapPost("/pipelines/preview", async Task<IResult>(
            PipelinePreviewInput input, WorkloadPreviewService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PreviewPipelineAsync(
                input.SourceConnectionId, input.DestinationConnectionId,
                input.ProjectId ?? "", input.PipelineId, input.TargetRepository ?? "", ct)); }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (HttpRequestException) { return Results.UnprocessableEntity(new { error = "ProviderReadFailed" }); }
        }).RequireRateLimiting("external");

        group.MapPost("/boards/preview", async Task<IResult>(
            BoardsPreviewInput input, WorkloadPreviewService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PreviewBoardsAsync(
                input.SourceConnectionId, input.ProjectId ?? "",
                input.WorkItemIds, input.KnownGitHubIssues, ct)); }
            catch (WorkloadInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (HttpRequestException) { return Results.UnprocessableEntity(new { error = "ProviderReadFailed" }); }
        }).RequireRateLimiting("external");
    }
}

public sealed record PipelinePreviewInput(Guid SourceConnectionId, Guid DestinationConnectionId,
    string? ProjectId, int PipelineId, string? TargetRepository);
public sealed record BoardsPreviewInput(Guid SourceConnectionId, string? ProjectId,
    IReadOnlyList<int>? WorkItemIds, IReadOnlyDictionary<int, int>? KnownGitHubIssues);
