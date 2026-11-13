using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class AiConnectionEndpoints
{
    public static void MapAiConnectionEndpoints(this WebApplication app)
    {
        var endpoints = app.MapGroup("/api/ai-connections")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        endpoints.MapGet("", async (AiConnectionService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToResponse)));

        // What this deployment allows, so the UI can explain a rejection before the operator hits it.
        endpoints.MapGet("/options", (AiConnectionService service) => Results.Ok(new AiOptionsResponse(
            ["OpenAICompatible", "AzureOpenAI"], service.Policy.Hosts, service.Policy.AllowHttp, service.ManagedIdentityAvailable)));

        endpoints.MapPost("/validate", async Task<IResult> (AiConnectionInput input, AiConnectionService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ValidateDraftAsync(input.ToDraft(), ct)); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "InvalidAiConnectionInput" }); }
        }).RequireRateLimiting("external");

        endpoints.MapPost("", async Task<IResult> (AiConnectionInput input, AiConnectionService service,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                var result = await service.CreateAsync(input.ToDraft(), ct);
                logs.CreateLogger("AiConnections").LogInformation(
                    "AI connection created Id={ConnectionId} Provider={Provider} Host={Host}",
                    result.Id, result.Provider, new Uri(result.Endpoint).Host);
                return Results.Created($"/api/ai-connections/{result.Id}", ToResponse(result));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "InvalidAiConnectionInput" }); }
            catch (AiConnectionException ex) { return Results.UnprocessableEntity(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        endpoints.MapPost("/{id:guid}/verify", async Task<IResult> (Guid id, AiConnectionService service,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var result = await service.VerifyAsync(id, ct);
            if (result is null) return Results.NotFound();
            logs.CreateLogger("AiConnections").LogInformation(
                "AI connection verification Id={ConnectionId} Status={Status}", id, result.VerificationCode);
            return Results.Ok(ToResponse(result));
        }).RequireRateLimiting("external");

        endpoints.MapPut("/{id:guid}/credential", async Task<IResult> (Guid id, AiRotateInput input,
            AiConnectionService service, CancellationToken ct) =>
        {
            try
            {
                var result = await service.RotateAsync(id, input.ApiKey ?? "", ct);
                return result is null ? Results.NotFound() : Results.Ok(ToResponse(result));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "InvalidAiConnectionInput" }); }
            catch (AiConnectionException ex) { return Results.UnprocessableEntity(new { error = ex.Code }); }
        }).RequireRateLimiting("external");

        // Sending pipeline source to a third party is a data-governance decision: superadmin only, recorded with who and when.
        endpoints.MapPut("/{id:guid}/source-consent", async Task<IResult> (Guid id, AiConsentInput input,
            AiConnectionService service, ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                var actor = user.Identity?.Name ?? "unknown";
                var result = await service.SetSourceConsentAsync(id, input.SendSourceContent, actor, ct);
                if (result is null) return Results.NotFound();
                logs.CreateLogger("AiConnections").LogInformation(
                    "AI source-content consent changed Id={ConnectionId} Allowed={Allowed} By={Actor}", id, result.SendSourceContent, result.ConsentBy);
                return Results.Ok(ToResponse(result));
            }
            catch (AiConnectionException ex) { return Results.UnprocessableEntity(new { error = ex.Code }); }
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin" });

        endpoints.MapDelete("/{id:guid}", async Task<IResult> (Guid id, AiConnectionService service,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!await service.DeleteAsync(id, ct)) return Results.NotFound();
            logs.CreateLogger("AiConnections").LogInformation("AI connection deleted Id={ConnectionId}", id);
            return Results.NoContent();
        });
    }

    // Never carries the API key or the secret-store reference.
    private static AiConnectionResponse ToResponse(AiConnection x) => new(x.Id, x.Name, x.Provider.ToString(), x.Endpoint, x.Model,
        x.Auth.ToString(), x.ApiVersion, x.TokenParameter.ToString(), x.MaxOutputTokens, x.Temperature, x.TimeoutSeconds, x.MaxInputBytes,
        x.CreatedAtUtc, x.VerifiedAtUtc, x.VerificationCode, x.VerifiedModel, x.SendSourceContent, x.ConsentAtUtc, x.ConsentBy);
}

public sealed record AiConnectionInput(string? Name, string? Provider, string? Endpoint, string? Model, string? Auth, string? ApiKey = null,
    string? ApiVersion = null, string? TokenParameter = null, int? MaxOutputTokens = null, bool? SendTemperature = null,
    double? Temperature = null, int? TimeoutSeconds = null, int? MaxInputBytes = null)
{
    public AiConnectionDraft ToDraft() => new(Name ?? "", ParseEnum<AiProvider>(Provider), Endpoint ?? "", Model ?? "", ParseEnum<AiAuthMode>(Auth),
        string.IsNullOrEmpty(ApiKey) ? null : ApiKey, string.IsNullOrWhiteSpace(ApiVersion) ? null : ApiVersion,
        string.IsNullOrWhiteSpace(TokenParameter) ? null : ParseEnum<TokenLimitParameter>(TokenParameter),
        MaxOutputTokens, SendTemperature ?? true, Temperature, TimeoutSeconds, MaxInputBytes);

    // Numbers and unknown names are rejected (ArgumentException -> 400) rather than silently becoming an enum value.
    private static T ParseEnum<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && !int.TryParse(value, out _) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Unknown value.");
}

public sealed record AiRotateInput(string? ApiKey);
public sealed record AiConsentInput(bool SendSourceContent);
public sealed record AiOptionsResponse(IReadOnlyList<string> Providers, IReadOnlyList<string> AllowedHosts, bool AllowHttp, bool ManagedIdentityAvailable);
public sealed record AiConnectionResponse(Guid Id, string Name, string Provider, string Endpoint, string Model, string Auth, string? ApiVersion,
    string TokenParameter, int MaxOutputTokens, double? Temperature, int TimeoutSeconds, int MaxInputBytes, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? VerifiedAtUtc, string VerificationCode, string? VerifiedModel, bool SendSourceContent, DateTimeOffset? ConsentAtUtc, string? ConsentBy);

public sealed class AzureManagedIdentityTokenSource : IManagedIdentityTokenSource
{
    private static readonly DefaultAzureCredential Credential = new();
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    public async Task<string> GetTokenAsync(CancellationToken ct) =>
        (await Credential.GetTokenAsync(new TokenRequestContext(Scopes), ct)).Token;
}
