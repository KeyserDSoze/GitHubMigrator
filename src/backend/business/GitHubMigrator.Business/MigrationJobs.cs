namespace GitHubMigrator.Business;

// F-008 slice 1: durable jobs can ONLY normalize a previously saved discovery snapshot.
// They never grant permissions, approve plans, contact providers or publish to GitHub.
public static class MigrationJobKinds
{
    public const string StageSnapshot = "StageSnapshot";
    public const string DiscoverSource = "DiscoverSource";
}

public static class MigrationJobStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

public sealed record MigrationJobView(
    Guid Id, string Kind, Guid SnapshotId, Guid SourceConnectionId,
    Guid EnqueuedBy, string State, int Attempts, int MaxAttempts,
    DateTimeOffset AvailableAtUtc, DateTimeOffset? LeaseUntilUtc,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc, string? LastErrorCode, StagingSyncResult? Result);

public sealed record MigrationJobEventView(
    long Id, Guid JobId, string EventType, int Attempt, DateTimeOffset OccurredAtUtc,
    string? ErrorCode);

public sealed record MigrationJobLease(
    Guid Id, Guid SnapshotId, Guid SourceConnectionId, string Kind,
    Guid LeaseToken, int Attempt);

public interface IMigrationJobRepository
{
    Task<MigrationJobView> EnqueueStagingAsync(
        Guid snapshotId, Guid sourceConnectionId, Guid actorId, CancellationToken ct);
    Task<MigrationJobView> EnqueueDiscoveryAsync(
        Guid sourceConnectionId, Guid actorId, Guid requestId, CancellationToken ct);
    Task<bool> CheckpointSnapshotAsync(MigrationJobLease lease, Guid snapshotId, CancellationToken ct);
    Task<MigrationJobView?> GetAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<MigrationJobView>> ListAsync(Guid sourceConnectionId, CancellationToken ct);
    Task<IReadOnlyList<MigrationJobEventView>> EventsAsync(Guid jobId, CancellationToken ct);
    Task<MigrationJobLease?> ClaimAsync(Guid leaseToken, TimeSpan leaseDuration, CancellationToken ct);
    Task<bool> CompleteAsync(MigrationJobLease lease, StagingSyncResult result, CancellationToken ct);
    Task<bool> FailAsync(MigrationJobLease lease, string errorCode, bool permanent, CancellationToken ct);
}

public sealed class MigrationJobInputException(string code)
    : Exception("Invalid migration job request")
{
    public string Code { get; } = code;
}

public sealed class MigrationJobService(
    IDiscoveryRepository discoveries, IMigrationJobRepository jobs,
    IConnectionRepository connections)
{
    public async Task<MigrationJobView> EnqueueStageAsync(Guid snapshotId, Guid actorId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || actorId == Guid.Empty)
            throw new MigrationJobInputException("InvalidJobContext");
        var snapshot = await discoveries.GetAsync(snapshotId, ct)
            ?? throw new KeyNotFoundException("SnapshotNotFound");
        if (snapshot.Id != snapshotId || snapshot.Snapshot.SnapshotId != snapshotId ||
            snapshot.ConnectionId == Guid.Empty)
            throw new MigrationJobInputException("InvalidJobSnapshot");
        return await jobs.EnqueueStagingAsync(snapshotId, snapshot.ConnectionId, actorId, ct);
    }

    public async Task<MigrationJobView> EnqueueDiscoveryAsync(
        Guid sourceConnectionId, Guid actorId, Guid requestId, CancellationToken ct)
    {
        if (sourceConnectionId == Guid.Empty || actorId == Guid.Empty || requestId == Guid.Empty)
            throw new MigrationJobInputException("InvalidJobContext");
        var source = await connections.GetAsync(sourceConnectionId, ct)
            ?? throw new KeyNotFoundException("ConnectionNotFound");
        if (source.Direction != GitHubMigrator.Domain.ConnectionDirection.Source ||
            !string.Equals(source.ProviderKey, "azure-devops-services", StringComparison.OrdinalIgnoreCase))
            throw new MigrationJobInputException("UnsupportedDiscoverySource");
        if (source.ValidationCode != "Verified")
            throw new MigrationJobInputException("SourceNotVerified");
        return await jobs.EnqueueDiscoveryAsync(sourceConnectionId, actorId, requestId, ct);
    }

    public Task<MigrationJobView?> GetAsync(Guid jobId, CancellationToken ct) =>
        jobs.GetAsync(jobId, ct);

    public Task<IReadOnlyList<MigrationJobView>> ListAsync(Guid sourceId, CancellationToken ct) =>
        jobs.ListAsync(sourceId, ct);

    public Task<IReadOnlyList<MigrationJobEventView>> EventsAsync(Guid jobId, CancellationToken ct) =>
        jobs.EventsAsync(jobId, ct);
}

public sealed class MigrationJobProcessor(
    IMigrationJobRepository jobs, StagingSyncService staging,
    DiscoveryService? discovery = null)
{
    // Lease > execution deadline, ensuring normal slow work cannot run past its lease.
    // An abrupt process death is recovered after the lease expires by another worker.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExecutionDeadline = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DiscoveryDeadline = TimeSpan.FromMinutes(4);

    public async Task<bool> RunOnceAsync(CancellationToken shutdown)
    {
        var lease = await jobs.ClaimAsync(Guid.NewGuid(), LeaseDuration, shutdown);
        if (lease is null) return false;

        try
        {
            if (lease.Kind is not (MigrationJobKinds.StageSnapshot or MigrationJobKinds.DiscoverSource))
            {
                await jobs.FailAsync(lease, "UnsupportedJobKind", true, shutdown);
                return true;
            }
            using var work = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            // The deadline applies to the *whole* operation, not each phase.
            work.CancelAfter(lease.Kind == MigrationJobKinds.DiscoverSource
                ? DiscoveryDeadline : ExecutionDeadline);

            Guid snapshotId = lease.SnapshotId;
            if (lease.Kind == MigrationJobKinds.DiscoverSource)
            {
                if (discovery is null)
                    throw new InvalidOperationException("Discovery worker service not registered");
                // Stable snapshot ID = job ID. A retry checks PostgreSQL first:
                // if source discovery already committed, do not call Azure DevOps again.
                var snapshot = await discovery.ScanAsync(lease.SourceConnectionId, work.Token, lease.Id);
                snapshotId = snapshot.Id;
                if (snapshotId != lease.Id || snapshot.ConnectionId != lease.SourceConnectionId)
                    throw new MigrationJobInputException("DiscoveryJobSourceMismatch");
                if (lease.SnapshotId == Guid.Empty &&
                    !await jobs.CheckpointSnapshotAsync(lease, snapshotId, work.Token))
                    return true; // ownership lost; the new lease is responsible
            }
            var result = await staging.SyncAsync(snapshotId, work.Token);
            await jobs.CompleteAsync(lease, result, shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Do not erase the lease. The next worker may safely reclaim it after expiration.
            throw;
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(lease, "WorkerDeadlineExceeded", false, shutdown);
        }
        catch (StagingStaleSnapshotException)
        {
            await RecordFailureAsync(lease, "StagingSnapshotOutOfOrder", true, shutdown);
        }
        catch (StagingSyncInputException ex)
        {
            await RecordFailureAsync(lease, ex.Code, true, shutdown);
        }
        catch (DiscoveryProviderException ex)
        {
            var permanent = ex.Code is "Unauthorized" or "InsufficientScope" or
                "CredentialMissing" or "UnexpectedResponse";
            await RecordFailureAsync(lease, ex.Code, permanent, shutdown);
        }
        catch (MigrationJobInputException ex)
        {
            await RecordFailureAsync(lease, ex.Code, true, shutdown);
        }
        catch (ArgumentException)
        {
            await RecordFailureAsync(lease, "UnsupportedDiscoverySource", true, shutdown);
        }
        catch (KeyNotFoundException)
        {
            await RecordFailureAsync(lease, "SnapshotNotFound", true, shutdown);
        }
        catch (Exception)
        {
            // Error types / stack traces may contain sensitive provider data.
            // Never persist or send exception messages as job results.
            await RecordFailureAsync(lease,
                lease.Kind == MigrationJobKinds.DiscoverSource
                    ? "DiscoveryExecutionFailed" : "StagingExecutionFailed",
                false, shutdown);
        }
        return true;
    }

    private async Task RecordFailureAsync(
        MigrationJobLease lease, string errorCode, bool permanent, CancellationToken ct)
    {
        await jobs.FailAsync(lease, errorCode, permanent, ct);
    }
}
