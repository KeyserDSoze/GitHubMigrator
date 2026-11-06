namespace GitHubMigrator.Business;

// F-047 is a durable, OFF-BY-DEFAULT transfer *preparation* journal.
// No code here can invoke Git transport, grant execution, mint credentials,
// or persist a remote success. All records are permanently non-executable.
public static class GitHubTransferAttemptStates
{
    public const string AwaitingIndependentReview = "AwaitingIndependentReview";
    public const string Reviewed = "Reviewed";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string BlockedAtWriteBarrier = "BlockedAtWriteBarrier";
}

public sealed record GitHubTransferAttemptDraft(
    Guid PreparationId, Guid PlanId, Guid ScopeId, long RepositoryId,
    string Organization, string Repository, string PacketSha256,
    string PolicySha256, string CandidateSetSha256, int CandidateCount,
    DateTimeOffset PolicyObservedAtUtc, Guid LeaseToken, long LeaseGeneration,
    Guid RequestedBy);

public sealed record GitHubTransferAttemptView(
    Guid Id, Guid PreparationId, Guid PlanId, Guid ScopeId,
    long RepositoryId, string Organization, string Repository,
    string PacketSha256, string PolicySha256, string CandidateSetSha256,
    int CandidateCount, long LeaseGeneration, string State,
    Guid RequestedBy, Guid? ReviewedBy,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ReviewedAtUtc,
    DateTimeOffset? BlockedAtUtc, DateTimeOffset? ExpiredAtUtc,
    bool TransportAttempted,
    bool ExecutionAuthorized, bool RemoteWritesPerformed,
    bool ReadyForExecution);

public sealed record GitHubTransferAttemptEvent(
    long Id, Guid AttemptId, string Type, Guid ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed class GitHubTransferAttemptException(string code)
    : Exception("GitHub transfer attempt preparation rejected")
{
    public string Code { get; } = code;
}

public static class GitHubTransferAttemptIntegrity
{
    public const int MaxCandidates = GitHubCandidatePolicyIntegrity.MaxOnlineCandidates;
    public static readonly TimeSpan MaxReviewAge = TimeSpan.FromSeconds(30);

    public static GitHubTransferAttemptDraft Build(
        GitHubCreateOnlyPacket packet, GitHubCandidatePolicyReview policy,
        Guid leaseToken, long leaseGeneration, Guid actorId,
        DateTimeOffset now)
    {
        if (packet is null || policy is null ||
            actorId == Guid.Empty || leaseToken == Guid.Empty ||
            leaseGeneration <= 0 || packet.RepositoryId <= 0 ||
            packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            packet.ExecutionAuthorized || packet.RemoteWritesPerformed ||
            packet.ReadyForExecution || packet.WriteCapabilityVerified ||
            policy.ExecutionAuthorized || policy.RemoteWritesPerformed ||
            policy.ReadyForExecution || policy.WriteCapabilityVerified ||
            policy.ScopeId != packet.ScopeId ||
            policy.RepositoryId != packet.RepositoryId ||
            policy.PacketSha256 != packet.EvidenceSha256 ||
            policy.Status != "AdvisoryNoRulesReported" ||
            policy.NoActiveRulesReported != packet.CreateCandidates ||
            policy.RulesRequireReview != 0 || policy.PolicyUnverified != 0 ||
            policy.Candidates != packet.CreateCandidates ||
            policy.Items is null || policy.Items.Count != packet.CreateCandidates ||
            policy.Items.Any(x =>
                x is null ||
                x.Status != GitHubCandidatePolicyStates.NoActiveRulesReported ||
                x.ActiveRules != 0 || x.ProviderStatus != "Observed") ||
            packet.CreateCandidates is < 1 or > MaxCandidates ||
            !GitSourceBundleStorageCheck.ValidSha(packet.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(packet.CandidateSetSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(policy.EvidenceSha256) ||
            policy.ObservedAtUtc > now.AddSeconds(5) ||
            now >= policy.ObservedAtUtc.Add(MaxReviewAge) ||
            string.IsNullOrWhiteSpace(packet.Organization) ||
            string.IsNullOrWhiteSpace(packet.Repository))
            throw new GitHubTransferAttemptException("TransferPreparationEvidenceInvalid");

        return new(packet.PreparationId, packet.PlanId, packet.ScopeId,
            packet.RepositoryId, packet.Organization, packet.Repository,
            packet.EvidenceSha256, policy.EvidenceSha256,
            packet.CandidateSetSha256, packet.CreateCandidates,
            policy.ObservedAtUtc, leaseToken, leaseGeneration, actorId);
    }
}

public interface IGitHubTransferAttemptStore
{
    Task<GitHubTransferAttemptView> PrepareAsync(
        GitHubTransferAttemptDraft draft, CancellationToken ct);
    Task<GitHubTransferAttemptView> DecideAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid actorId, string decision, CancellationToken ct);
    Task<GitHubTransferAttemptView?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        CancellationToken ct);
    Task<IReadOnlyList<GitHubTransferAttemptEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        CancellationToken ct);
    // Only the local no-write worker can invoke this method. An actual
    // remote executor needs a distinct, later authorization mechanism.
    Task<GitHubTransferAttemptView?> BlockNextAtWriteBarrierAsync(
        CancellationToken ct);
    Task<bool> ExpireNextStaleAsync(CancellationToken ct);
}

public sealed class GitHubTransferAttemptService(
    GitHubCreateOnlyPacketService packets,
    GitHubCandidatePolicyService policy,
    IGitHubTransferAttemptStore store,
    GitHubTransferAttemptOptions options,
    TimeProvider clock)
{
    public async Task<GitHubTransferAttemptView> PrepareAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        string expectedPacketSha256, string expectedPolicySha256,
        Guid leaseToken, long leaseGeneration,
        Guid actorId, CancellationToken ct)
    {
        EnsureEnabled();
        if (!GitSourceBundleStorageCheck.ValidSha(expectedPacketSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(expectedPolicySha256))
            throw new GitHubTransferAttemptException("TransferPreparationRequestInvalid");

        // Policy reader internally reconstructs F-044 both before and after
        // its GET-only GitHub calls. There is no caller-authored policy input.
        var observed = await policy.ReviewAsync(
            preparationId, planId, scopeId, expectedPacketSha256, ct);
        if (observed.EvidenceSha256 != expectedPolicySha256)
            throw new GitHubTransferAttemptException("TransferPreparationPolicyChanged");
        var packet = await packets.PreviewAsync(
            preparationId, planId, scopeId, ct);
        var draft = GitHubTransferAttemptIntegrity.Build(
            packet, observed, leaseToken, leaseGeneration, actorId,
            clock.GetUtcNow());
        return await store.PrepareAsync(draft, ct);
    }

    public Task<GitHubTransferAttemptView> DecideAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid actorId, string decision, CancellationToken ct)
    {
        EnsureEnabled();
        if (actorId == Guid.Empty || decision is not ("acknowledge-dry-run" or "reject"))
            throw new GitHubTransferAttemptException("TransferPreparationDecisionInvalid");
        return store.DecideAsync(preparationId, planId, scopeId,
            attemptId, actorId, decision, ct);
    }

    public Task<GitHubTransferAttemptView?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        CancellationToken ct)
    {
        EnsureEnabled();
        return store.GetAsync(preparationId, planId, scopeId, attemptId, ct);
    }

    public Task<IReadOnlyList<GitHubTransferAttemptEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        CancellationToken ct)
    {
        EnsureEnabled();
        return store.EventsAsync(preparationId, planId, scopeId, attemptId, ct);
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled)
            throw new GitHubTransferAttemptException("TransferPreparationDisabled");
    }
}

public sealed record GitHubTransferAttemptOptions(bool Enabled);
