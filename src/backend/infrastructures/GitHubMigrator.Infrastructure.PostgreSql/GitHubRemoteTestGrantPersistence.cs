using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteTestGrantRecord
{
    public Guid Id { get; set; }
    public long RepositoryId { get; set; }
    public string Organization { get; set; } = "";
    public string Repository { get; set; } = "";
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ScopeId { get; set; }
    public Guid AttemptId { get; set; }
    public Guid FenceRequestId { get; set; }
    public long FenceGeneration { get; set; }
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string OriginalPolicySha256 { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public string PreflightSha256 { get; set; } = "";
    public string CancellationTokenSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public Guid RequestedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitHubRemoteTestGrantEventRecord
{
    public long Id { get; set; }
    public Guid GrantId { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// F-052 grants are NEVER SENDABLE. All SQL mutations are under F-050's
// numeric repository advisory lock. A future transport needs new schema,
// distinct authority and a reviewed test-repository allowlist.
public sealed class PostgresGitHubRemoteTestGrantStore(
    AppDbContext db, TimeProvider clock,
    GitHubRemoteTestGrantOptions options) : IGitHubRemoteTestGrantStore
{
    public async Task<GitHubRemoteTestGrantClaim> ProposeAsync(
        GitHubRemoteTestGrantEvidence input, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        GitHubRemoteTestGrantIntegrity.Validate(input, options, now);
        var f = input.Fence;
        var p = input.Preflight;
        var deadline = Min(f.DeadlineUtc,
            p.ObservedAtUtc.Add(GitHubRemoteTestGrantIntegrity.MaxPreflightAge));
        if (deadline <= now)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantEvidenceExpired");
        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var digest = GitHubRemoteTestGrantIntegrity.TokenDigest(token);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(f.RepositoryId, ct);
        if (await QuarantineLockedAsync(f.RepositoryId, now, ct) > 0)
        {
            await tx.CommitAsync(ct);
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantRecoveryRequired");
        }
        await VerifyDependenciesAsync(input, now, ct);

        var previous = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.FenceRequestId == f.RequestId &&
                x.FenceGeneration == f.Generation, ct);
        if (previous is not null)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantEpochConsumed");
        if (await db.GitHubRemoteTestGrants.AsNoTracking().AnyAsync(x =>
            x.RepositoryId == f.RepositoryId &&
            (x.State == GitHubRemoteTestGrantStates.Proposed ||
             x.State == GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
             x.State == GitHubRemoteTestGrantStates.RecoveryRequired), ct))
            throw new GitHubRemoteTestGrantException("RemoteTestGrantBusy");

        try
        {
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH grant_row AS (
                  INSERT INTO github_remote_test_grants (
                    id, repository_id, organization, repository,
                    preparation_id, plan_id, scope_id, attempt_id,
                    fence_request_id, fence_generation,
                    packet_sha256, candidate_set_sha256,
                    original_policy_sha256, baseline_sha256,
                    preflight_sha256, cancellation_token_sha256,
                    state, requested_by, reviewed_by,
                    deadline_utc, updated_at_utc,
                    execution_authorized, remote_writes_performed,
                    ready_for_execution)
                  VALUES (
                    {id}, {f.RepositoryId}, {f.Organization}, {f.Repository},
                    {f.PreparationId}, {f.PlanId}, {f.ScopeId}, {f.AttemptId},
                    {f.RequestId}, {f.Generation},
                    {p.PacketSha256}, {p.CandidateSetSha256},
                    {p.OriginalPolicySha256}, {p.ObservedBaselineSha256},
                    {p.EvidenceSha256}, {digest},
                    'Proposed', {input.ActorId}, NULL,
                    {deadline}, {now}, false, false, false
                  )
                  RETURNING id
                )
                INSERT INTO github_remote_test_grant_events
                    (grant_id,type,actor_id,occurred_at_utc)
                SELECT id,'Proposed',{input.ActorId},{now} FROM grant_row
                """, ct);
            if (changed != 1)
                throw new GitHubRemoteTestGrantException("RemoteTestGrantAdmissionLost");
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GitHubRemoteTestGrantException("RemoteTestGrantBusy");
        }
        var row = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleAsync(x => x.Id == id, ct);
        return new(Map(row), token);
    }

    public async Task<GitHubRemoteTestGrantStatus> ReviewAsync(
        Guid grantId, GitHubRemoteTestGrantEvidence input,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        GitHubRemoteTestGrantIntegrity.Validate(input, options, now);
        var p = input.Preflight;
        var f = input.Fence;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(f.RepositoryId, ct);
        if (await QuarantineLockedAsync(f.RepositoryId, now, ct) > 0)
        {
            await tx.CommitAsync(ct);
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantRecoveryRequired");
        }
        await VerifyDependenciesAsync(input, now, ct);
        var row = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grantId &&
                x.RepositoryId == f.RepositoryId, ct)
            ?? throw new GitHubRemoteTestGrantException("RemoteTestGrantNotFound");
        if (row.State != GitHubRemoteTestGrantStates.Proposed ||
            row.DeadlineUtc <= now ||
            row.RequestedBy == input.ActorId ||
            row.FenceRequestId != f.RequestId ||
            row.FenceGeneration != f.Generation ||
            row.AttemptId != f.AttemptId ||
            row.PacketSha256 != p.PacketSha256 ||
            row.CandidateSetSha256 != p.CandidateSetSha256 ||
            row.OriginalPolicySha256 != p.OriginalPolicySha256 ||
            row.BaselineSha256 != p.ObservedBaselineSha256)
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantReviewStaleOrSameActor");

        // Reviewer does not extend F-050's deadline. The row remains
        // physically non-executable even after human acknowledgement.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH acknowledged AS (
              UPDATE github_remote_test_grants
              SET state = 'ReviewedWriteDisabled',
                  reviewed_by = {input.ActorId},
                  preflight_sha256 = {p.EvidenceSha256},
                  updated_at_utc = {now}
              WHERE id = {grantId} AND repository_id = {f.RepositoryId}
                AND fence_request_id = {f.RequestId}
                AND fence_generation = {f.Generation}
                AND state = 'Proposed' AND deadline_utc > {now}
                AND requested_by <> {input.ActorId}
                AND packet_sha256 = {p.PacketSha256}
                AND candidate_set_sha256 = {p.CandidateSetSha256}
                AND baseline_sha256 = {p.ObservedBaselineSha256}
                AND execution_authorized = false
                AND remote_writes_performed = false
                AND ready_for_execution = false
              RETURNING id
            )
            INSERT INTO github_remote_test_grant_events
                (grant_id,type,actor_id,occurred_at_utc)
            SELECT id,'ReviewedWriteDisabled',{input.ActorId},{now}
            FROM acknowledged
            """, ct);
        if (changed != 1)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantReviewLost");
        await tx.CommitAsync(ct);
        return Map(await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleAsync(x => x.Id == grantId, ct));
    }

    public async Task<bool> CancelAsync(Guid grantId, long repositoryId,
        Guid actorId, Guid token, CancellationToken ct)
    {
        AssertRepo(repositoryId);
        if (grantId == Guid.Empty || actorId == Guid.Empty || token == Guid.Empty)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantHandleInvalid");
        var now = clock.GetUtcNow();
        var digest = GitHubRemoteTestGrantIntegrity.TokenDigest(token);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repositoryId, ct);
        var expired = await QuarantineLockedAsync(repositoryId, now, ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH cancelled AS (
              UPDATE github_remote_test_grants
              SET state='Cancelled', updated_at_utc={now}
              WHERE id={grantId} AND repository_id={repositoryId}
                AND requested_by={actorId}
                AND cancellation_token_sha256={digest}
                AND state IN ('Proposed','ReviewedWriteDisabled')
                AND deadline_utc > {now}
                AND execution_authorized=false
                AND remote_writes_performed=false
                AND ready_for_execution=false
              RETURNING id
            )
            INSERT INTO github_remote_test_grant_events
                (grant_id,type,actor_id,occurred_at_utc)
            SELECT id,'Cancelled',{actorId},{now} FROM cancelled
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1 && expired == 0;
    }

    public async Task<bool> RecoverAsync(Guid grantId, long repositoryId,
        Guid actorId, string acknowledgement, CancellationToken ct)
    {
        AssertRepo(repositoryId);
        if (grantId == Guid.Empty || actorId == Guid.Empty ||
            acknowledgement != GitHubRemoteTestGrantIntegrity.RecoveryText)
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantRecoveryAttestationRequired");
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repositoryId, ct);
        await QuarantineLockedAsync(repositoryId, now, ct);
        var row = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grantId &&
                x.RepositoryId == repositoryId, ct)
            ?? throw new GitHubRemoteTestGrantException("RemoteTestGrantNotFound");
        if (row.State != GitHubRemoteTestGrantStates.RecoveryRequired ||
            row.RequestedBy == actorId || row.ReviewedBy == actorId)
        {
            // Quarantine must survive invalid actor or stale handle even
            // when the recovery request was first to discover expiry.
            await tx.CommitAsync(ct);
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantRecoveryMustBeIndependent");
        }
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH recovered AS (
              UPDATE github_remote_test_grants
              SET state='RecoveredNoWrites',updated_at_utc={now}
              WHERE id={grantId} AND repository_id={repositoryId}
                AND state='RecoveryRequired'
                AND requested_by<>{actorId}
                AND (reviewed_by IS NULL OR reviewed_by<>{actorId})
                AND execution_authorized=false
                AND remote_writes_performed=false
                AND ready_for_execution=false
              RETURNING id
            )
            INSERT INTO github_remote_test_grant_events
                (grant_id,type,actor_id,occurred_at_utc)
            SELECT id,'RecoveredNoWrites',{actorId},{now} FROM recovered
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<GitHubRemoteTestGrantStatus?> GetAsync(
        Guid grantId, long repositoryId, CancellationToken ct)
    {
        AssertRepo(repositoryId);
        var item = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grantId &&
                x.RepositoryId == repositoryId, ct);
        return item is null ? null : Map(item);
    }

    public async Task<IReadOnlyList<GitHubRemoteTestGrantEvent>> EventsAsync(
        Guid grantId, long repositoryId, CancellationToken ct)
    {
        AssertRepo(repositoryId);
        if (!await db.GitHubRemoteTestGrants.AsNoTracking()
            .AnyAsync(x => x.Id == grantId && x.RepositoryId == repositoryId, ct))
            throw new KeyNotFoundException("RemoteTestGrantNotFound");
        return (await db.GitHubRemoteTestGrantEvents.AsNoTracking()
            .Where(x => x.GrantId == grantId).OrderBy(x => x.Id)
            .Take(100).ToArrayAsync(ct))
            .Select(x => new GitHubRemoteTestGrantEvent(x.Id, x.GrantId,
                x.Type, x.ActorId, x.OccurredAtUtc)).ToArray();
    }

    public async Task<bool> QuarantineNextExpiredAsync(CancellationToken ct)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0)
            return false;
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(options.AllowedRepositoryId, ct);
        var changed = await QuarantineLockedAsync(
            options.AllowedRepositoryId, now, ct);
        await tx.CommitAsync(ct);
        return changed > 0;
    }

    private async Task VerifyDependenciesAsync(
        GitHubRemoteTestGrantEvidence input,
        DateTimeOffset now, CancellationToken ct)
    {
        var f = input.Fence;
        var storedFence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == f.RepositoryId, ct);
        var storedAttempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == f.AttemptId, ct);
        if (storedFence is null ||
            storedFence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            !string.Equals(storedFence.Organization, f.Organization,
                StringComparison.Ordinal) ||
            !string.Equals(storedFence.Repository, f.Repository,
                StringComparison.Ordinal) ||
            storedFence.RequestedBy != f.RequestedBy ||
            storedFence.ReviewedBy != f.ReviewedBy ||
            storedFence.RequestId != f.RequestId ||
            storedFence.Generation != f.Generation ||
            storedFence.DeadlineUtc <= now ||
            storedFence.PacketSha256 != f.PacketSha256 ||
            storedFence.CandidateSetSha256 != f.CandidateSetSha256 ||
            storedFence.PolicySha256 != f.PolicySha256 ||
            storedFence.ExecutionAuthorized || storedFence.RemoteWritesPerformed ||
            storedFence.ReadyForExecution ||
            storedAttempt is null ||
            storedAttempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            storedAttempt.RequestedBy != input.Attempt.RequestedBy ||
            storedAttempt.ReviewedBy != input.Attempt.ReviewedBy ||
            !string.Equals(storedAttempt.Organization, f.Organization,
                StringComparison.Ordinal) ||
            !string.Equals(storedAttempt.Repository, f.Repository,
                StringComparison.Ordinal) ||
            storedAttempt.Id != f.AttemptId ||
            storedAttempt.RepositoryId != f.RepositoryId ||
            storedAttempt.PreparationId != f.PreparationId ||
            storedAttempt.PlanId != f.PlanId ||
            storedAttempt.ScopeId != f.ScopeId ||
            storedAttempt.PacketSha256 != input.Preflight.PacketSha256 ||
            storedAttempt.CandidateSetSha256 != input.Preflight.CandidateSetSha256 ||
            storedAttempt.PolicySha256 != input.Preflight.OriginalPolicySha256 ||
            storedAttempt.ReviewedBy is null ||
            storedAttempt.ReviewedBy == storedAttempt.RequestedBy ||
            storedAttempt.TransportAttempted || storedAttempt.ExecutionAuthorized ||
            storedAttempt.RemoteWritesPerformed || storedAttempt.ReadyForExecution)
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantStoredBarrierChanged");
    }

    private Task<int> QuarantineLockedAsync(long repositoryId,
        DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
              UPDATE github_remote_test_grants
              SET state='RecoveryRequired', updated_at_utc={now}
              WHERE repository_id={repositoryId}
                AND state IN ('Proposed','ReviewedWriteDisabled')
                AND deadline_utc <= {now}
                AND execution_authorized=false
                AND remote_writes_performed=false
                AND ready_for_execution=false
              RETURNING id
            )
            INSERT INTO github_remote_test_grant_events
                (grant_id,type,actor_id,occurred_at_utc)
            SELECT id,'RecoveryRequired',{Guid.Empty},{now} FROM expired
            """, ct);

    private Task<int> LockAsync(long repositoryId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repositoryId.ToString()}))", ct);

    private void AssertRepo(long repositoryId)
    {
        if (!options.Enabled || repositoryId <= 0 ||
            repositoryId != options.AllowedRepositoryId)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantWrongRepository");
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) =>
        a <= b ? a : b;

    private static GitHubRemoteTestGrantStatus Map(
        GitHubRemoteTestGrantRecord x) =>
        new(x.Id, x.RepositoryId, x.Organization, x.Repository,
            x.PreparationId, x.PlanId, x.ScopeId, x.AttemptId,
            x.FenceRequestId, x.FenceGeneration, x.PacketSha256,
            x.CandidateSetSha256, x.OriginalPolicySha256,
            x.BaselineSha256, x.PreflightSha256, x.State, x.RequestedBy,
            x.ReviewedBy, x.DeadlineUtc, x.UpdatedAtUtc,
            x.ExecutionAuthorized, x.RemoteWritesPerformed,
            x.ReadyForExecution);
}
