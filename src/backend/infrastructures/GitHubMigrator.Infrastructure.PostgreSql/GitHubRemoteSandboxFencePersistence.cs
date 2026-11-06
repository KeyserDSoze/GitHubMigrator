using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class GitHubRemoteSandboxFenceRecord
{
    public long RepositoryId { get; set; }
    public Guid RequestId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ScopeId { get; set; }
    public Guid AttemptId { get; set; }
    public string Organization { get; set; } = "";
    public string Repository { get; set; } = "";
    public string PacketSha256 { get; set; } = "";
    public string CandidateSetSha256 { get; set; } = "";
    public string PolicySha256 { get; set; } = "";
    public string TokenSha256 { get; set; } = "";
    public long Generation { get; set; }
    public string State { get; set; } = "";
    public Guid RequestedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitHubRemoteSandboxFenceEventRecord
{
    public long Id { get; set; }
    public long RepositoryId { get; set; }
    public Guid RequestId { get; set; }
    public long Generation { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

// This fence is keyed by GitHub's GLOBAL numeric repository identity.
// Advisory locking synchronizes the absent-row insert across API replicas;
// repository row and generation CAS additionally fence late operations.
public sealed class PostgresGitHubRemoteSandboxFenceStore(
    AppDbContext db, TimeProvider clock,
    GitHubRemoteSandboxFenceOptions options) : IGitHubRemoteSandboxFenceStore
{
    public async Task<GitHubRemoteSandboxFenceClaim> ProposeAsync(
        GitHubRemoteSandboxFenceInput input, CancellationToken ct)
    {
        GitHubRemoteSandboxFenceIntegrity.Verify(input, options);
        var packet = input.Packet;
        var now = clock.GetUtcNow();
        var token = Guid.NewGuid();
        var digest = GitHubRemoteSandboxFenceIntegrity.TokenDigest(token);
        var requestId = Guid.NewGuid();
        var deadline = now.Add(GitHubRemoteSandboxFenceIntegrity.ReviewWindow);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(packet.RepositoryId, ct);
        if (await QuarantineLockedAsync(packet.RepositoryId, now, ct) != 0)
        {
            // The quarantine must COMMIT even when a new request is
            // rejected; throwing inside an uncommitted transaction would
            // silently roll back this critical safety transition.
            await tx.CommitAsync(ct);
            throw new GitHubRemoteSandboxFenceException(
                "RemoteFenceAlreadyHeldOrQuarantined");
        }
        var old = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == packet.RepositoryId, ct);

        long generation;
        if (old is null)
        {
            generation = 1;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO github_remote_sandbox_fences (
                    repository_id, request_id, preparation_id, plan_id,
                    scope_id, attempt_id, organization, repository,
                    packet_sha256, candidate_set_sha256, policy_sha256,
                    token_sha256, generation, state, requested_by,
                    reviewed_by, deadline_utc, updated_at_utc,
                    execution_authorized, remote_writes_performed, ready_for_execution)
                VALUES ({packet.RepositoryId}, {requestId}, {packet.PreparationId},
                    {packet.PlanId}, {packet.ScopeId}, {input.Attempt.Id},
                    {packet.Organization}, {packet.Repository},
                    {packet.EvidenceSha256}, {packet.CandidateSetSha256},
                    {input.Attempt.PolicySha256}, {digest}, {generation},
                    'Proposed', {input.ActorId}, NULL,
                    {deadline}, {now}, false, false, false)
                """, ct);
        }
        else
        {
            if (old.State is not (
                GitHubRemoteSandboxFenceStates.Released or
                GitHubRemoteSandboxFenceStates.RecoveredNoWrites))
                throw new GitHubRemoteSandboxFenceException(
                    "RemoteFenceAlreadyHeldOrQuarantined");
            if (old.Generation == long.MaxValue)
                throw new GitHubRemoteSandboxFenceException("RemoteFenceGenerationExhausted");
            generation = checked(old.Generation + 1);
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE github_remote_sandbox_fences
                SET request_id = {requestId},
                    preparation_id = {packet.PreparationId},
                    plan_id = {packet.PlanId}, scope_id = {packet.ScopeId},
                    attempt_id = {input.Attempt.Id},
                    organization = {packet.Organization},
                    repository = {packet.Repository},
                    packet_sha256 = {packet.EvidenceSha256},
                    candidate_set_sha256 = {packet.CandidateSetSha256},
                    policy_sha256 = {input.Attempt.PolicySha256},
                    token_sha256 = {digest}, generation = {generation},
                    state = 'Proposed', requested_by = {input.ActorId},
                    reviewed_by = NULL, deadline_utc = {deadline},
                    updated_at_utc = {now}
                WHERE repository_id = {packet.RepositoryId}
                  AND generation = {old.Generation}
                  AND state IN ('Released','RecoveredNoWrites')
                  AND execution_authorized = false
                  AND remote_writes_performed = false
                  AND ready_for_execution = false
                """, ct);
            if (changed != 1)
                throw new GitHubRemoteSandboxFenceException("RemoteFenceChanged");
        }
        await EventAsync(packet.RepositoryId, requestId, generation,
            "Proposed", input.ActorId, now, ct);
        await tx.CommitAsync(ct);
        return new(requestId, packet.RepositoryId, generation, token,
            GitHubRemoteSandboxFenceStates.Proposed, deadline,
            false, false, false);
    }

    public async Task<GitHubRemoteSandboxFenceStatus> ReviewAsync(
        GitHubRemoteSandboxFenceInput input, Guid requestId,
        long generation, CancellationToken ct)
    {
        GitHubRemoteSandboxFenceIntegrity.Verify(input, options);
        if (requestId == Guid.Empty || generation <= 0)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceHandleInvalid");
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(input.Packet.RepositoryId, ct);
        if (await QuarantineLockedAsync(input.Packet.RepositoryId, now, ct) != 0)
        {
            await tx.CommitAsync(ct);
            throw new GitHubRemoteSandboxFenceException(
                "RemoteFenceReviewStaleOrSameActor");
        }
        var row = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == input.Packet.RepositoryId, ct)
            ?? throw new GitHubRemoteSandboxFenceException("RemoteFenceMissing");
        if (row.State != GitHubRemoteSandboxFenceStates.Proposed ||
            row.RequestId != requestId || row.Generation != generation ||
            row.DeadlineUtc <= now ||
            row.RequestedBy == input.ActorId ||
            row.PacketSha256 != input.Packet.EvidenceSha256 ||
            row.CandidateSetSha256 != input.Packet.CandidateSetSha256 ||
            row.PolicySha256 != input.Attempt.PolicySha256 ||
            row.AttemptId != input.Attempt.Id ||
            row.ScopeId != input.Packet.ScopeId ||
            row.PreparationId != input.Packet.PreparationId ||
            row.PlanId != input.Packet.PlanId ||
            !string.Equals(row.Organization, input.Packet.Organization,
                StringComparison.Ordinal) ||
            !string.Equals(row.Repository, input.Packet.Repository,
                StringComparison.Ordinal))
            throw new GitHubRemoteSandboxFenceException(
                "RemoteFenceReviewStaleOrSameActor");

        var nextDeadline = now.Add(
            GitHubRemoteSandboxFenceIntegrity.ReviewedWindow);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_remote_sandbox_fences
            SET state = 'ReviewedWriteDisabled',
                reviewed_by = {input.ActorId},
                deadline_utc = {nextDeadline}, updated_at_utc = {now}
            WHERE repository_id = {row.RepositoryId}
              AND request_id = {requestId}
              AND generation = {generation}
              AND state = 'Proposed' AND deadline_utc > {now}
              AND requested_by <> {input.ActorId}
              AND packet_sha256 = {input.Packet.EvidenceSha256}
              AND execution_authorized = false
              AND remote_writes_performed = false
              AND ready_for_execution = false
            """, ct);
        if (changed != 1)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceReviewChanged");
        await EventAsync(row.RepositoryId, requestId, generation,
            "ReviewedWriteDisabled", input.ActorId, now, ct);
        await tx.CommitAsync(ct);
        return Map(await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleAsync(x => x.RepositoryId == row.RepositoryId, ct));
    }

    public async Task<bool> ReleaseAsync(
        long repositoryId, Guid requestId, Guid token,
        long generation, Guid actorId, CancellationToken ct)
    {
        if (!EnabledFor(repositoryId) ||
            requestId == Guid.Empty || token == Guid.Empty ||
            generation <= 0 || actorId == Guid.Empty)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceHandleInvalid");
        var now = clock.GetUtcNow();
        var digest = GitHubRemoteSandboxFenceIntegrity.TokenDigest(token);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repositoryId, ct);
        await QuarantineLockedAsync(repositoryId, now, ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH released AS (
                UPDATE github_remote_sandbox_fences
                SET state = 'Released', updated_at_utc = {now}
                WHERE repository_id = {repositoryId}
                  AND request_id = {requestId}
                  AND generation = {generation}
                  AND token_sha256 = {digest}
                  AND requested_by = {actorId}
                  AND deadline_utc > {now}
                  AND state IN ('Proposed','ReviewedWriteDisabled')
                  AND execution_authorized = false
                  AND remote_writes_performed = false
                  AND ready_for_execution = false
                RETURNING repository_id, request_id, generation
            )
            INSERT INTO github_remote_sandbox_fence_events (
                repository_id, request_id, generation, type,
                actor_id, occurred_at_utc)
            SELECT repository_id, request_id, generation,
                'Released', {actorId}, {now} FROM released
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<bool> QuarantineNextExpiredAsync(CancellationToken ct)
    {
        if (!options.Enabled) return false;
        var now = clock.GetUtcNow();
        var repoId = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .Where(x => x.RepositoryId == options.AllowedRepositoryId &&
                (x.State == GitHubRemoteSandboxFenceStates.Proposed ||
                 x.State == GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled) &&
                x.DeadlineUtc <= now)
            .OrderBy(x => x.DeadlineUtc)
            .Select(x => (long?)x.RepositoryId)
            .FirstOrDefaultAsync(ct);
        if (repoId is null || !EnabledFor(repoId.Value)) return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repoId.Value, ct);
        var changed = await QuarantineLockedAsync(repoId.Value, now, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<bool> RecoverAsync(
        GitHubRemoteSandboxFenceInput freshInput,
        Guid expectedOldRequestId, long expectedGeneration,
        string acknowledgementText, CancellationToken ct)
    {
        GitHubRemoteSandboxFenceIntegrity.Verify(freshInput, options);
        if (acknowledgementText != GitHubRemoteSandboxFenceIntegrity.RecoveryText ||
            expectedOldRequestId == Guid.Empty || expectedGeneration <= 0)
            throw new GitHubRemoteSandboxFenceException(
                "RemoteFenceRecoveryAttestationRequired");
        var now = clock.GetUtcNow();
        var repoId = freshInput.Packet.RepositoryId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repoId, ct);
        // Recover is permitted when this same call discovers the expired
        // row; it must preserve the quarantine even if the actor is invalid.
        await QuarantineLockedAsync(repoId, now, ct);
        var row = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repoId, ct)
            ?? throw new GitHubRemoteSandboxFenceException("RemoteFenceMissing");
        if (row.State != GitHubRemoteSandboxFenceStates.RecoveryRequired ||
            row.Generation != expectedGeneration ||
            row.RequestId != expectedOldRequestId ||
            freshInput.ActorId == row.RequestedBy ||
            freshInput.ActorId == row.ReviewedBy)
        {
            // If this recovery call was also the first to discover an
            // expired fence, persist its quarantine even when the recovery
            // actor is ineligible. An exception must not roll it back.
            await tx.CommitAsync(ct);
            throw new GitHubRemoteSandboxFenceException(
                "RemoteFenceRecoveryMustBeIndependent");
        }

        // F-050 has *no* transport. This explicit operator attestation can
        // clear only an F-050 no-write fence, never a future send attempt.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH recovered AS (
                UPDATE github_remote_sandbox_fences
                SET state = 'RecoveredNoWrites', updated_at_utc = {now}
                WHERE repository_id = {repoId}
                  AND request_id = {expectedOldRequestId}
                  AND generation = {expectedGeneration}
                  AND state = 'RecoveryRequired'
                  AND requested_by <> {freshInput.ActorId}
                  AND (reviewed_by IS NULL OR reviewed_by <> {freshInput.ActorId})
                  AND execution_authorized = false
                  AND remote_writes_performed = false
                  AND ready_for_execution = false
                RETURNING repository_id, request_id, generation
            )
            INSERT INTO github_remote_sandbox_fence_events (
                repository_id, request_id, generation,
                type, actor_id, occurred_at_utc)
            SELECT repository_id, request_id, generation,
                'RecoveredNoWrites', {freshInput.ActorId}, {now} FROM recovered
            """, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<GitHubRemoteSandboxFenceStatus?> GetAsync(
        long repositoryId, CancellationToken ct)
    {
        if (!EnabledFor(repositoryId))
            throw new GitHubRemoteSandboxFenceException("RemoteFenceWrongRepository");
        var row = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repositoryId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitHubRemoteSandboxFenceEvent>> EventsAsync(
        long repositoryId, CancellationToken ct)
    {
        if (!EnabledFor(repositoryId))
            throw new GitHubRemoteSandboxFenceException("RemoteFenceWrongRepository");
        return (await db.GitHubRemoteSandboxFenceEvents.AsNoTracking()
            .Where(x => x.RepositoryId == repositoryId)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct))
            .OrderBy(x => x.Id)
            .Select(x => new GitHubRemoteSandboxFenceEvent(
                x.Id, x.RepositoryId, x.RequestId,
                x.Generation, x.Type, x.ActorId, x.OccurredAtUtc))
            .ToArray();
    }

    private bool EnabledFor(long repoId) =>
        options.Enabled && repoId > 0 &&
        options.AllowedRepositoryId == repoId;

    private Task<int> LockAsync(long repoId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(733050, hashtext({repoId.ToString()}))", ct);

    private Task<int> EventAsync(long repoId, Guid requestId,
        long generation, string type, Guid actor,
        DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_remote_sandbox_fence_events (
                repository_id, request_id, generation, type,
                actor_id, occurred_at_utc)
            VALUES ({repoId}, {requestId}, {generation}, {type},
                {actor}, {now})
            """, ct);

    private Task<int> QuarantineLockedAsync(long repoId,
        DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH quarantined AS (
                UPDATE github_remote_sandbox_fences
                SET state = 'RecoveryRequired', updated_at_utc = {now}
                WHERE repository_id = {repoId}
                  AND state IN ('Proposed','ReviewedWriteDisabled')
                  AND deadline_utc <= {now}
                  AND execution_authorized = false
                  AND remote_writes_performed = false
                  AND ready_for_execution = false
                RETURNING repository_id, request_id, generation
            )
            INSERT INTO github_remote_sandbox_fence_events (
                repository_id, request_id, generation, type,
                actor_id, occurred_at_utc)
            SELECT repository_id, request_id, generation,
                'RecoveryRequired', {Guid.Empty}, {now} FROM quarantined
            """, ct);

    private static GitHubRemoteSandboxFenceStatus Map(
        GitHubRemoteSandboxFenceRecord x) =>
        new(x.RequestId, x.RepositoryId, x.Organization, x.Repository,
            x.PreparationId, x.PlanId, x.ScopeId, x.AttemptId,
            x.PacketSha256, x.CandidateSetSha256, x.PolicySha256,
            x.Generation, x.State, x.RequestedBy, x.ReviewedBy,
            x.DeadlineUtc, x.UpdatedAtUtc, x.ExecutionAuthorized,
            x.RemoteWritesPerformed, x.ReadyForExecution);
}
