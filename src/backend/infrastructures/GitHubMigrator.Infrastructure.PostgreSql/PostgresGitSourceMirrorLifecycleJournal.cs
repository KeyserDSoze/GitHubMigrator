using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-061: PostgreSQL fencing of the local F-060 physical mirror.
// A lease is NOT a GitHub transport permit. Only the current generation
// may record a verified SHA, and any expired generation must re-fsck.
public sealed class PostgresGitSourceMirrorLifecycleJournal(
    AppDbContext db, TimeProvider clock,
    GitSourceMirrorLifecycleOptions options)
    : IGitSourceMirrorLifecycleJournal
{
    public async Task<GitSourceMirrorLease> AcquireAsync(
        GitHubTrustedRemoteReadPlan plan, Guid workerId, CancellationToken ct)
    {
        RequireEnabled();
        GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
        if (workerId == Guid.Empty)
            throw Reject("SourceMirrorLifecycleWorkerInvalid");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(plan.SourceMirrorId, ct);
        var now = clock.GetUtcNow();
        var row = await db.GitSourceMirrorLifecycles
            .SingleOrDefaultAsync(x => x.MirrorId == plan.SourceMirrorId, ct);
        string eventType;
        if (row is null)
        {
            row = new GitSourceMirrorLifecycleRecord
            {
                MirrorId = plan.SourceMirrorId,
                SourceSnapshotId = plan.SourceSnapshotId,
                WorkpackId = plan.SourceBundle!.WorkpackId,
                PlanId = plan.SourceBundle.PlanId,
                ReconciliationId = plan.SourceBundle.ReconciliationId,
                BundleSha256 = plan.SourceBundle.BundleSha256,
                SourceEvidenceSha256 = plan.SourceEvidenceSha256,
                RefsSha256 = GitSourceSnapshotMirrorIntegrity.SourceRefsSha(
                    plan.SourceRefs),
                CreatedAtUtc = now,
                LeaseGeneration = 1,
            };
            db.GitSourceMirrorLifecycles.Add(row);
            eventType = "LeaseAcquired";
        }
        else
        {
            // EF's identity map can return a previously tracked, stale
            // Leased row after a Busy refusal. The PostgreSQL advisory
            // lock protects the DB state, NOT the DbContext cache.
            // Always reload under the lock before making a decision.
            await db.Entry(row).ReloadAsync(ct);
            RequireSamePlan(row, plan);
            if (row.State == GitSourceMirrorLifecycleStates.RecoveryRequired)
                throw Reject("SourceMirrorLifecycleQuarantined");
            if (row.State == GitSourceMirrorLifecycleStates.Leased &&
                row.LeaseExpiresAtUtc > now)
                throw Reject("SourceMirrorLifecycleBusy");
            if (row.LeaseGeneration >=
                GitSourceMirrorLifecycleIntegrity.MaxGenerations)
                throw Reject("SourceMirrorLifecycleGenerationLimit");
            eventType = row.State switch
            {
                GitSourceMirrorLifecycleStates.Leased => "LeaseReclaimed",
                GitSourceMirrorLifecycleStates.Verified => "ReverifyStarted",
                _ => throw Reject("SourceMirrorLifecycleStateInvalid")
            };
            row.LeaseGeneration++;
        }
        var token = Guid.NewGuid();
        row.State = GitSourceMirrorLifecycleStates.Leased;
        row.LeaseToken = token;
        row.LeasedBy = workerId;
        // PostgreSQL timestamptz has microsecond precision while .NET
        // DateTimeOffset exposes 100ns ticks. Persist/return the SAME
        // canonical millisecond lease deadline across replica roundtrips.
        row.LeaseExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            (now + GitSourceMirrorLifecycleIntegrity.LeaseDuration)
                .ToUnixTimeMilliseconds());
        row.UpdatedAtUtc = now;
        row.FailureCode = null;
        db.GitSourceMirrorLifecycleEvents.Add(new()
        {
            MirrorId = row.MirrorId, Type = eventType,
            Generation = row.LeaseGeneration, WorkerId = workerId,
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(row.MirrorId, row.WorkpackId, workerId,
            token, row.LeaseGeneration, row.LeaseExpiresAtUtc.Value);
    }

    public async Task<GitSourceMirrorLifecycleStatus> CompleteAsync(
        GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
        GitSourceSnapshotMirrorProof proof, CancellationToken ct)
    {
        RequireEnabled();
        GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(plan.SourceMirrorId, ct);
        var now = clock.GetUtcNow();
        GitSourceMirrorLifecycleIntegrity.VerifyProof(plan, proof, now);
        var row = await OwnedAsync(plan, lease, now, ct);
        if (row.MirrorProofSha256 is not null &&
            (row.MirrorProofSha256 != proof.EvidenceSha256 ||
             row.ObjectSetSha256 != proof.ObjectSetSha256 ||
             row.ObjectCount != proof.VerifiedObjectCount))
            throw Reject("SourceMirrorLifecycleProofConflict");

        row.State = GitSourceMirrorLifecycleStates.Verified;
        row.MirrorProofSha256 = proof.EvidenceSha256;
        row.ObjectSetSha256 = proof.ObjectSetSha256;
        row.ObjectCount = proof.VerifiedObjectCount;
        row.VerifiedAtUtc = proof.VerifiedAtUtc;
        row.LeaseToken = null;
        row.LeasedBy = null;
        row.LeaseExpiresAtUtc = null;
        row.FailureCode = null;
        row.UpdatedAtUtc = now;
        db.GitSourceMirrorLifecycleEvents.Add(new()
        {
            MirrorId = row.MirrorId, Type = "Verified",
            Generation = row.LeaseGeneration,
            WorkerId = lease.WorkerId, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task QuarantineAsync(
        GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
        string failureCode, CancellationToken ct)
    {
        RequireEnabled();
        GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
        if (string.IsNullOrWhiteSpace(failureCode) ||
            failureCode.Length > 70 ||
            failureCode.Any(c => !char.IsAsciiLetterOrDigit(c) &&
                c is not '_' and not '-'))
            throw Reject("SourceMirrorLifecycleFailureInvalid");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(plan.SourceMirrorId, ct);
        var now = clock.GetUtcNow();
        var row = await OwnedAsync(plan, lease, now, ct);
        row.State = GitSourceMirrorLifecycleStates.RecoveryRequired;
        row.FailureCode = failureCode;
        row.LeaseToken = null;
        row.LeasedBy = null;
        row.LeaseExpiresAtUtc = null;
        row.UpdatedAtUtc = now;
        db.GitSourceMirrorLifecycleEvents.Add(new()
        {
            MirrorId = row.MirrorId, Type = "Quarantined",
            Generation = row.LeaseGeneration,
            WorkerId = lease.WorkerId, OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<GitSourceMirrorLifecycleStatus?> GetAsync(
        Guid mirrorId, CancellationToken ct)
    {
        RequireEnabled();
        if (mirrorId == Guid.Empty)
            throw Reject("SourceMirrorLifecycleIdentityInvalid");
        var row = await db.GitSourceMirrorLifecycles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MirrorId == mirrorId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<GitSourceMirrorLifecycleEvent>> EventsAsync(
        Guid mirrorId, CancellationToken ct)
    {
        RequireEnabled();
        if (mirrorId == Guid.Empty)
            throw Reject("SourceMirrorLifecycleIdentityInvalid");
        return (await db.GitSourceMirrorLifecycleEvents.AsNoTracking()
            .Where(x => x.MirrorId == mirrorId)
            .OrderBy(x => x.Id).Take(2000).ToArrayAsync(ct))
            .Select(x => new GitSourceMirrorLifecycleEvent(
                x.Id, x.MirrorId, x.Type, x.Generation,
                x.WorkerId, x.OccurredAtUtc)).ToArray();
    }

    private async Task<GitSourceMirrorLifecycleRecord> OwnedAsync(
        GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
        DateTimeOffset now, CancellationToken ct)
    {
        if (lease is null || lease.MirrorId != plan.SourceMirrorId ||
            lease.WorkpackId != plan.SourceBundle!.WorkpackId ||
            lease.LeaseToken == Guid.Empty || lease.WorkerId == Guid.Empty ||
            lease.Generation < 1)
            throw Reject("SourceMirrorLifecycleLeaseInvalid");
        var row = await db.GitSourceMirrorLifecycles
            .SingleOrDefaultAsync(x => x.MirrorId == plan.SourceMirrorId, ct)
            ?? throw Reject("SourceMirrorLifecycleNotFound");
        // Critical fencing check: a scoped DbContext may have tracked
        // this object before another replica reclaimed the lease.
        // Reload from PostgreSQL under the transaction advisory lock.
        await db.Entry(row).ReloadAsync(ct);
        RequireSamePlan(row, plan);
        if (row.State != GitSourceMirrorLifecycleStates.Leased ||
            row.LeaseGeneration != lease.Generation ||
            row.LeaseToken != lease.LeaseToken ||
            row.LeasedBy != lease.WorkerId ||
            row.LeaseExpiresAtUtc is null ||
            row.LeaseExpiresAtUtc <= now ||
            lease.ExpiresAtUtc != row.LeaseExpiresAtUtc)
            throw Reject("SourceMirrorLifecycleLeaseLost");
        return row;
    }

    private static void RequireSamePlan(
        GitSourceMirrorLifecycleRecord row,
        GitHubTrustedRemoteReadPlan plan)
    {
        var bundle = plan.SourceBundle!;
        if (row.SourceSnapshotId != plan.SourceSnapshotId ||
            row.WorkpackId != bundle.WorkpackId ||
            row.PlanId != bundle.PlanId ||
            row.ReconciliationId != bundle.ReconciliationId ||
            row.BundleSha256 != bundle.BundleSha256 ||
            row.SourceEvidenceSha256 != plan.SourceEvidenceSha256 ||
            row.RefsSha256 != GitSourceSnapshotMirrorIntegrity.SourceRefsSha(
                plan.SourceRefs) ||
            row.RemotePushInvoked || row.RemoteWritesAuthorized ||
            row.MayAutomaticallyRetry || row.MigrationComplete)
            throw Reject("SourceMirrorLifecyclePlanConflict");
    }

    private async Task LockAsync(Guid mirrorId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(736061, hashtext({mirrorId.ToString()}))",
            ct);

    private void RequireEnabled()
    {
        if (!options.Enabled)
            throw Reject("SourceMirrorLifecycleDisabled");
    }

    private static GitSourceMirrorLifecycleException Reject(string code) =>
        new(code);

    private static GitSourceMirrorLifecycleStatus Map(
        GitSourceMirrorLifecycleRecord r) =>
        new(r.MirrorId, r.SourceSnapshotId, r.WorkpackId, r.State,
            r.LeaseGeneration, r.LeasedBy, r.LeaseExpiresAtUtc,
            r.BundleSha256, r.SourceEvidenceSha256, r.RefsSha256,
            r.ObjectSetSha256, r.ObjectCount, r.MirrorProofSha256,
            r.FailureCode, r.UpdatedAtUtc,
            r.RemotePushInvoked, r.RemoteWritesAuthorized,
            r.MayAutomaticallyRetry, r.MigrationComplete);
}
