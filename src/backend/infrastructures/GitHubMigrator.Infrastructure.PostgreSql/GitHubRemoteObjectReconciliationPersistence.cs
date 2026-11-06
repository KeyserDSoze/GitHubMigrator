using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-058 only appends receipt hashes; it has no GitHub client, token,
// transport, retry queue or mutation of F-055's MayHaveTransmitted marker.
public sealed class PostgresGitHubRemoteObjectReconciliationJournal(
    AppDbContext db, TimeProvider clock,
    GitHubReadOnlyObjectMirrorOptions options)
    : IGitHubRemoteObjectReconciliationJournal
{
    public async Task<GitHubRemoteObjectReconciliationStatus> RecordAsync(
        GitHubRemoteObjectReconciliationEvidence input, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var decision = GitHubRemoteObjectReconciliationIntegrity.Verify(
            input, options, now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({input.RepositoryId.ToString()}))", ct);

        var prior = await db.GitHubRemoteObjectReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HazardId == input.HazardId, ct);
        if (prior is not null)
        {
            if (prior.GrantId != input.GrantId ||
                prior.ReadbackId != input.ReadbackId ||
                prior.RepositoryId != input.RepositoryId ||
                prior.EvidenceSha256 != decision.EvidenceSha256 ||
                prior.ObservedBy != input.ObserverId)
                throw new GitHubRemoteObjectReconciliationException(
                    "RemoteObjectReconciliationConflict");
            await tx.CommitAsync(ct);
            return Map(prior);
        }

        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.GrantId &&
                x.RepositoryId == input.RepositoryId, ct)
            ?? throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationGrantNotFound");
        var readback = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.ReadbackId &&
                x.GrantId == input.GrantId &&
                x.RepositoryId == input.RepositoryId, ct)
            ?? throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationReadbackNotFound");
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.HazardId &&
                x.GrantId == input.GrantId &&
                x.RepositoryId == input.RepositoryId, ct)
            ?? throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationHazardNotFound");
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == input.RepositoryId, ct);
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.AttemptId, ct);

        GitHubRemoteObjectReconciliationGuard.Require(
            input, decision, options, grant, readback, hazard, fence, attempt);

        // Historical event existence prevents manually changed state
        // labels from being silently treated as reviewed evidence.
        if (!await db.GitHubRemoteTransmitHazardEvents.AsNoTracking()
                .AnyAsync(x => x.HazardId == hazard.Id &&
                    x.Type == GitHubRemoteTransmitHazardStates.MayHaveTransmitted &&
                    x.OccurredAtUtc <= decision.VerifiedAtUtc, ct) ||
            !await db.GitHubRemoteReadbackEvents.AsNoTracking()
                .AnyAsync(x => x.ReadbackId == readback.Id &&
                    x.Type == "Recorded" &&
                    x.OccurredAtUtc <= decision.VerifiedAtUtc, ct))
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationAuditMissing");

        var row = new GitHubRemoteObjectReconciliationRecord
        {
            Id = Guid.NewGuid(),
            GrantId = input.GrantId,
            ReadbackId = input.ReadbackId,
            HazardId = input.HazardId,
            RepositoryId = input.RepositoryId,
            SourceSnapshotId = decision.SourceSnapshotId,
            State = decision.State,
            OriginalBaselineSha256 = decision.OriginalBaselineSha256,
            CandidateSetSha256 = decision.CandidateSetSha256,
            TargetRefSetSha256 = decision.TargetRefSetSha256,
            RemoteRefObservationSha256 = decision.RemoteRefObservationSha256,
            FullObjectClosureSha256 = decision.FullObjectClosureSha256,
            RemoteMirrorReceiptSha256 = decision.RemoteMirrorReceiptSha256,
            EvidenceSha256 = decision.EvidenceSha256,
            ObservedBy = input.ObserverId,
            VerifiedAtUtc = decision.VerifiedAtUtc,
            RecordedAtUtc = now,
            RemotePushInvoked = false,
            RemoteWritesAuthorized = false,
            MayAutomaticallyRetry = false,
            MigrationComplete = false
        };
        db.GitHubRemoteObjectReconciliations.Add(row);
        db.GitHubRemoteObjectReconciliationEvents.Add(new()
        {
            ReconciliationId = row.Id, Type = "Recorded",
            ActorId = input.ObserverId, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<GitHubRemoteObjectReconciliationStatus?> GetAsync(
        Guid hazardId, long repositoryId, CancellationToken ct)
    {
        AssertAllowed(repositoryId);
        var row = await db.GitHubRemoteObjectReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HazardId == hazardId &&
                x.RepositoryId == repositoryId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitHubRemoteObjectReconciliationEvent>>
        EventsAsync(Guid hazardId, long repositoryId, CancellationToken ct)
    {
        AssertAllowed(repositoryId);
        var row = await db.GitHubRemoteObjectReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HazardId == hazardId &&
                x.RepositoryId == repositoryId, ct)
            ?? throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationNotFound");
        return (await db.GitHubRemoteObjectReconciliationEvents.AsNoTracking()
            .Where(x => x.ReconciliationId == row.Id)
            .OrderBy(x => x.Id).Take(50).ToArrayAsync(ct))
            .Select(x => new GitHubRemoteObjectReconciliationEvent(
                x.Id, x.ReconciliationId, x.Type, x.ActorId,
                x.OccurredAtUtc)).ToArray();
    }

    private void AssertAllowed(long repositoryId)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            options.AllowedRepositoryId != repositoryId ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationDisabled");
    }

    private static GitHubRemoteObjectReconciliationStatus Map(
        GitHubRemoteObjectReconciliationRecord x) =>
        new(x.Id, x.GrantId, x.ReadbackId, x.HazardId,
            x.RepositoryId, x.SourceSnapshotId, x.State,
            x.OriginalBaselineSha256, x.CandidateSetSha256,
            x.TargetRefSetSha256, x.RemoteRefObservationSha256,
            x.FullObjectClosureSha256, x.RemoteMirrorReceiptSha256,
            x.EvidenceSha256, x.ObservedBy, x.VerifiedAtUtc,
            x.RecordedAtUtc, x.RemotePushInvoked,
            x.RemoteWritesAuthorized, x.MayAutomaticallyRetry,
            x.MigrationComplete);
}
