using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class IdentityMappingEndpoints
{
    public static void MapIdentityMappingEndpoints(this WebApplication app)
    {
        var endpoints = app.MapGroup("/api/identity-mappings")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        endpoints.MapGet("/{snapshotId:guid}", async Task<IResult> (Guid snapshotId,
            Guid destinationConnectionId, IdentityMappingService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.GetAsync(snapshotId, destinationConnectionId, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "MappingContextNotFound" }); }
            catch (MappingInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        });

        endpoints.MapPut("/{snapshotId:guid}", async Task<IResult> (Guid snapshotId,
            IdentityMappingUpdate request, IdentityMappingService service,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                var result = await service.SaveAsync(snapshotId, request.DestinationConnectionId,
                    request.ExpectedRevision, request.Mappings, ct);
                logs.CreateLogger("IdentityMappings").LogInformation(
                    "Draft updated SnapshotId={SnapshotId} DestinationId={DestinationId} Revision={Revision} Count={Count}",
                    result.Draft.SnapshotId, result.Draft.DestinationConnectionId,
                    result.Draft.Revision, result.Draft.Mappings.Count);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "MappingContextNotFound" }); }
            catch (MappingInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
            catch (MappingRevisionConflictException) { return Results.Conflict(new { error = "RevisionConflict" }); }
        });
    }
}

public sealed record IdentityMappingUpdate(
    Guid DestinationConnectionId, int ExpectedRevision,
    IReadOnlyList<IdentityMappingInput>? Mappings);
