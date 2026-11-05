namespace GitHubMigrator.Business;

// F-027: authorization of a read-only *planning review*, never authorization
// to execute a destination operation. No writer consumes this contract.
public static class IncrementalReviewStates
{
    public const string DryRunReady = "DryRunReady";
    public const string ApprovedForPlanning = "ApprovedForPlanning";
    public const string Rejected = "Rejected";
    public const string Revoked = "Revoked";
}

public sealed record IncrementalReviewView(
    Guid PreparationId, Guid SnapshotId, Guid SourceConnectionId,
    Guid DestinationConnectionId, string State,
    string EvidenceSha256, string DryRunSha256,
    int ItemCount, int SensitiveItemCount, int RemovalItemCount,
    DateTimeOffset DryRunAtUtc, Guid? ReviewerId,
    DateTimeOffset? ReviewedAtUtc, string? ReasonCode,
    bool SensitiveAcknowledged, bool IsCurrent);

public sealed record IncrementalReviewEventView(
    long Id, Guid PreparationId, string EventType, Guid ActorId,
    DateTimeOffset OccurredAtUtc, string DryRunSha256, string? ReasonCode);

public sealed class IncrementalReviewException(string code)
    : Exception("Incremental review refused")
{
    public string Code { get; } = code;
}

public interface IIncrementalReviewRepository
{
    Task<IncrementalReviewView?> GetAsync(Guid preparationId, CancellationToken ct);
    Task<IncrementalReviewView> DryRunAsync(
        Guid preparationId, Guid actorId, CancellationToken ct);
    Task<IncrementalReviewView> DecideAsync(
        Guid preparationId, Guid actorId, string decision,
        string expectedDryRunSha256, bool acknowledgeSensitive,
        string? reasonCode, CancellationToken ct);
    Task<IncrementalReviewView> RevokeAsync(
        Guid preparationId, Guid actorId, string expectedDryRunSha256,
        string reasonCode, CancellationToken ct);
    Task<IReadOnlyList<IncrementalReviewEventView>> EventsAsync(
        Guid preparationId, CancellationToken ct);
}

public sealed class IncrementalReviewService(IIncrementalReviewRepository repository)
{
    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeReason(string? code) =>
        code is { Length: >= 1 and <= 48 } &&
        code.All(char.IsAsciiLetterOrDigit);

    public Task<IncrementalReviewView?> GetAsync(
        Guid preparationId, CancellationToken ct) =>
        repository.GetAsync(preparationId, ct);

    public Task<IReadOnlyList<IncrementalReviewEventView>> EventsAsync(
        Guid preparationId, CancellationToken ct) =>
        repository.EventsAsync(preparationId, ct);

    public Task<IncrementalReviewView> DryRunAsync(
        Guid preparationId, Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty)
            throw new IncrementalReviewException("InvalidReviewContext");
        return repository.DryRunAsync(preparationId, actorId, ct);
    }

    public Task<IncrementalReviewView> DecideAsync(
        Guid preparationId, Guid actorId, string decision,
        string expectedDryRunSha256, bool acknowledgeSensitive,
        string? reasonCode, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty ||
            !IsSha256(expectedDryRunSha256) ||
            decision is not ("ApproveForPlanning" or "Reject") ||
            (decision == "Reject" && !IsSafeReason(reasonCode)) ||
            (reasonCode is not null && !IsSafeReason(reasonCode)))
            throw new IncrementalReviewException("InvalidReviewDecision");
        return repository.DecideAsync(preparationId, actorId, decision,
            expectedDryRunSha256, acknowledgeSensitive, reasonCode, ct);
    }

    public Task<IncrementalReviewView> RevokeAsync(
        Guid preparationId, Guid actorId, string expectedDryRunSha256,
        string reasonCode, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || actorId == Guid.Empty ||
            !IsSha256(expectedDryRunSha256) || !IsSafeReason(reasonCode))
            throw new IncrementalReviewException("InvalidReviewDecision");
        return repository.RevokeAsync(preparationId, actorId,
            expectedDryRunSha256, reasonCode, ct);
    }
}
