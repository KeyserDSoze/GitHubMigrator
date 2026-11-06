using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// A transfer *preparation* never contains an executable command, OAuth
// token, Git credential, URL or any ref. The original F-044 packet remains
// review-only even when this attempt receives an independent acknowledgement.
public sealed class GitHubTransferAttemptRecord
{
    public Guid Id { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ScopeId { get; set; }
    public long RepositoryId { get; set; }
    public string Organization { get; set; } = "";
    public string Repository { get; set; } = "";
    public string PacketSha256 { get; set; } = "";
    public string PolicySha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public int CandidateCount { get; set; }
    public DateTimeOffset PolicyObservedAtUtc { get; set; }
    public Guid LeaseToken { get; set; }
    public long LeaseGeneration { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? BlockedAtUtc { get; set; }
    public DateTimeOffset? ExpiredAtUtc { get; set; }
    public string State { get; set; } = "";
    public bool TransportAttempted { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitHubTransferAttemptEventRecord
{
    public long Id { get; set; }
    public Guid AttemptId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// Both the F-045 lease and the F-047 attempt use identical repository-level
// PostgreSQL advisory locks, including first insert, across API replicas.
public sealed class PostgresGitHubTransferAttemptStore(
    AppDbContext db, TimeProvider clock) : IGitHubTransferAttemptStore
{
    public async Task<GitHubTransferAttemptView> PrepareAsync(
        GitHubTransferAttemptDraft draft, CancellationToken ct)
    {
        if (draft is null || draft.RepositoryId <= 0 ||
            draft.PreparationId == Guid.Empty || draft.PlanId == Guid.Empty ||
            draft.ScopeId == Guid.Empty || draft.RequestedBy == Guid.Empty ||
            draft.LeaseToken == Guid.Empty || draft.LeaseGeneration <= 0 ||
            draft.CandidateCount is < 1 or > GitHubTransferAttemptIntegrity.MaxCandidates ||
            !GitSourceBundleStorageCheck.ValidSha(draft.PacketSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(draft.PolicySha256) ||
            !GitSourceBundleStorageCheck.ValidSha(draft.CandidateSetSha256))
            throw new GitHubTransferAttemptException("TransferPreparationInvalid");

        var now = clock.GetUtcNow();
        if (draft.PolicyObservedAtUtc > now.AddSeconds(5) ||
            now >= draft.PolicyObservedAtUtc.Add(GitHubTransferAttemptIntegrity.MaxReviewAge))
            throw new GitHubTransferAttemptException("TransferPreparationPolicyExpired");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockRepositoryAsync(draft.RepositoryId, ct);
        await ExpireStaleForRepositoryAsync(draft.RepositoryId, now, ct);
        if (!await LeaseMatchesAsync(draft.RepositoryId, draft.RequestedBy,
            draft.LeaseToken, draft.LeaseGeneration, draft.PacketSha256,
            draft.Organization, draft.Repository, now, ct))
            throw new GitHubTransferAttemptException("TransferPreparationLeaseLost");

        var existing = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeId == draft.ScopeId, ct);
        if (existing is not null)
        {
            if (existing.PreparationId != draft.PreparationId ||
                existing.PlanId != draft.PlanId ||
                existing.RepositoryId != draft.RepositoryId ||
                existing.RequestedBy != draft.RequestedBy ||
                existing.PacketSha256 != draft.PacketSha256 ||
                existing.PolicySha256 != draft.PolicySha256 ||
                existing.CandidateSetSha256 != draft.CandidateSetSha256 ||
                existing.CandidateCount != draft.CandidateCount ||
                existing.LeaseToken != draft.LeaseToken ||
                existing.LeaseGeneration != draft.LeaseGeneration)
                throw new GitHubTransferAttemptException("TransferPreparationConflictingReplay");
            await tx.CommitAsync(ct);
            return Map(existing);
        }

        // Source-side prep cannot be concurrent with any other F-047
        // work in progress for this globally identified GitHub repo.
        if (await db.GitHubTransferAttempts.AsNoTracking().AnyAsync(x =>
            x.RepositoryId == draft.RepositoryId &&
            (x.State == GitHubTransferAttemptStates.AwaitingIndependentReview ||
             x.State == GitHubTransferAttemptStates.Reviewed), ct))
            throw new GitHubTransferAttemptException("TransferPreparationBusy");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempts (
                id, preparation_id, plan_id, scope_id, repository_id,
                organization, repository, packet_sha256, policy_sha256,
                candidate_set_sha256, candidate_count, policy_observed_at_utc,
                lease_token, lease_generation, requested_by, reviewed_by,
                created_at_utc, reviewed_at_utc, blocked_at_utc, expired_at_utc, state,
                transport_attempted, execution_authorized,
                remote_writes_performed, ready_for_execution)
            VALUES ({id}, {draft.PreparationId}, {draft.PlanId},
                {draft.ScopeId}, {draft.RepositoryId},
                {draft.Organization}, {draft.Repository},
                {draft.PacketSha256}, {draft.PolicySha256},
                {draft.CandidateSetSha256}, {draft.CandidateCount},
                {draft.PolicyObservedAtUtc}, {draft.LeaseToken},
                {draft.LeaseGeneration}, {draft.RequestedBy}, NULL,
                {now}, NULL, NULL, NULL, 'AwaitingIndependentReview',
                false, false, false, false)
            """, ct);
        await EventAsync(id, "Prepared", draft.RequestedBy, now, ct);
        await tx.CommitAsync(ct);
        var saved = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == id, ct);
        return Map(saved);
    }

    public async Task<GitHubTransferAttemptView> DecideAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid actorId, string decision, CancellationToken ct)
    {
        if (actorId == Guid.Empty ||
            decision is not ("acknowledge-dry-run" or "reject"))
            throw new GitHubTransferAttemptException("TransferPreparationDecisionInvalid");
        var row = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attemptId &&
                x.PreparationId == preparationId && x.PlanId == planId &&
                x.ScopeId == scopeId, ct)
            ?? throw new KeyNotFoundException("TransferPreparationNotFound");
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockRepositoryAsync(row.RepositoryId, ct);
        if (row.RequestedBy == actorId)
            throw new GitHubTransferAttemptException("TransferPreparationTwoPersonRequired");

        // The reviewer cannot act after lease expiry or token/epoch change.
        // This acknowledgement does NOT establish GitHub write authority.
        if (!await LeaseMatchesAsync(row.RepositoryId, row.RequestedBy,
            row.LeaseToken, row.LeaseGeneration, row.PacketSha256,
            row.Organization, row.Repository, now, ct))
            throw new GitHubTransferAttemptException("TransferPreparationLeaseLost");

        var next = decision == "reject"
            ? GitHubTransferAttemptStates.Rejected
            : GitHubTransferAttemptStates.Reviewed;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH changed AS (
                UPDATE github_transfer_attempts
                SET state = {next}, reviewed_by = {actorId},
                    reviewed_at_utc = {now}
                WHERE id = {attemptId} AND preparation_id = {preparationId}
                    AND plan_id = {planId} AND scope_id = {scopeId}
                    AND state = 'AwaitingIndependentReview'
                    AND requested_by <> {actorId}
                    AND transport_attempted = false AND execution_authorized = false
                    AND remote_writes_performed = false AND ready_for_execution = false
                RETURNING id
            )
            INSERT INTO github_transfer_attempt_events
                (attempt_id, type, actor_id, occurred_at_utc)
            SELECT id, {next}, {actorId}, {now} FROM changed
            """, ct);
        if (changed != 1)
            throw new GitHubTransferAttemptException("TransferPreparationAlreadyDecided");
        await tx.CommitAsync(ct);
        return Map((await db.GitHubTransferAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == attemptId, ct)));
    }

    public async Task<bool> ExpireNextStaleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var repositoryId = await db.GitHubTransferAttempts.AsNoTracking()
            .Where(a => a.State == GitHubTransferAttemptStates.AwaitingIndependentReview
                && !db.GitHubTargetPreviewLeases.Any(l =>
                    l.RepositoryId == a.RepositoryId &&
                    l.State == GitHubTargetPreviewLeaseIntegrity.Held &&
                    l.LeaseToken == a.LeaseToken &&
                    l.Generation == a.LeaseGeneration &&
                    l.ActorId == a.RequestedBy &&
                    l.PacketSha256 == a.PacketSha256 &&
                    l.LeaseUntilUtc > now))
            .OrderBy(a => a.CreatedAtUtc)
            .Select(a => (long?)a.RepositoryId).FirstOrDefaultAsync(ct);
        if (repositoryId is null)
        {
            await tx.CommitAsync(ct);
            return false;
        }
        await LockRepositoryAsync(repositoryId.Value, ct);
        var changed = await ExpireStaleForRepositoryAsync(
            repositoryId.Value, now, ct);
        await tx.CommitAsync(ct);
        return changed > 0;
    }

    // Expiration is always read-only. This CAS can run both on worker polls
    // and proactively during a fresh prepare, so disabling background
    // polling never leaves an abandoned unique active target slot forever.
    private Task<int> ExpireStaleForRepositoryAsync(
        long repositoryId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                UPDATE github_transfer_attempts a
                SET state = 'Expired', expired_at_utc = {now}
                WHERE a.repository_id = {repositoryId}
                  AND a.state = 'AwaitingIndependentReview'
                  AND a.transport_attempted = false
                  AND a.execution_authorized = false
                  AND a.remote_writes_performed = false
                  AND a.ready_for_execution = false
                  AND NOT EXISTS (
                      SELECT 1 FROM github_target_preview_leases l
                      WHERE l.repository_id = a.repository_id
                        AND l.state = 'Held'
                        AND l.lease_token = a.lease_token
                        AND l.generation = a.lease_generation
                        AND l.actor_id = a.requested_by
                        AND l.packet_sha256 = a.packet_sha256
                        AND l.lease_until_utc > {now}
                  )
                RETURNING a.id
            )
            INSERT INTO github_transfer_attempt_events
                (attempt_id, type, actor_id, occurred_at_utc)
            SELECT id, 'LeaseExpired', {Guid.Empty}, {now} FROM expired
            """, ct);

    public async Task<GitHubTransferAttemptView?> BlockNextAtWriteBarrierAsync(
        CancellationToken ct)
    {
        // Atomic cross-replica claim. Even when a future worker is added,
        // this version has NO transport injection and always stops before
        // the first remote send. A timeout cannot trigger a retry of a push.
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = await db.GitHubTransferAttempts.AsNoTracking()
            .Where(x => x.State == GitHubTransferAttemptStates.Reviewed)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (id is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH blocked AS (
                UPDATE github_transfer_attempts a
                SET state = 'BlockedAtWriteBarrier',
                    blocked_at_utc = {now}
                WHERE a.id = {id.Value}
                    AND a.state = 'Reviewed'
                    AND a.transport_attempted = false
                    AND a.execution_authorized = false
                    AND a.remote_writes_performed = false
                    AND a.ready_for_execution = false
                RETURNING a.id
            )
            INSERT INTO github_transfer_attempt_events
                (attempt_id, type, actor_id, occurred_at_utc)
            SELECT id, 'WriteBarrierBlocked', {Guid.Empty}, {now} FROM blocked
            """, ct);
        await tx.CommitAsync(ct);
        if (changed == 0) return null;
        // Only this exact selected attempt can be reported. A competing
        // replica may have updated the same row first: then changed == 0.
        return Map(await db.GitHubTransferAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == id.Value &&
                x.State == GitHubTransferAttemptStates.BlockedAtWriteBarrier, ct));
    }

    public async Task<GitHubTransferAttemptView?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        CancellationToken ct)
    {
        var row = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attemptId &&
                x.PreparationId == preparationId && x.PlanId == planId &&
                x.ScopeId == scopeId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitHubTransferAttemptEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        CancellationToken ct)
    {
        if (await GetAsync(preparationId, planId, scopeId, attemptId, ct) is null)
            throw new KeyNotFoundException("TransferPreparationNotFound");
        return (await db.GitHubTransferAttemptEvents.AsNoTracking()
            .Where(x => x.AttemptId == attemptId)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct))
            .OrderBy(x => x.Id)
            .Select(x => new GitHubTransferAttemptEvent(
                x.Id, x.AttemptId, x.Type, x.ActorId, x.OccurredAtUtc)).ToArray();
    }

    private async Task<bool> LeaseMatchesAsync(
        long repositoryId, Guid actorId, Guid leaseToken,
        long generation, string packetSha256, string organization,
        string repository, DateTimeOffset now, CancellationToken ct)
    {
        return await db.GitHubTargetPreviewLeases.AsNoTracking().AnyAsync(x =>
            x.RepositoryId == repositoryId &&
            x.State == GitHubTargetPreviewLeaseIntegrity.Held &&
            x.LeaseUntilUtc > now && x.ActorId == actorId &&
            x.LeaseToken == leaseToken && x.Generation == generation &&
            x.PacketSha256 == packetSha256 &&
            x.Organization == organization &&
            x.Repository == repository &&
            !x.ExecutionAuthorized && !x.RemoteWritesPerformed &&
            !x.ReadyForExecution, ct);
    }

    private Task<int> LockRepositoryAsync(
        long repositoryId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repositoryId.ToString()}))", ct);

    private Task<int> EventAsync(
        Guid id, string kind, Guid actorId, DateTimeOffset now,
        CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_transfer_attempt_events
                (attempt_id, type, actor_id, occurred_at_utc)
            VALUES ({id}, {kind}, {actorId}, {now})
            """, ct);

    private static GitHubTransferAttemptView Map(
        GitHubTransferAttemptRecord x) => new(
            x.Id, x.PreparationId, x.PlanId, x.ScopeId, x.RepositoryId,
            x.Organization, x.Repository, x.PacketSha256, x.PolicySha256,
            x.CandidateSetSha256, x.CandidateCount, x.LeaseGeneration,
            x.State, x.RequestedBy, x.ReviewedBy, x.CreatedAtUtc,
            x.ReviewedAtUtc, x.BlockedAtUtc, x.ExpiredAtUtc,
            x.TransportAttempted,
            x.ExecutionAuthorized, x.RemoteWritesPerformed,
            x.ReadyForExecution);
}
