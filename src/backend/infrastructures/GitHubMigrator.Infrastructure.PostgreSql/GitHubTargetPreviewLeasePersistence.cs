using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// One row per GLOBAL GitHub numeric repository identity. Org/repo slugs
// are advisory human labels, never the lock key. No GitHub credential or
// executable command is stored.
public sealed class GitHubTargetPreviewLeaseRecord
{
    public long RepositoryId { get; set; }
    public string Organization { get; set; } = "";
    public string Repository { get; set; } = "";
    public Guid LeaseToken { get; set; }
    public long Generation { get; set; }
    public Guid ActorId { get; set; }
    public string PacketSha256 { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset AcquiredAtUtc { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool ExecutionAuthorized { get; set; }
    public bool RemoteWritesPerformed { get; set; }
    public bool ReadyForExecution { get; set; }
}

public sealed class GitHubTargetPreviewLeaseEventRecord
{
    public long Id { get; set; }
    public long RepositoryId { get; set; }
    public long Generation { get; set; }
    public string Type { get; set; } = "";
    public Guid ActorId { get; set; }
    public string PacketSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class PostgresGitHubTargetPreviewLeaseStore(
    AppDbContext db, TimeProvider clock) : IGitHubTargetPreviewLeaseStore
{
    public async Task<GitHubTargetPreviewLeaseClaim> AcquireAsync(
        GitHubCreateOnlyPacket packet, Guid actorId,
        TimeSpan duration, CancellationToken ct)
    {
        GitHubTargetPreviewLeaseIntegrity.VerifyPacket(packet);
        GitHubTargetPreviewLeaseIntegrity.VerifyDuration(duration);
        RequireActor(actorId);
        var now = clock.GetUtcNow();
        var until = now.Add(duration);
        var token = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(packet.RepositoryId, ct);
        var row = await db.GitHubTargetPreviewLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == packet.RepositoryId, ct);
        if (row is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO github_target_preview_leases (
                    repository_id, organization, repository, lease_token,
                    generation, actor_id, packet_sha256, state,
                    acquired_at_utc, lease_until_utc, updated_at_utc,
                    execution_authorized, remote_writes_performed, ready_for_execution)
                VALUES ({packet.RepositoryId}, {packet.Organization},
                    {packet.Repository}, {token}, 1, {actorId},
                    {packet.EvidenceSha256}, 'Held', {now}, {until}, {now},
                    false, false, false)
                """, ct);
            await EventAsync(packet.RepositoryId, 1, "Acquired",
                actorId, packet.EvidenceSha256, now, ct);
            await tx.CommitAsync(ct);
            return Claim(packet.RepositoryId, token, 1, actorId,
                packet.EvidenceSha256, until);
        }

        // A timed-out operation is NOT silently reclaimed, even though the
        // current code cannot write GitHub. It enters a durable quarantine
        // that requires an independent human and fresh F-044 proofs.
        if (row.State == GitHubTargetPreviewLeaseIntegrity.Held &&
            row.LeaseUntilUtc <= now)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE github_target_preview_leases
                SET state = 'RecoveryRequired', lease_until_utc = NULL,
                    updated_at_utc = {now}
                WHERE repository_id = {packet.RepositoryId} AND state = 'Held'
                    AND generation = {row.Generation} AND lease_token = {row.LeaseToken}
                """, ct);
            await EventAsync(packet.RepositoryId, row.Generation, "Quarantined",
                actorId, row.PacketSha256, now, ct);
            await tx.CommitAsync(ct);
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseRecoveryRequired");
        }
        if (row.State == GitHubTargetPreviewLeaseIntegrity.Held)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseBusy");
        if (row.State == GitHubTargetPreviewLeaseIntegrity.RecoveryRequired)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseRecoveryRequired");
        if (row.State != GitHubTargetPreviewLeaseIntegrity.Released ||
            row.Generation == long.MaxValue)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseStateInvalid");
        // Do not silently adopt a different GitHub numeric repository under
        // a renamed organization or a stale source mapping.
        if (!string.Equals(row.Organization, packet.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(row.Repository, packet.Repository,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseTargetChanged");

        var generation = checked(row.Generation + 1);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_target_preview_leases
            SET lease_token = {token}, generation = {generation},
                actor_id = {actorId}, packet_sha256 = {packet.EvidenceSha256},
                state = 'Held', acquired_at_utc = {now},
                lease_until_utc = {until}, updated_at_utc = {now}
            WHERE repository_id = {packet.RepositoryId} AND state = 'Released'
                AND generation = {row.Generation} AND lease_token = {row.LeaseToken}
                AND execution_authorized = false AND remote_writes_performed = false
            """, ct);
        if (updated != 1)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseLost");
        await EventAsync(packet.RepositoryId, generation, "Acquired",
            actorId, packet.EvidenceSha256, now, ct);
        await tx.CommitAsync(ct);
        return Claim(packet.RepositoryId, token, generation, actorId,
            packet.EvidenceSha256, until);
    }

    public async Task<bool> RenewAsync(
        GitHubCreateOnlyPacket packet, Guid actorId,
        Guid leaseToken, long generation, TimeSpan duration, CancellationToken ct)
    {
        GitHubTargetPreviewLeaseIntegrity.VerifyPacket(packet);
        GitHubTargetPreviewLeaseIntegrity.VerifyDuration(duration);
        RequireActor(actorId);
        if (leaseToken == Guid.Empty || generation <= 0)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseHandleInvalid");
        var now = clock.GetUtcNow();
        var until = now.Add(duration);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(packet.RepositoryId, ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_target_preview_leases
            SET lease_until_utc = {until}, updated_at_utc = {now}
            WHERE repository_id = {packet.RepositoryId} AND state = 'Held'
                AND lease_token = {leaseToken} AND generation = {generation}
                AND actor_id = {actorId} AND packet_sha256 = {packet.EvidenceSha256}
                AND lease_until_utc > {now} AND execution_authorized = false
                AND remote_writes_performed = false
            """, ct);
        if (changed == 1)
            await EventAsync(packet.RepositoryId, generation, "Renewed",
                actorId, packet.EvidenceSha256, now, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<bool> ReleaseAsync(
        long repositoryId, Guid actorId, Guid leaseToken, long generation,
        string packetSha256, CancellationToken ct)
    {
        ValidateHandle(repositoryId, actorId, leaseToken, generation, packetSha256);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(repositoryId, ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_target_preview_leases
            SET state = 'Released', lease_until_utc = NULL, updated_at_utc = {now}
            WHERE repository_id = {repositoryId} AND state = 'Held'
                AND generation = {generation} AND lease_token = {leaseToken}
                AND actor_id = {actorId} AND packet_sha256 = {packetSha256}
                AND lease_until_utc > {now} AND execution_authorized = false
                AND remote_writes_performed = false
            """, ct);
        if (changed == 1)
            await EventAsync(repositoryId, generation, "Released",
                actorId, packetSha256, now, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<bool> RecoverAsync(
        GitHubCreateOnlyPacket freshPacket, Guid actorId,
        long expectedGeneration, string oldPacketSha256, CancellationToken ct)
    {
        GitHubTargetPreviewLeaseIntegrity.VerifyPacket(freshPacket);
        RequireActor(actorId);
        if (expectedGeneration <= 0 ||
            !GitSourceBundleStorageCheck.ValidSha(oldPacketSha256) ||
            oldPacketSha256 == freshPacket.EvidenceSha256)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseRecoveryProofInvalid");
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(freshPacket.RepositoryId, ct);
        var row = await db.GitHubTargetPreviewLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == freshPacket.RepositoryId, ct);
        if (row is null || row.Generation != expectedGeneration ||
            row.PacketSha256 != oldPacketSha256 || row.ActorId == actorId ||
            !string.Equals(row.Organization, freshPacket.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(row.Repository, freshPacket.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !(row.State == GitHubTargetPreviewLeaseIntegrity.RecoveryRequired ||
              row.State == GitHubTargetPreviewLeaseIntegrity.Held &&
              row.LeaseUntilUtc <= now))
            return false;
        // Invalidate old lease tokens permanently. Never let an expired
        // worker make a successful checkpoint, even after recovery.
        var nextGeneration = checked(expectedGeneration + 1);
        var token = Guid.NewGuid();
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE github_target_preview_leases
            SET state = 'Released', lease_token = {token},
                generation = {nextGeneration}, actor_id = {actorId},
                packet_sha256 = {freshPacket.EvidenceSha256},
                lease_until_utc = NULL, updated_at_utc = {now}
            WHERE repository_id = {freshPacket.RepositoryId}
                AND generation = {expectedGeneration}
                AND lease_token = {row.LeaseToken}
                AND packet_sha256 = {oldPacketSha256}
                AND actor_id = {row.ActorId}
                AND (state = 'RecoveryRequired'
                     OR (state = 'Held' AND lease_until_utc <= {now}))
                AND execution_authorized = false AND remote_writes_performed = false
            """, ct);
        if (changed == 1)
            await EventAsync(freshPacket.RepositoryId, nextGeneration, "Recovered",
                actorId, freshPacket.EvidenceSha256, now, ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<GitHubTargetPreviewLeaseStatus?> GetAsync(
        long repositoryId, CancellationToken ct)
    {
        var row = await db.GitHubTargetPreviewLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == repositoryId, ct);
        if (row is null) return null;
        var state = row.State == GitHubTargetPreviewLeaseIntegrity.Held &&
            row.LeaseUntilUtc <= clock.GetUtcNow()
            ? GitHubTargetPreviewLeaseIntegrity.RecoveryRequired : row.State;
        return new(row.RepositoryId, row.Organization, row.Repository,
            state, row.Generation, row.PacketSha256, row.LeaseUntilUtc,
            row.UpdatedAtUtc, false, false, false);
    }

    public async Task<IReadOnlyList<GitHubTargetPreviewLeaseEvent>> EventsAsync(
        long repositoryId, CancellationToken ct)
    {
        return (await db.GitHubTargetPreviewLeaseEvents.AsNoTracking()
            .Where(x => x.RepositoryId == repositoryId)
            .OrderByDescending(x => x.Id).Take(100).ToArrayAsync(ct))
            .OrderBy(x => x.Id)
            .Select(x => new GitHubTargetPreviewLeaseEvent(x.Id,
                x.RepositoryId, x.Generation, x.Type, x.ActorId,
                x.PacketSha256, x.OccurredAtUtc)).ToArray();
    }

    private async Task LockAsync(long repositoryId, CancellationToken ct)
    {
        // Transaction lock serializes the first INSERT too (the row may
        // not yet exist) and is shared across ALL ASP.NET replicas.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(732911, hashtext({repositoryId.ToString()}))", ct);
    }

    private Task<int> EventAsync(
        long repositoryId, long generation, string type,
        Guid actorId, string packetSha, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO github_target_preview_lease_events
                (repository_id, generation, type, actor_id,
                 packet_sha256, occurred_at_utc)
            VALUES ({repositoryId}, {generation}, {type},
                    {actorId}, {packetSha}, {now})
            """, ct);

    private static GitHubTargetPreviewLeaseClaim Claim(
        long id, Guid token, long generation, Guid actor,
        string sha, DateTimeOffset expires) =>
        new(id, token, generation, actor, sha, expires,
            GitHubTargetPreviewLeaseIntegrity.Held, false, false, false);

    private static void RequireActor(Guid actor)
    {
        if (actor == Guid.Empty)
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseActorInvalid");
    }

    private static void ValidateHandle(
        long repositoryId, Guid actorId, Guid leaseToken,
        long generation, string packetSha256)
    {
        RequireActor(actorId);
        if (repositoryId <= 0 || leaseToken == Guid.Empty || generation <= 0 ||
            !GitSourceBundleStorageCheck.ValidSha(packetSha256))
            throw new GitHubTargetPreviewLeaseException("PreviewLeaseHandleInvalid");
    }
}
