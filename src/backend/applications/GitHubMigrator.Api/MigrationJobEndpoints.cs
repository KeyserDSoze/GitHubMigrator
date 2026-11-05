using System.Security.Claims;
using GitHubMigrator.Business;
using Microsoft.AspNetCore.Authorization;

public static class MigrationJobEndpoints
{
    public static void MapMigrationJobEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/jobs")
            .RequireAuthorization("PasswordChanged")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "superadmin,admin" });

        routes.MapPost("/staging", async Task<IResult> (
            StageSnapshotJobRequest request, ClaimsPrincipal principal,
            MigrationJobService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
                return Results.Unauthorized();
            try
            {
                var job = await service.EnqueueStageAsync(request.SnapshotId, actorId, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "SnapshotNotFound" }); }
            catch (MigrationJobInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireRateLimiting("staging-sync");

        routes.MapPost("/discovery", async Task<IResult> (
            DiscoverSourceJobRequest request, ClaimsPrincipal principal,
            MigrationJobService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
                return Results.Unauthorized();
            try
            {
                var job = await service.EnqueueDiscoveryAsync(
                    request.ConnectionId, actorId, request.RequestId, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "ConnectionNotFound" }); }
            catch (MigrationJobInputException ex) { return Results.BadRequest(new { error = ex.Code }); }
        }).RequireRateLimiting("discovery-queue");

        routes.MapGet("/{jobId:guid}", async Task<IResult> (
            Guid jobId, MigrationJobService service, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            return job is null ? Results.NotFound(new { error = "JobNotFound" }) : Results.Ok(job);
        });

        routes.MapGet("/{jobId:guid}/ref-progress", async Task<IResult> (
            Guid jobId, MigrationJobService service,
            IDiscoveryRefIngestProgress observations, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            return Results.Ok(await observations.GetAsync(jobId, ct));
        });

        routes.MapGet("/{jobId:guid}/group-progress", async Task<IResult> (
            Guid jobId, MigrationJobService service,
            IDiscoveryGroupObservationStore groups, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            return Results.Ok(await groups.GetProgressAsync(jobId, ct));
        });

        routes.MapGet("/{jobId:guid}/groups", async Task<IResult> (
            Guid jobId, string? after, int? take, MigrationJobService service,
            IDiscoveryGroupObservationStore groups, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            if (job.State != MigrationJobStates.Succeeded)
                return Results.Conflict(new { error = "DiscoveryNotCompleted" });
            try
            {
                return Results.Ok(await groups.ListAsync(jobId, after, take ?? 50, ct));
            }
            catch (DiscoveryCheckpointException ex)
                when (ex.Code == "InvalidGroupObservationPage")
            {
                return Results.BadRequest(new { error = ex.Code });
            }
        });

        routes.MapGet("/{jobId:guid}/acl-progress", async Task<IResult> (
            Guid jobId, MigrationJobService service,
            IDiscoveryAclEvidenceStore evidence, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            return Results.Ok(await evidence.GetProgressAsync(jobId, ct));
        });

        routes.MapGet("/{jobId:guid}/acls", async Task<IResult> (
            Guid jobId, string? after, int? take, MigrationJobService service,
            IDiscoveryAclEvidenceStore evidence, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            if (job.State != MigrationJobStates.Succeeded)
                return Results.Conflict(new { error = "DiscoveryNotCompleted" });
            try { return Results.Ok(await evidence.ListAsync(jobId, after, take ?? 50, ct)); }
            catch (DiscoveryCheckpointException ex)
                when (ex.Code == "InvalidAclObservationPage")
            {
                return Results.BadRequest(new { error = ex.Code });
            }
        });

        routes.MapGet("/{jobId:guid}/membership-progress", async Task<IResult> (
            Guid jobId, MigrationJobService service,
            IDiscoveryMembershipEvidenceVerifier memberships, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            return Results.Ok(await memberships.GetProgressAsync(jobId, ct));
        });

        routes.MapGet("/{jobId:guid}/memberships", async Task<IResult> (
            Guid jobId, string? after, int? take, MigrationJobService service,
            IDiscoveryMembershipEvidenceVerifier memberships, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            if (job.State != MigrationJobStates.Succeeded)
                return Results.Conflict(new { error = "DiscoveryNotCompleted" });
            try
            {
                return Results.Ok(await memberships.ListAsync(jobId, after, take ?? 50, ct));
            }
            catch (DiscoveryCheckpointException ex)
                when (ex.Code == "InvalidMembershipObservationPage")
            {
                return Results.BadRequest(new { error = ex.Code });
            }
        });

        routes.MapGet("/{jobId:guid}/identity-progress", async Task<IResult> (
            Guid jobId, MigrationJobService service,
            IDiscoveryIdentityObservationStore observations, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            return Results.Ok(await observations.GetProgressAsync(jobId, ct));
        });

        routes.MapGet("/{jobId:guid}/identities", async Task<IResult> (
            Guid jobId, string? after, int? take, MigrationJobService service,
            IDiscoveryIdentityObservationStore observations, CancellationToken ct) =>
        {
            var job = await service.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            // Descriptors, display names and addresses must not be exposed
            // as an apparent verified inventory before discovery completes.
            if (job.State != MigrationJobStates.Succeeded)
                return Results.Conflict(new { error = "DiscoveryNotCompleted" });
            try
            {
                return Results.Ok(await observations.ListAsync(jobId, after, take ?? 50, ct));
            }
            catch (DiscoveryCheckpointException ex)
                when (ex.Code == "InvalidIdentityObservationPage")
            {
                return Results.BadRequest(new { error = ex.Code });
            }
        });

        routes.MapGet("/{jobId:guid}/ref-manifest", async Task<IResult> (
            Guid jobId, MigrationJobService jobs,
            IDiscoveryRefManifestStore manifests, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            if (job.State != MigrationJobStates.Succeeded)
                return Results.Conflict(new { error = "DiscoveryNotCompleted" });
            var manifest = await manifests.GetAsync(jobId, ct);
            return manifest is null
                ? Results.NotFound(new { error = "RefManifestUnavailable" })
                : Results.Ok(manifest);
        });

        routes.MapGet("/{jobId:guid}/ref-manifest/chunks", async Task<IResult> (
            Guid jobId, int? after, int? take, MigrationJobService jobs,
            IDiscoveryRefManifestStore manifests, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            if (job.State != MigrationJobStates.Succeeded)
                return Results.Conflict(new { error = "DiscoveryNotCompleted" });
            try
            {
                return Results.Ok(await manifests.ListChunksAsync(
                    jobId, after ?? -1, take ?? 20, ct));
            }
            catch (DiscoveryRefManifestException ex) when (ex.Code == "InvalidRefChunkPage")
            {
                return Results.BadRequest(new { error = ex.Code });
            }
            catch (DiscoveryRefManifestException ex)
            {
                return Results.Conflict(new { error = ex.Code });
            }
        });

        routes.MapGet("/{jobId:guid}/ref-manifest/chunks/{index:int}", async Task<IResult> (
            Guid jobId, int index, MigrationJobService jobs,
            IDiscoveryRefManifestStore manifests, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { error = "JobNotFound" });
            if (job.Kind != MigrationJobKinds.DiscoverSource)
                return Results.BadRequest(new { error = "UnsupportedJobKind" });
            if (job.State != MigrationJobStates.Succeeded)
                return Results.Conflict(new { error = "DiscoveryNotCompleted" });
            try
            {
                var chunk = await manifests.ReadChunkAsync(jobId, index, ct);
                return chunk is null
                    ? Results.NotFound(new { error = "RefChunkNotFound" })
                    : Results.Ok(chunk);
            }
            catch (DiscoveryRefManifestException ex) when (ex.Code == "InvalidRefChunkIndex")
            {
                return Results.BadRequest(new { error = ex.Code });
            }
            catch (DiscoveryRefManifestException ex)
            {
                return Results.Conflict(new { error = ex.Code });
            }
        });

        routes.MapGet("/{jobId:guid}/events", async Task<IResult> (
            Guid jobId, MigrationJobService service, CancellationToken ct) =>
        {
            if (await service.GetAsync(jobId, ct) is null)
                return Results.NotFound(new { error = "JobNotFound" });
            return Results.Ok(await service.EventsAsync(jobId, ct));
        });

        routes.MapGet("/connections/{connectionId:guid}", async (
            Guid connectionId, MigrationJobService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(connectionId, ct)));
    }
}

public sealed record StageSnapshotJobRequest(Guid SnapshotId);
public sealed record DiscoverSourceJobRequest(Guid ConnectionId, Guid RequestId);
