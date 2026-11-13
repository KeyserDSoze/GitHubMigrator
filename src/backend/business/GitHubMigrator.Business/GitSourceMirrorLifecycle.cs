namespace GitHubMigrator.Business;

// F-061: durable admission for the F-060 local-only materializer.
// No GitHub URL, token, push command or remote execution permission.
public static class GitSourceMirrorLifecycleStates
{
    public const string Leased = "Leased";
    public const string Verified = "Verified";
    public const string RecoveryRequired = "RecoveryRequired";
}

public sealed class GitSourceMirrorLifecycleException(string code)
    : Exception("Durable source mirror lease refused")
{
    public string Code { get; } = code;
}

public sealed record GitSourceMirrorLease(
    Guid MirrorId, Guid WorkpackId, Guid WorkerId,
    Guid LeaseToken, long Generation, DateTimeOffset ExpiresAtUtc);

public sealed record GitSourceMirrorLifecycleStatus(
    Guid MirrorId, Guid SourceSnapshotId, Guid WorkpackId,
    string State, long Generation, Guid? LeasedBy,
    DateTimeOffset? ExpiresAtUtc, string BundleSha256,
    string SourceEvidenceSha256, string RefsSha256,
    string? ObjectSetSha256, int? ObjectCount,
    string? MirrorProofSha256, string? FailureCode,
    DateTimeOffset UpdatedAtUtc,
    bool RemotePushInvoked, bool RemoteWritesAuthorized,
    bool MayAutomaticallyRetry, bool MigrationComplete);

public sealed record GitSourceMirrorLifecycleEvent(
    long Id, Guid MirrorId, string Type, long Generation,
    Guid WorkerId, DateTimeOffset OccurredAtUtc);

public sealed record GitSourceMirrorLifecycleOptions(bool Enabled);

public interface IGitSourceMirrorLifecycleJournal
{
    Task<GitSourceMirrorLease> AcquireAsync(
        GitHubTrustedRemoteReadPlan plan, Guid workerId, CancellationToken ct);
    // Rotate only the deadline, never the ownership generation or
    // lease admission timestamp used by F-062 freshness checks.
    Task<GitSourceMirrorLease> RenewAsync(
        GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
        CancellationToken ct);
    Task<GitSourceMirrorLifecycleStatus> CompleteAsync(
        GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
        GitSourceSnapshotMirrorProof proof, CancellationToken ct);
    // Only a failed, still-owned generation may be quarantined. No
    // blind retry from a known-corrupt or conflicting physical mirror.
    Task QuarantineAsync(
        GitHubTrustedRemoteReadPlan plan, GitSourceMirrorLease lease,
        string failureCode, CancellationToken ct);
    Task<GitSourceMirrorLifecycleStatus?> GetAsync(
        Guid mirrorId, CancellationToken ct);
    Task<IReadOnlyList<GitSourceMirrorLifecycleEvent>> EventsAsync(
        Guid mirrorId, CancellationToken ct);
}

public static class GitSourceMirrorLifecycleIntegrity
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxProofAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxLeaseLifetime = TimeSpan.FromHours(2);
    public const int MaxGenerations = 2000;

    public static void VerifyProof(
        GitHubTrustedRemoteReadPlan plan,
        GitSourceSnapshotMirrorProof proof,
        DateTimeOffset now)
    {
        GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
        if (proof is null ||
            proof.SourceSnapshotId != plan.SourceSnapshotId ||
            proof.PhysicalMirrorId != plan.SourceMirrorId ||
            proof.WorkpackId != plan.SourceBundle!.WorkpackId ||
            proof.BundleSha256 != plan.SourceBundle.BundleSha256 ||
            proof.SourceEvidenceSha256 != plan.SourceEvidenceSha256 ||
            proof.SourceRefSetSha256 !=
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(plan.SourceRefs) ||
            proof.VerifiedRefCount != plan.SourceRefs.Count ||
            proof.VerifiedAtUtc < now - MaxProofAge ||
            proof.VerifiedAtUtc > now.AddSeconds(5) ||
            proof.RemotePushInvoked || proof.RemoteWritesAuthorized ||
            proof.MayAutomaticallyRetry || proof.MigrationComplete)
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleProofInvalid");
        GitSourceSnapshotMirrorProof recalculated;
        try
        {
            recalculated = GitSourceSnapshotMirrorIntegrity.Seal(
                plan, proof.ObjectSetSha256, proof.VerifiedObjectCount,
                proof.VerifiedAtUtc);
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleProofInvalid");
        }
        if (recalculated.EvidenceSha256 != proof.EvidenceSha256 ||
            recalculated.VerificationReceiptSha256 !=
                proof.VerificationReceiptSha256)
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleProofInvalid");
    }

    // Proofs from previous generations remain content-identical, but
    // are not evidence of a new physical fsck. PostgreSQL supplies the
    // current lease admission timestamp under its advisory lock.
    public static void VerifyProofForLease(
        GitHubTrustedRemoteReadPlan plan,
        GitSourceSnapshotMirrorProof proof,
        DateTimeOffset acquiredAtUtc, DateTimeOffset now)
    {
        VerifyProof(plan, proof, now);
        if (acquiredAtUtc == default || acquiredAtUtc > now ||
            proof.VerifiedAtUtc < acquiredAtUtc)
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleProofStale");
    }
}

// Internal wrapper: physical Git work is admitted by PostgreSQL and
// remains fenced during long-running source verification. The heartbeat
// never sends data remotely and cannot authorize a destination write.
public sealed class DurableGitSourceSnapshotMirrorMaterializer(
    IGitSourceSnapshotMirrorMaterializer physical,
    IGitSourceMirrorLifecycleJournal lifecycle,
    TimeSpan? heartbeatInterval = null)
    : IGitSourceSnapshotMirrorMaterializer
{
    public async Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
        GitHubTrustedRemoteReadPlan plan, CancellationToken ct)
    {
        GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
        var interval = heartbeatInterval ??
            GitSourceMirrorLifecycleIntegrity.LeaseDuration / 3;
        if (interval <= TimeSpan.Zero ||
            interval >= GitSourceMirrorLifecycleIntegrity.LeaseDuration)
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        var workerId = Guid.NewGuid();
        GitSourceMirrorLease? lease = null;
        for (var retry = 0; retry < 240; retry++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                lease = await lifecycle.AcquireAsync(plan, workerId, ct);
                break;
            }
            catch (GitSourceMirrorLifecycleException ex) when
                (ex.Code == "SourceMirrorLifecycleBusy" && retry < 239)
            {
                await Task.Delay(125, ct);
            }
        }
        if (lease is null)
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleBusy");

        var currentLease = lease;
        using var stopHeartbeat =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Keep physical work and the heartbeat independent. No two
        // journal operations overlap on the scoped EF DbContext:
        // the heartbeat is stopped/joined before completion/quarantine.
        var heartbeat = HeartbeatAsync(
            plan, lease, interval, renewed => currentLease = renewed,
            stopHeartbeat.Token);
        try
        {
            var physicalTask = physical.MaterializeAsync(
                plan, stopHeartbeat.Token);
            var first = await Task.WhenAny(physicalTask, heartbeat);
            if (first == heartbeat)
            {
                // A failed renewal revokes physical work; an old owner
                // may not publish its result even if cancellation loses
                // the race with the final filesystem promotion.
                stopHeartbeat.Cancel();
                try { await physicalTask; }
                catch (Exception) when (heartbeat.IsFaulted ||
                    heartbeat.IsCanceled) { }
                await heartbeat;
                throw new GitSourceMirrorLifecycleException(
                    "SourceMirrorLifecycleLeaseLost");
            }

            stopHeartbeat.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when
                (stopHeartbeat.IsCancellationRequested) { }

            var proof = await physicalTask;
            await lifecycle.CompleteAsync(plan, currentLease, proof, ct);
            return proof;
        }
        catch (GitSourceSnapshotMirrorException ex)
        {
            await JoinHeartbeatAfterFailureAsync(heartbeat, stopHeartbeat);
            // A damaged bundle/mirror is NOT automatically retried.
            // The owned generation is quarantined; stale owners cannot.
            try
            {
                await lifecycle.QuarantineAsync(plan, currentLease,
                    ex.Code, CancellationToken.None);
            }
            catch (GitSourceMirrorLifecycleException) { }
            throw;
        }
        catch (GitSourceMirrorLifecycleException ex) when
            (ex.Code is "SourceMirrorLifecycleProofConflict" or
                "SourceMirrorLifecycleProofInvalid" or
                "SourceMirrorLifecycleProofStale")
        {
            await JoinHeartbeatAfterFailureAsync(heartbeat, stopHeartbeat);
            try
            {
                await lifecycle.QuarantineAsync(plan, currentLease,
                    ex.Code, CancellationToken.None);
            }
            catch (GitSourceMirrorLifecycleException) { }
            throw;
        }
        finally
        {
            await JoinHeartbeatAfterFailureAsync(heartbeat, stopHeartbeat);
            // Cancellation/crash leaves the generation to expire. The
            // next owner must physically re-verify, never reuse a receipt.
        }
    }

    private static async Task JoinHeartbeatAfterFailureAsync(
        Task heartbeat, CancellationTokenSource stop)
    {
        stop.Cancel();
        // Only invoked during cleanup: the original physical/lease error
        // must not be masked by another failed timer tick.
        try { await heartbeat; }
        catch (Exception) when (heartbeat.IsFaulted || heartbeat.IsCanceled) { }
    }

    private async Task HeartbeatAsync(
        GitHubTrustedRemoteReadPlan plan,
        GitSourceMirrorLease admitted, TimeSpan interval,
        Action<GitSourceMirrorLease> renewed,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        var current = admitted;
        while (await timer.WaitForNextTickAsync(ct))
        {
            // Once a tick begins, join its database transaction even
            // when physical work finishes: cancellation mid-commit could
            // otherwise leave the wrapper with a stale deadline token.
            current = await lifecycle.RenewAsync(
                plan, current, CancellationToken.None);
            renewed(current);
        }
    }
}
