using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.AspNetCore.Authorization;

public static class ConnectionEndpoints
{
    public static void MapConnectionEndpoints(this WebApplication app)
    {
        var endpoints = app.MapGroup("/api/connections")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        endpoints.MapGet("", async (ConnectionService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToResponse)));

        endpoints.MapPost("/validate", async Task<IResult> (ConnectionInput input,
            ConnectionService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ValidateDraftAsync(input.ToDraft(), ct)); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "InvalidConnectionInput" }); }
        }).RequireRateLimiting("external");

        endpoints.MapPost("", async Task<IResult> (ConnectionInput input, ConnectionService service,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                var result = await service.CreateAsync(input.ToDraft(), ct);
                logs.CreateLogger("Connections").LogInformation(
                    "Migration connection created Id={ConnectionId} Provider={Provider} Direction={Direction}",
                    result.Id, result.ProviderKey, result.Direction);
                return Results.Created($"/api/connections/{result.Id}", ToResponse(result));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "InvalidConnectionInput" }); }
            catch (ConnectionValidationException ex) { return Results.UnprocessableEntity(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        endpoints.MapPost("/{id:guid}/verify", async Task<IResult> (Guid id, ConnectionService service,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var result = await service.VerifyAsync(id, ct);
            if (result is null) return Results.NotFound();
            logs.CreateLogger("Connections").LogInformation(
                "Connection validation Id={ConnectionId} Provider={Provider} Status={Status}",
                id, result.ProviderKey, result.ValidationCode);
            return Results.Ok(ToResponse(result));
        }).RequireRateLimiting("external");

        endpoints.MapPut("/{id:guid}/credential", async Task<IResult> (Guid id, RotateCredentialInput input,
            ConnectionService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.RotateAsync(id, input.PersonalAccessToken ?? "", ct);
                return result is null ? Results.NotFound() : Results.Ok(ToResponse(result));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "InvalidConnectionInput" }); }
            catch (ConnectionValidationException ex) { return Results.UnprocessableEntity(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        endpoints.MapDelete("/{id:guid}", async Task<IResult> (Guid id, ConnectionService service,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var removed = await service.DeleteAsync(id, ct);
            if (!removed) return Results.NotFound();
            logs.CreateLogger("Connections").LogInformation("Migration connection deleted Id={ConnectionId}", id);
            return Results.NoContent();
        });
    }

    private static ConnectionResponse ToResponse(MigrationConnection x) =>
        new(x.Id, x.Name, x.ProviderKey, x.Direction.ToString().ToLowerInvariant(),
            x.Organization, x.CreatedAtUtc, x.ValidatedAtUtc, x.ValidationCode,
            x.AuthenticatedAs, x.PermissionAssessment);
}

public sealed record ConnectionInput(string? Name, string? ProviderKey, string? Organization,
    string? PersonalAccessToken)
{
    public ConnectionDraft ToDraft() => new(Name ?? "", ProviderKey ?? "", Organization ?? "",
        PersonalAccessToken ?? "");
}
public sealed record RotateCredentialInput(string? PersonalAccessToken);
public sealed record ConnectionResponse(Guid Id, string Name, string ProviderKey, string Direction,
    string Organization, DateTimeOffset CreatedAtUtc, DateTimeOffset? ValidatedAtUtc,
    string ValidationCode, string? AuthenticatedAs, string PermissionAssessment);
