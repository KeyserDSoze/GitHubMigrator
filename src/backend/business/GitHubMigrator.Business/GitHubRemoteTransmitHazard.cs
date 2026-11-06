using System.Security.Cryptography;
using System.Text;

namespace GitHubMigrator.Business;

// F-055 is a pessimistic pre-transport protocol for a future, separately
// authorized sender. It cannot execute transport or grant remote write access.
public static class GitHubRemoteTransmitHazardStates
{
    public const string PreparedWriteDisabled = "PreparedWriteDisabled";
    public const string MayHaveTransmitted = "MayHaveTransmitted";
    public const string AbortedBeforeTransport = "AbortedBeforeTransport";
    public const string OperatorReviewRequired = "OperatorReviewRequired";
}

public sealed record GitHubRemoteTransmitHazardOptions(
    bool Enabled, long AllowedRepositoryId, string AllowedRepository);

public sealed class GitHubRemoteTransmitHazardException(string code)
    : Exception("Remote transmission safety fence refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubRemoteTransmitHazardStatus(
    Guid Id, Guid GrantId, long RepositoryId, Guid FenceRequestId,
    long FenceGeneration, string PacketSha256, string CandidateSetSha256,
    string BaselineSha256, string State, Guid PreparedBy,
    Guid? MarkedBy, DateTimeOffset DeadlineUtc, DateTimeOffset UpdatedAtUtc,
    bool TransportAuthorized, bool TransportInvoked, bool RemoteWritesPerformed,
    bool MayAutomaticallyRetry);

public sealed record GitHubRemoteTransmitHazardClaim(
    GitHubRemoteTransmitHazardStatus Status, Guid AbortToken);

public sealed record GitHubRemoteTransmitHazardEvent(
    long Id, Guid HazardId, string Type, Guid ActorId,
    DateTimeOffset OccurredAtUtc);

// Internal only; no HTTP endpoints, Git process, sender or worker registered.
// No method returns an execution credential or transport authorization.
public interface IGitHubRemoteTransmitHazardJournal
{
    Task<GitHubRemoteTransmitHazardClaim> PrepareAsync(
        Guid grantId, long repositoryId, Guid actorId, CancellationToken ct);
    Task<GitHubRemoteTransmitHazardStatus> MarkMayHaveTransmittedAsync(
        Guid hazardId, long repositoryId, Guid actorId, Guid abortToken,
        CancellationToken ct);
    Task<bool> AbortBeforeTransportAsync(
        Guid hazardId, long repositoryId, Guid actorId, Guid abortToken,
        CancellationToken ct);
    Task<GitHubRemoteTransmitHazardStatus?> GetAsync(
        Guid hazardId, long repositoryId, CancellationToken ct);
    Task<IReadOnlyList<GitHubRemoteTransmitHazardEvent>> EventsAsync(
        Guid hazardId, long repositoryId, CancellationToken ct);
    Task<bool> QuarantineExpiredAsync(CancellationToken ct);
}

public static class GitHubRemoteTransmitHazardIntegrity
{
    public static string AbortTokenDigest(Guid token) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "f055-remote-pretransport-abort-v1\n" + token.ToString("D"))));

    public static bool ValidSha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void RequireAllowed(
        GitHubRemoteTransmitHazardOptions options, long repositoryId)
    {
        if (!options.Enabled || repositoryId <= 0 ||
            repositoryId != options.AllowedRepositoryId ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw new GitHubRemoteTransmitHazardException(
                "RemoteTransmitHazardDisabled");
    }

    public static void RequireDependencies(
        GitHubRemoteTestGrantStatus grant,
        GitHubRemoteSandboxFenceStatus fence,
        GitHubTransferAttemptView attempt,
        GitHubRemoteTransmitHazardOptions options,
        Guid actorId, DateTimeOffset now)
    {
        RequireAllowed(options, grant.RepositoryId);
        if (actorId == Guid.Empty ||
            grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.DeadlineUtc <= now ||
            grant.FenceGeneration <= 0 ||
            !ValidSha(grant.PacketSha256) ||
            !ValidSha(grant.CandidateSetSha256) ||
            !ValidSha(grant.OriginalPolicySha256) ||
            !ValidSha(grant.BaselineSha256) ||
            grant.ReviewedBy is null ||
            grant.RequestedBy == grant.ReviewedBy ||
            grant.RequestedBy == actorId ||
            grant.ReviewedBy == actorId ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            !string.Equals(options.AllowedRepository,
                grant.Organization + "/" + grant.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            fence.RepositoryId != grant.RepositoryId ||
            fence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            fence.DeadlineUtc <= now ||
            fence.RequestId != grant.FenceRequestId ||
            fence.Generation != grant.FenceGeneration ||
            fence.PacketSha256 != grant.PacketSha256 ||
            fence.CandidateSetSha256 != grant.CandidateSetSha256 ||
            fence.PolicySha256 != grant.OriginalPolicySha256 ||
            fence.AttemptId != grant.AttemptId ||
            fence.RequestedBy == actorId || fence.ReviewedBy is null ||
            fence.ReviewedBy == actorId || fence.ReviewedBy == fence.RequestedBy ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt.Id != grant.AttemptId ||
            attempt.PreparationId != grant.PreparationId ||
            attempt.PlanId != grant.PlanId ||
            attempt.ScopeId != grant.ScopeId ||
            attempt.RepositoryId != grant.RepositoryId ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.PacketSha256 != grant.PacketSha256 ||
            attempt.CandidateSetSha256 != grant.CandidateSetSha256 ||
            attempt.PolicySha256 != grant.OriginalPolicySha256 ||
            attempt.RequestedBy == actorId || attempt.ReviewedBy is null ||
            attempt.ReviewedBy == actorId ||
            attempt.RequestedBy == attempt.ReviewedBy ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution)
            throw new GitHubRemoteTransmitHazardException(
                "RemoteTransmitHazardBarrierChanged");
    }
}
