using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

// F-065: internal read-only fleet diagnostics. It does not read or
// serialize source/target identities, tokens or mirror filesystem paths.
public static class GitSourceMirrorFleetHealthEndpoints
{
    public static void MapGitSourceMirrorFleetHealthEndpoints(
        this WebApplication app)
    {
        var routes = app.MapGroup("/api/operations/source-mirrors")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute {
                Roles = "superadmin"
            })
            .RequireRateLimiting("source-mirror-health");

        routes.MapGet("/health", async Task<IResult> (
            IGitSourceMirrorFleetHealthReader health,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await health.ReadAsync(ct));
            }
            catch (GitSourceMirrorLifecycleException ex) when
                (ex.Code == "SourceMirrorLifecycleDisabled")
            {
                // Default-off: do not advertise a feature's internal state
                // or leak any lifecycle metadata to the caller.
                return Results.NotFound();
            }
        });
    }
}
