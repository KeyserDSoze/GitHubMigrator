using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using Microsoft.AspNetCore.Authorization;

public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/discoveries")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        routes.MapPost("", async Task<IResult> (DiscoverRequest request, DiscoveryService service,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            try
            {
                // Synchronous MVP, bounded for App Service HTTP limits. Durable async worker is F-008.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                var inventory = await service.ScanAsync(request.ConnectionId, deadline.Token);
                loggerFactory.CreateLogger("Discovery").LogInformation(
                    "Source inventory collected InventoryId={InventoryId} ConnectionId={ConnectionId} Projects={ProjectCount} Refs={RefCount} IdentityCount={IdentityCount} Issues={IssueCount}",
                    inventory.Id, inventory.ConnectionId, inventory.Snapshot.Projects.Count,
                    inventory.Refs.Count, inventory.Snapshot.Identities.Count, inventory.Issues.Count);
                return Results.Created($"/api/discoveries/{inventory.Id}", Summary(inventory));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "ConnectionNotFound" }); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "UnsupportedSourceConnection" }); }
            catch (DiscoveryProviderException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Code });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.UnprocessableEntity(new { error = "DiscoveryTimeout" });
            }
        }).RequireRateLimiting("external");

        routes.MapGet("", async (DiscoveryService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(Summary)));

        routes.MapGet("/{id:guid}", async Task<IResult> (Guid id, DiscoveryService service, CancellationToken ct) =>
        {
            var inventory = await service.GetAsync(id, ct);
            return inventory is null ? Results.NotFound() : Results.Ok(inventory);
        });
    }

    private static object Summary(DiscoveryInventory x) => new
    {
        x.Id, x.ConnectionId, x.SourceProvider, x.Organization, x.CollectedAtUtc,
        completeness = x.Completeness.ToString().ToLowerInvariant(),
        projectCount = x.Snapshot.Projects.Count,
        repositoryCount = x.Snapshot.Projects.Sum(p => p.Repositories.Count),
        refCount = x.Refs.Count,
        identityCount = x.Snapshot.Identities.Count,
        groupCount = x.Groups.Count,
        membershipCount = x.Memberships.Count,
        aclEntryCount = x.AclEvidence.Count,
        x.SecurityCoverage,
        issueCount = x.Issues.Count,
        x.IdentitiesComplete, x.PermissionsComplete
    };
}
public sealed record DiscoverRequest(Guid ConnectionId);
