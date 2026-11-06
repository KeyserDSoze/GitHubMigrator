using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteTransmitHazardRecord
{
    public Guid Id { get; set; }
    public Guid GrantId { get; set; }
    public long RepositoryId { get; set; }
    public Guid FenceRequestId { get; set; }
    public long FenceGeneration { get; set; }
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string AbortTokenSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public Guid PreparedBy { get; set; }
    public Guid? MarkedBy { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool TransportAuthorized { get; set; }
    public bool TransportInvoked { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool MayAutomaticallyRetry { get; set; }
}

public sealed class GitHubRemoteTransmitHazardEventRecord
{
    public long Id { get; set; }
    public Guid HazardId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// F-055 intentionally has no GitHub transport dependency and no API route.
// Every state remains permanently write-disabled at the PostgreSQL layer.
public sealed class PostgresGitHubRemoteTransmitHazardJournal(
    AppDbContext db, TimeProvider clock,
    GitHubRemoteTransmitHazardOptions options)
    : IGitHubRemoteTransmitHazardJournal
{
    public async Task<GitHubRemoteTransmitHazardClaim> PrepareAsync(
        Guid grantId, long repositoryId, Guid actorId, CancellationToken ct)
    {
        AssertAllowed(repositoryId);
        if (grantId == Guid.Empty || actorId == Guid.Empty)
            throw new GitHubRemoteTransmitHazardException("RemoteHazardHandleInvalid");
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repositoryId, ct);
        if (await QuarantineLockedAsync(repositoryId, now, ct) > 0)
        {
            await tx.CommitAsync(ct);
            throw new GitHubRemoteTransmitHazardException("RemoteHazardRecoveryRequired");
        }
        var grant = await VerifyBarrierAsync(grantId, repositoryId, actorId, now, ct);
        if (await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .AnyAsync(x => x.GrantId == grantId, ct))
            throw new GitHubRemoteTransmitHazardException("RemoteHazardEpochConsumed");
        if (await db.GitHubRemoteTransmitHazards.AsNoTracking().AnyAsync(x =>
            x.RepositoryId == repositoryId &&
            (x.State == GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
             x.State == GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
             x.State == GitHubRemoteTransmitHazardStates.OperatorReviewRequired), ct))
            throw new GitHubRemoteTransmitHazardException("RemoteHazardRepositoryBusy");

        var token = Guid.NewGuid();
        var row = new GitHubRemoteTransmitHazardRecord
        {
            Id = Guid.NewGuid(),
            GrantId = grantId,
            RepositoryId = repositoryId,
            FenceRequestId = grant.FenceRequestId,
            FenceGeneration = grant.FenceGeneration,
            PacketSha256 = grant.PacketSha256,
            CandidateSetSha256 = grant.CandidateSetSha256,
            BaselineSha256 = grant.BaselineSha256,
            AbortTokenSha256 = GitHubRemoteTransmitHazardIntegrity.AbortTokenDigest(token),
            State = GitHubRemoteTransmitHazardStates.PreparedWriteDisabled,
            PreparedBy = actorId,
            DeadlineUtc = grant.DeadlineUtc,
            UpdatedAtUtc = now,
            TransportAuthorized = false,
            TransportInvoked = false,
            RemoteWritesPerformed = false,
            MayAutomaticallyRetry = false
        };
        db.GitHubRemoteTransmitHazards.Add(row);
        db.GitHubRemoteTransmitHazardEvents.Add(new()
        {
            HazardId = row.Id, Type = row.State,
            ActorId = actorId, OccurredAtUtc = now
        });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg &&
            pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GitHubRemoteTransmitHazardException("RemoteHazardRepositoryBusy");
        }
        return new(Map(row), token);
    }

    public async Task<GitHubRemoteTransmitHazardStatus> MarkMayHaveTransmittedAsync(
        Guid hazardId, long repositoryId, Guid actorId, Guid abortToken,
        CancellationToken ct)
    {
        RequireHandle(hazardId, repositoryId, actorId, abortToken);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repositoryId, ct);
        if (await QuarantineLockedAsync(repositoryId, now, ct) > 0)
        {
            await tx.CommitAsync(ct);
            throw new GitHubRemoteTransmitHazardException("RemoteHazardRecoveryRequired");
        }

        var row = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == hazardId &&
                x.RepositoryId == repositoryId, ct)
            ?? throw new GitHubRemoteTransmitHazardException("RemoteHazardNotFound");
        var digest = GitHubRemoteTransmitHazardIntegrity.AbortTokenDigest(abortToken);
        if (row.State != GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            row.PreparedBy != actorId || row.AbortTokenSha256 != digest ||
            row.DeadlineUtc <= now)
            throw new GitHubRemoteTransmitHazardException("RemoteHazardStaleOrAlreadyMarked");

        var grant = await VerifyBarrierAsync(row.GrantId, repositoryId, actorId, now, ct);
        if (grant.FenceRequestId != row.FenceRequestId ||
            grant.FenceGeneration != row.FenceGeneration ||
            grant.PacketSha256 != row.PacketSha256 ||
            grant.CandidateSetSha256 != row.CandidateSetSha256 ||
            grant.BaselineSha256 != row.BaselineSha256)
            throw new GitHubRemoteTransmitHazardException("RemoteHazardBarrierChanged");

        // The future sender MUST commit MayHaveTransmitted BEFORE any Git I/O.
        // This method *only* stores uncertainty; it returns no send permission.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH marked AS (
              UPDATE github_remote_transmit_hazards
              SET state='MayHaveTransmitted',
                  marked_by={actorId}, updated_at_utc={now}
              WHERE id={hazardId} AND repository_id={repositoryId}
                AND grant_id={grant.Id}
                AND state='PreparedWriteDisabled'
                AND deadline_utc>{now}
                AND prepared_by={actorId}
                AND abort_token_sha256={digest}
                AND transport_authorized=false
                AND transport_invoked=false
                AND remote_writes_performed=false
                AND may_automatically_retry=false
              RETURNING id
            )
            INSERT INTO github_remote_transmit_hazard_events
              (hazard_id,type,actor_id,occurred_at_utc)
            SELECT id,'MayHaveTransmitted',{actorId},{now} FROM marked
            """, ct);
        if (changed != 1)
            throw new GitHubRemoteTransmitHazardException("RemoteHazardTransitionLost");
        await tx.CommitAsync(ct);
        return Map(await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleAsync(x => x.Id == hazardId && x.RepositoryId == repositoryId, ct));
    }

    public async Task<bool> AbortBeforeTransportAsync(
        Guid hazardId, long repositoryId, Guid actorId, Guid abortToken,
        CancellationToken ct)
    {
        RequireHandle(hazardId, repositoryId, actorId, abortToken);
        var now = clock.GetUtcNow();
        var digest = GitHubRemoteTransmitHazardIntegrity.AbortTokenDigest(abortToken);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repositoryId, ct);
        await QuarantineLockedAsync(repositoryId, now, ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH aborted AS (
              UPDATE github_remote_transmit_hazards
              SET state='AbortedBeforeTransport', updated_at_utc={now}
              WHERE id={hazardId} AND repository_id={repositoryId}
                AND prepared_by={actorId}
                AND abort_token_sha256={digest}
                AND state='PreparedWriteDisabled'
                AND deadline_utc>{now}
                AND transport_authorized=false AND transport_invoked=false
                AND remote_writes_performed=false AND may_automatically_retry=false
              RETURNING id
            )
            INSERT INTO github_remote_transmit_hazard_events
              (hazard_id,type,actor_id,occurred_at_utc)
            SELECT id,'AbortedBeforeTransport',{actorId},{now} FROM aborted
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<GitHubRemoteTransmitHazardStatus?> GetAsync(
        Guid hazardId, long repositoryId, CancellationToken ct)
    {
        AssertAllowed(repositoryId);
        var row = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == hazardId &&
                x.RepositoryId == repositoryId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitHubRemoteTransmitHazardEvent>> EventsAsync(
        Guid hazardId, long repositoryId, CancellationToken ct)
    {
        AssertAllowed(repositoryId);
        if (!await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .AnyAsync(x => x.Id == hazardId && x.RepositoryId == repositoryId, ct))
            throw new GitHubRemoteTransmitHazardException("RemoteHazardNotFound");
        return (await db.GitHubRemoteTransmitHazardEvents.AsNoTracking()
            .Where(x => x.HazardId == hazardId).OrderBy(x => x.Id)
            .Take(50).ToArrayAsync(ct))
            .Select(x => new GitHubRemoteTransmitHazardEvent(
                x.Id, x.HazardId, x.Type, x.ActorId, x.OccurredAtUtc)).ToArray();
    }

    public async Task<bool> QuarantineExpiredAsync(CancellationToken ct)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0)
            return false;
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(options.AllowedRepositoryId, ct);
        var changed = await QuarantineLockedAsync(options.AllowedRepositoryId, now, ct);
        await tx.CommitAsync(ct);
        return changed > 0;
    }

    private Task<int> QuarantineLockedAsync(
        long repositoryId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
              UPDATE github_remote_transmit_hazards
              SET state='OperatorReviewRequired',updated_at_utc={now}
              WHERE repository_id={repositoryId}
                AND state='PreparedWriteDisabled'
                AND deadline_utc<={now}
                AND transport_authorized=false AND transport_invoked=false
                AND remote_writes_performed=false AND may_automatically_retry=false
              RETURNING id
            )
            INSERT INTO github_remote_transmit_hazard_events
              (hazard_id,type,actor_id,occurred_at_utc)
            SELECT id,'OperatorReviewRequired',{Guid.Empty},{now} FROM expired
            """, ct);

    private async Task<GitHubRemoteTestGrantStatus> VerifyBarrierAsync(
        Guid grantId, long repoId, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grantId &&
                x.RepositoryId == repoId, ct)
            ?? throw new GitHubRemoteTransmitHazardException("RemoteHazardGrantNotFound");
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repoId, ct)
            ?? throw new GitHubRemoteTransmitHazardException("RemoteHazardFenceNotFound");
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.AttemptId, ct)
            ?? throw new GitHubRemoteTransmitHazardException("RemoteHazardAttemptNotFound");

        var g = new GitHubRemoteTestGrantStatus(
            grant.Id, grant.RepositoryId, grant.Organization, grant.Repository,
            grant.PreparationId, grant.PlanId, grant.ScopeId, grant.AttemptId,
            grant.FenceRequestId, grant.FenceGeneration, grant.PacketSha256,
            grant.CandidateSetSha256, grant.OriginalPolicySha256,
            grant.BaselineSha256, grant.PreflightSha256,
            grant.State, grant.RequestedBy, grant.ReviewedBy,
            grant.DeadlineUtc, grant.UpdatedAtUtc,
            grant.ExecutionAuthorized, grant.RemoteWritesPerformed,
            grant.ReadyForExecution);
        var f = new GitHubRemoteSandboxFenceStatus(
            fence.RequestId, fence.RepositoryId, fence.Organization,
            fence.Repository, fence.PreparationId, fence.PlanId,
            fence.ScopeId, fence.AttemptId, fence.PacketSha256,
            fence.CandidateSetSha256, fence.PolicySha256, fence.Generation,
            fence.State, fence.RequestedBy, fence.ReviewedBy,
            fence.DeadlineUtc, fence.UpdatedAtUtc, fence.ExecutionAuthorized,
            fence.RemoteWritesPerformed, fence.ReadyForExecution);
        var a = new GitHubTransferAttemptView(
            attempt.Id, attempt.PreparationId, attempt.PlanId, attempt.ScopeId,
            attempt.RepositoryId, attempt.Organization, attempt.Repository,
            attempt.PacketSha256, attempt.PolicySha256,
            attempt.CandidateSetSha256, attempt.CandidateCount,
            attempt.LeaseGeneration, attempt.State,
            attempt.RequestedBy, attempt.ReviewedBy, attempt.CreatedAtUtc,
            attempt.ReviewedAtUtc, attempt.BlockedAtUtc,
            attempt.ExpiredAtUtc, attempt.TransportAttempted,
            attempt.ExecutionAuthorized, attempt.RemoteWritesPerformed,
            attempt.ReadyForExecution);
        GitHubRemoteTransmitHazardIntegrity.RequireDependencies(
            g, f, a, options, actorId, now);
        return g;
    }

    private Task<int> LockAsync(long repoId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repoId.ToString()}))", ct);

    private void AssertAllowed(long repoId) =>
        GitHubRemoteTransmitHazardIntegrity.RequireAllowed(options, repoId);

    private void RequireHandle(Guid hazardId, long repoId, Guid actorId, Guid token)
    {
        AssertAllowed(repoId);
        if (hazardId == Guid.Empty || actorId == Guid.Empty || token == Guid.Empty)
            throw new GitHubRemoteTransmitHazardException("RemoteHazardHandleInvalid");
    }

    private static GitHubRemoteTransmitHazardStatus Map(
        GitHubRemoteTransmitHazardRecord x) =>
        new(x.Id, x.GrantId, x.RepositoryId, x.FenceRequestId,
            x.FenceGeneration, x.PacketSha256, x.CandidateSetSha256,
            x.BaselineSha256, x.State, x.PreparedBy, x.MarkedBy,
            x.DeadlineUtc, x.UpdatedAtUtc, x.TransportAuthorized,
            x.TransportInvoked, x.RemoteWritesPerformed,
            x.MayAutomaticallyRetry);
}
