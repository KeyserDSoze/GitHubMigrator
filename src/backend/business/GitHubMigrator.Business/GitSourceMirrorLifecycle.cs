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
        var recalculated = GitSourceSnapshotMirrorIntegrity.Seal(
            plan, proof.ObjectSetSha256, proof.VerifiedObjectCount,
            proof.VerifiedAtUtc);
        if (recalculated.EvidenceSha256 != proof.EvidenceSha256)
            throw new GitSourceMirrorLifecycleException(
                "SourceMirrorLifecycleProofInvalid");
    }
}

// Internal wrapper: each physical verification is admitted by a
// PostgreSQL fencing generation. F-060 remains responsible for
// filesystem/POSIX safety, full-object fsck and bundle rehashing.
public sealed class DurableGitSourceSnapshotMirrorMaterializer(
    IGitSourceSnapshotMirrorMaterializer physical,
    IGitSourceMirrorLifecycleJournal lifecycle)
    : IGitSourceSnapshotMirrorMaterializer
{
    public async Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
        GitHubTrustedRemoteReadPlan plan, CancellationToken ct)
    {
        GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
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

        try
        {
            var proof = await physical.MaterializeAsync(plan, ct);
            await lifecycle.CompleteAsync(plan, lease, proof, ct);
            return proof;
        }
        catch (GitSourceSnapshotMirrorException ex)
        {
            // A corrupted bundle/mirror is NOT automatically retried.
            // Only still-owned, unexpired leases can be quarantined.
            // The original failure remains the caller-visible cause.
            try
            {
                await lifecycle.QuarantineAsync(plan, lease,
                    ex.Code, CancellationToken.None);
            }
            catch (GitSourceMirrorLifecycleException) { }
            throw;
        }
        catch (GitSourceMirrorLifecycleException ex) when
            (ex.Code is "SourceMirrorLifecycleProofConflict" or
                "SourceMirrorLifecycleProofInvalid")
        {
            try
            {
                await lifecycle.QuarantineAsync(plan, lease,
                    ex.Code, CancellationToken.None);
            }
            catch (GitSourceMirrorLifecycleException) { }
            throw;
        }
        // Cancellation or worker crash deliberately leaves the lease
        // to expire. A new generation must physically re-verify first.
    }
}
