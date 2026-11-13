using GitHubMigrator.Domain;

using Microsoft.Extensions.Logging;

namespace GitHubMigrator.Business;

// F-026 produces review-only, source-pinned preparation items. There is NO
// destination executor in this contract; these rows are never approvals.
public static class IncrementalPreparationStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Prepared = "Prepared";
    public const string Failed = "Failed";
}

public sealed record IncrementalPreparationView(
    Guid Id, Guid SnapshotId, Guid SourceConnectionId, Guid DestinationConnectionId,
    Guid EnqueuedBy, string State, int Attempts, int MaxAttempts,
    DateTimeOffset AvailableAtUtc, DateTimeOffset? LeaseUntilUtc,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PreparedAtUtc, string? LastErrorCode,
    int ItemCount, int SensitiveItemCount, string? EvidenceSha256);

public sealed record IncrementalPreparationLease(
    Guid Id, Guid SnapshotId, Guid SourceConnectionId, Guid DestinationConnectionId,
    Guid LeaseToken, int Attempt);

public sealed record IncrementalPreparationItem(
    string AssetType, string SourceId, string ChangeKind,
    string? PreviousFingerprint, string? CurrentFingerprint,
    int Revision, string ReviewCategory, string ReviewState);

public sealed record IncrementalPreparationItemPage(
    Guid PreparationId, IReadOnlyList<IncrementalPreparationItem> Items,
    string? NextAfterType, string? NextAfterId);

public sealed record IncrementalPreparationEvent(
    long Id, Guid PreparationId, string EventType, int Attempt,
    DateTimeOffset OccurredAtUtc, string? ErrorCode);

public sealed class IncrementalPreparationException(string code)
    : Exception("Incremental preparation rejected")
{
    public string Code { get; } = code;
}

public interface IIncrementalPreparationRepository
{
    Task<IncrementalPreparationView> EnqueueAsync(
        Guid snapshotId, Guid sourceConnectionId, Guid destinationConnectionId,
        Guid actorId, CancellationToken ct);
    Task<IncrementalPreparationView?> GetAsync(Guid id, CancellationToken ct);
    Task<IncrementalPreparationItemPage?> ListItemsAsync(
        Guid id, string? afterType, string? afterId, int take, CancellationToken ct);
    Task<IReadOnlyList<IncrementalPreparationEvent>> EventsAsync(Guid id, CancellationToken ct);
    Task<IncrementalPreparationLease?> ClaimAsync(
        Guid token, TimeSpan duration, CancellationToken ct,
        Guid sourceConnectionId = default);
    Task<bool> PrepareAsync(IncrementalPreparationLease lease, CancellationToken ct);
    Task<bool> FailAsync(
        IncrementalPreparationLease lease, string errorCode, bool permanent, CancellationToken ct);
}

public sealed class IncrementalPreparationService(
    IIncrementalPreparationRepository preparations,
    IStagingRepository staging,
    IConnectionRepository connections)
{
    private static readonly HashSet<string> AssetTypes = new(StringComparer.Ordinal)
    {
        "project", "repository", "ref", "identity", "group", "membership", "acl"
    };

    public async Task<IncrementalPreparationView> EnqueueAsync(
        Guid snapshotId, Guid destinationId, Guid actorId, CancellationToken ct)
    {
        if (snapshotId == Guid.Empty || destinationId == Guid.Empty || actorId == Guid.Empty)
            throw new IncrementalPreparationException("InvalidPreparationContext");
        var sync = await staging.GetSyncAsync(snapshotId, ct)
            ?? throw new KeyNotFoundException("StagingSyncNotFound");
        // Validating the committed journal prevents queueing corrupt or
        // pre-F-025 source evidence, including a misleading empty delta.
        var changes = await staging.GetChangeSummaryAsync(snapshotId, ct)
            ?? throw new KeyNotFoundException("StagingSyncNotFound");
        if (changes.SourceConnectionId != sync.SourceConnectionId ||
            changes.Total > 200_000)
            throw new IncrementalPreparationException("InvalidPreparationSource");
        if (sync.SourceConnectionId == destinationId)
            throw new IncrementalPreparationException("InvalidPreparationDestination");
        var source = await connections.GetAsync(sync.SourceConnectionId, ct)
            ?? throw new KeyNotFoundException("SourceConnectionNotFound");
        var destination = await connections.GetAsync(destinationId, ct)
            ?? throw new KeyNotFoundException("DestinationConnectionNotFound");
        if (source.Direction != ConnectionDirection.Source ||
            !ProviderKeys.IsAzureDevOpsSource(source.ProviderKey) ||
            source.ValidationCode != "Verified")
            throw new IncrementalPreparationException("UnverifiedPreparationSource");
        if (destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified")
            throw new IncrementalPreparationException("UnverifiedPreparationDestination");

        return await preparations.EnqueueAsync(
            snapshotId, sync.SourceConnectionId, destinationId, actorId, ct);
    }

    public Task<IncrementalPreparationView?> GetAsync(Guid id, CancellationToken ct) =>
        preparations.GetAsync(id, ct);

    public Task<IReadOnlyList<IncrementalPreparationEvent>> EventsAsync(
        Guid id, CancellationToken ct) => preparations.EventsAsync(id, ct);

    public Task<IncrementalPreparationItemPage?> ListItemsAsync(
        Guid id, string? afterType, string? afterId, int take, CancellationToken ct)
    {
        if (id == Guid.Empty || take is < 1 or > 100 ||
            ((afterType is null) != (afterId is null)) ||
            (afterType is not null && !AssetTypes.Contains(afterType)) ||
            (afterId?.Length ?? 0) > 1024 ||
            (afterId?.Any(char.IsControl) ?? false))
            throw new IncrementalPreparationException("InvalidPreparationPage");
        return preparations.ListItemsAsync(id, afterType, afterId, take, ct);
    }
}

// Independent of the existing source discovery worker. The worker checks
// active leases, processes only committed source journal metadata, and does
// not even resolve a GitHub HTTP adapter or credential provider.
public sealed class IncrementalPreparationProcessor(IIncrementalPreparationRepository repository,
    Microsoft.Extensions.Logging.ILogger<IncrementalPreparationProcessor>? logger = null)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExecutionDeadline = TimeSpan.FromSeconds(90);

    public async Task<bool> RunOnceAsync(CancellationToken shutdown,
        Guid sourceConnectionId = default)
    {
        var lease = await repository.ClaimAsync(
            Guid.NewGuid(), LeaseDuration, shutdown, sourceConnectionId);
        if (lease is null) return false;
        try
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            work.CancelAfter(ExecutionDeadline);
            await repository.PrepareAsync(lease, work.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            await repository.FailAsync(lease, "PreparationDeadlineExceeded", false, shutdown);
        }
        catch (IncrementalPreparationException ex)
        {
            if (ex.Code == "IncrementalLeaseLost") return true;
            var permanent = ex.Code is "IncrementalSnapshotSuperseded" or
                "IncrementalJournalUnavailable" or "IncrementalJournalMismatch" or
                "IncrementalSourceMismatch";
            await repository.FailAsync(lease, ex.Code, permanent, shutdown);
        }
        catch (StagingChangeIntegrityException)
        {
            await repository.FailAsync(lease, "IncrementalJournalMismatch", true, shutdown);
        }
        catch (StagingChangeJournalUnavailableException)
        {
            await repository.FailAsync(lease, "IncrementalJournalUnavailable", true, shutdown);
        }
        catch (Exception ex)
        {
            // No exception messages: even infrastructure errors can contain
            // provider details or connection-string secrets.
            logger?.LogError("Preparation {Id} attempt {Attempt} failed: {ExceptionType} at {Site}",
                lease.Id, lease.Attempt, ex.GetType().FullName, ex.TargetSite?.DeclaringType?.Name + "." + ex.TargetSite?.Name);
            await repository.FailAsync(lease, "IncrementalPreparationFailed", false, shutdown);
        }
        return true;
    }
}
