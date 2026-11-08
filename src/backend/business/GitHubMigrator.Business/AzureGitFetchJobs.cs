namespace GitHubMigrator.Business;

// Source-only transport queue. "Completed" means F-035 bundle evidence was
// committed; it does NOT mean a GitHub migration was authorized or executed.
public static class AzureGitFetchJobStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class AzureGitFetchJobPhases
{
    public const string Pending = "Pending";
    public const string MirrorVerified = "MirrorVerified";
    public const string BundleVerified = "BundleVerified";
}

public sealed record AzureGitFetchJobView(
    Guid Id, Guid WorkpackId, Guid PlanId, Guid ReconciliationId,
    string ReconciliationSha256, string State, string Phase,
    int Attempts, int MaxAttempts, DateTimeOffset AvailableAtUtc,
    DateTimeOffset? LeaseUntilUtc, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, DateTimeOffset? CompletedAtUtc,
    string? LastErrorCode, bool IsCurrent, bool ReadyForExecution);

public sealed record AzureGitFetchEventView(
    long Id, Guid JobId, string Type, string Phase, int Attempt,
    DateTimeOffset OccurredAtUtc, string? ErrorCode);

public sealed record AzureGitFetchLease(
    Guid JobId, Guid PreparationId, Guid PlanId, Guid WorkpackId,
    string ReconciliationSha256, Guid Token, int Attempt, string Phase);

public sealed class AzureGitFetchJobException(string code)
    : Exception("Source Git fetch job rejected")
{
    public string Code { get; } = code;
}

public interface IAzureGitFetchJobRepository
{
    Task<AzureGitFetchJobView> EnqueueAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, Guid actorId, CancellationToken ct);
    Task<AzureGitFetchJobView?> GetAsync(
        Guid preparationId, Guid planId, Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<AzureGitFetchJobView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct);
    Task<IReadOnlyList<AzureGitFetchEventView>> EventsAsync(
        Guid preparationId, Guid planId, Guid jobId, CancellationToken ct);
    Task<AzureGitFetchLease?> ClaimAsync(
        Guid leaseToken, TimeSpan leaseDuration, CancellationToken ct);
    Task<bool> MirrorVerifiedAsync(AzureGitFetchLease lease, CancellationToken ct);
    Task<bool> BundleVerifiedAsync(
        AzureGitFetchLease lease, string bundleSha256, CancellationToken ct);
    Task<bool> FailAsync(
        AzureGitFetchLease lease, string safeErrorCode, bool permanent, CancellationToken ct);
}

public sealed class AzureGitFetchJobService(IAzureGitFetchJobRepository repository)
{
    public Task<AzureGitFetchJobView> EnqueueAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            workpackId == Guid.Empty || actorId == Guid.Empty ||
            !ValidSha(expectedReconciliationSha256))
            throw new AzureGitFetchJobException("InvalidAzureGitFetchJobRequest");
        return repository.EnqueueAsync(preparationId, planId, workpackId,
            expectedReconciliationSha256, actorId, ct);
    }

    public Task<AzureGitFetchJobView?> GetAsync(
        Guid preparationId, Guid planId, Guid jobId, CancellationToken ct) =>
        repository.GetAsync(preparationId, planId, jobId, ct);

    public Task<IReadOnlyList<AzureGitFetchJobView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct) =>
        repository.ListAsync(preparationId, planId, ct);

    public Task<IReadOnlyList<AzureGitFetchEventView>> EventsAsync(
        Guid preparationId, Guid planId, Guid jobId, CancellationToken ct) =>
        repository.EventsAsync(preparationId, planId, jobId, ct);

    public static bool ValidSha(string? sha) => sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class AzureGitFetchJobProcessor(
    IAzureGitFetchJobRepository jobs,
    IAzureGitSourceAcquisition acquisition)
{
    // F-036 commands are bounded at 115 seconds; the process phase deadline
    // must be strictly less than its durable lease to fence a timed-out worker.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WorkDeadline = TimeSpan.FromMinutes(4);

    public async Task<bool> RunOnceAsync(CancellationToken shutdown)
    {
        var lease = await jobs.ClaimAsync(Guid.NewGuid(), LeaseDuration, shutdown);
        if (lease is null) return false;

        try
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            work.CancelAfter(WorkDeadline);

            if (lease.Phase == AzureGitFetchJobPhases.Pending)
            {
                await acquisition.FetchMirrorAsync(
                    lease.PreparationId, lease.PlanId, lease.WorkpackId,
                    lease.ReconciliationSha256, work.Token);
                if (!await jobs.MirrorVerifiedAsync(lease, work.Token))
                    return true; // lost lease; no stale-worker checkpoint
            }

            var proof = await acquisition.BundleVerifiedMirrorAsync(
                lease.PreparationId, lease.PlanId, lease.WorkpackId,
                lease.ReconciliationSha256, work.Token);
            await jobs.BundleVerifiedAsync(lease, proof.BundleSha256, work.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        { throw; } // lease expiry + verified retry; never mark success
        catch (OperationCanceledException)
        { await jobs.FailAsync(lease, "AzureGitAcquisitionDeadline", false, shutdown); }
        catch (GitSourceBundleException ex)
        {
            var permanent = ex.Code is
                "BundleProofMismatch" or "BundleWorkpackNotCurrent" or
                "BundlePlanNotCurrent" or "BundleSourceEvidenceMismatch" or
                "BundleReconciliationNotCurrent" or "AzureGitSourceNotVerified" or
                "AzureGitSourceMappingInvalid" or "AzureGitMirrorSymlinkBlocked" or
                "AzureGitSourceSnapshotUnavailable" or "AzureGitMirrorSizeLimit" or
                "AzureGitSourceRefMismatch" or "AzureGitUnexpectedRef" or
                "AzureGitBareMirrorInvalid" or "AzureGitCredentialUnavailable";
            await jobs.FailAsync(lease, ex.Code, permanent, shutdown);
        }
        catch (AzureGitFetchJobException ex)
        { await jobs.FailAsync(lease, ex.Code, true, shutdown); }
        catch (Exception)
        { await jobs.FailAsync(lease, "AzureGitAcquisitionFailure", false, shutdown); }
        return true;
    }
}
