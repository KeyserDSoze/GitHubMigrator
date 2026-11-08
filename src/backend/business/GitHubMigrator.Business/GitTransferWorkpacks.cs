namespace GitHubMigrator.Business;

// F-034: a durable, resumable audit/preparation workpack only. No Git
// process runner, target writer, PAT, approval or executable payload exists.
public static class GitTransferWorkpackStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Blocked = "Blocked";
    public const string Failed = "Failed";
}

public static class GitTransferWorkpackPhases
{
    public const string EvidencePending = "EvidencePending";
    public const string EvidenceChecked = "EvidenceChecked";
    public const string WriteBlocked = "WriteBlocked";
}

public sealed record GitTransferWorkpackView(
    Guid Id, Guid ReconciliationId, Guid PlanId, string SourceRepositoryId,
    string TargetRepositoryName, string ManifestSha256, string ReconciliationSha256,
    string TargetEvidenceSha256, string State, string Phase,
    int Attempts, int MaxAttempts, DateTimeOffset AvailableAtUtc,
    DateTimeOffset? LeaseUntilUtc, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, DateTimeOffset? BlockedAtUtc,
    string? LastErrorCode, bool IsCurrent, bool ReadyForExecution);

public sealed record GitTransferWorkpackEvent(
    long Id, Guid WorkpackId, string Type, string Phase,
    int Attempt, DateTimeOffset OccurredAtUtc, string? ErrorCode);

public sealed record GitTransferWorkpackLease(
    Guid Id, Guid ReconciliationId, Guid PlanId, Guid LeaseToken,
    int Attempt, string ReconciliationSha256, string Phase);

public sealed class GitTransferWorkpackException(string code)
    : Exception("Git transfer preparation rejected")
{
    public string Code { get; } = code;
}

public interface IGitTransferWorkpackRepository
{
    Task<GitTransferWorkpackView> EnqueueAsync(
        Guid preparationId, Guid planId, Guid reconciliationId,
        string expectedManifestSha256, string expectedReconciliationSha256,
        Guid actorId, CancellationToken ct);
    Task<GitTransferWorkpackView?> GetAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct);
    Task<IReadOnlyList<GitTransferWorkpackView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct);
    Task<IReadOnlyList<GitTransferWorkpackEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct);
    Task<GitTransferWorkpackLease?> ClaimAsync(
        Guid leaseToken, TimeSpan leaseDuration, CancellationToken ct);
    Task<bool> CheckpointEvidenceAsync(GitTransferWorkpackLease lease, CancellationToken ct);
    Task<bool> BlockAtWriteBarrierAsync(GitTransferWorkpackLease lease, CancellationToken ct);
    Task<bool> FailAsync(
        GitTransferWorkpackLease lease, string safeCode,
        bool permanent, CancellationToken ct);
}

public sealed class GitTransferWorkpackService(IGitTransferWorkpackRepository repo)
{
    private static bool ValidSha(string? sha) => sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public Task<GitTransferWorkpackView> EnqueueAsync(
        Guid preparationId, Guid planId, Guid reconciliationId,
        string expectedManifestSha256, string expectedReconciliationSha256,
        Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            reconciliationId == Guid.Empty || actorId == Guid.Empty ||
            !ValidSha(expectedManifestSha256) ||
            !ValidSha(expectedReconciliationSha256))
            throw new GitTransferWorkpackException("InvalidTransferWorkpackRequest");

        return repo.EnqueueAsync(preparationId, planId, reconciliationId,
            expectedManifestSha256, expectedReconciliationSha256, actorId, ct);
    }

    public Task<GitTransferWorkpackView?> GetAsync(
        Guid preparationId, Guid planId, Guid id, CancellationToken ct) =>
        repo.GetAsync(preparationId, planId, id, ct);

    public Task<IReadOnlyList<GitTransferWorkpackView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct) =>
        repo.ListAsync(preparationId, planId, ct);

    public Task<IReadOnlyList<GitTransferWorkpackEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid id, CancellationToken ct) =>
        repo.EventsAsync(preparationId, planId, id, ct);
}

public sealed class GitTransferWorkpackProcessor(IGitTransferWorkpackRepository jobs)
{
    // The execution deadline is deliberately shorter than the lease.
    // Phase changes are compare-and-swap on token + attempt + expiry.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan WorkDeadline = TimeSpan.FromSeconds(45);

    public async Task<bool> RunOnceAsync(CancellationToken shutdown)
    {
        var lease = await jobs.ClaimAsync(Guid.NewGuid(), LeaseDuration, shutdown);
        if (lease is null) return false;
        try
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            work.CancelAfter(WorkDeadline);

            // The entire lifecycle is read-only evidence verification:
            // no operation here can call an Azure/GitHub provider or Git.
            if (lease.Phase == GitTransferWorkpackPhases.EvidencePending)
            {
                if (!await jobs.CheckpointEvidenceAsync(lease, work.Token))
                    return true; // lost lease or evidence changed
            }
            if (!await jobs.BlockAtWriteBarrierAsync(lease, work.Token))
                return true;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw; // crash/restart recovery via lease expiry
        }
        catch (OperationCanceledException)
        {
            await jobs.FailAsync(lease, "TransferPreparationDeadline", false, shutdown);
        }
        catch (GitTransferWorkpackException ex)
        {
            // Never persist or log provider messages, only closed safe codes.
            await jobs.FailAsync(lease, ex.Code, true, shutdown);
        }
        catch (Exception)
        {
            await jobs.FailAsync(lease, "TransferPreparationFailure", false, shutdown);
        }
        return true;
    }
}
