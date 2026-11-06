using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-052 binds a future test-only write intent to F-047/F-050/F-051, but
// deliberately cannot issue a GitHub push or become an execution grant.
// F-047/F-050's SQL-enforced write barriers remain permanently closed.
public static class GitHubRemoteTestGrantStates
{
    public const string Proposed = "Proposed";
    public const string ReviewedWriteDisabled = "ReviewedWriteDisabled";
    public const string Cancelled = "Cancelled";
    public const string RecoveryRequired = "RecoveryRequired";
    public const string RecoveredNoWrites = "RecoveredNoWrites";
}

public sealed record GitHubRemoteTestGrantOptions(
    bool Enabled, long AllowedRepositoryId, string AllowedRepository);

public sealed class GitHubRemoteTestGrantException(string code)
    : Exception("GitHub remote test grant refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubRemoteTestGrantEvidence(
    GitHubRemoteAppPreflightReceipt Preflight,
    GitHubRemoteSandboxFenceStatus Fence,
    GitHubTransferAttemptView Attempt,
    Guid ActorId);

public sealed record GitHubRemoteTestGrantStatus(
    Guid Id, long RepositoryId, string Organization, string Repository,
    Guid PreparationId, Guid PlanId, Guid ScopeId, Guid AttemptId,
    Guid FenceRequestId, long FenceGeneration, string PacketSha256,
    string CandidateSetSha256, string OriginalPolicySha256,
    string BaselineSha256, string PreflightSha256,
    string State, Guid RequestedBy, Guid? ReviewedBy,
    DateTimeOffset DeadlineUtc, DateTimeOffset UpdatedAtUtc,
    bool ExecutionAuthorized, bool RemoteWritesPerformed,
    bool ReadyForExecution);

public sealed record GitHubRemoteTestGrantClaim(
    GitHubRemoteTestGrantStatus Status, Guid CancellationToken);

public sealed record GitHubRemoteTestGrantEvent(
    long Id, Guid GrantId, string Type, Guid ActorId,
    DateTimeOffset OccurredAtUtc);

public static class GitHubRemoteTestGrantIntegrity
{
    public const string RecoveryText =
        "I CONFIRM F052 HAS NO GITHUB TRANSPORT AND REQUEST MANUAL NO-WRITE RECOVERY";
    public static readonly TimeSpan MaxPreflightAge = TimeSpan.FromSeconds(20);

    public static string TokenDigest(Guid token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "f052-test-grant-cancel-token-v1\n" + token.ToString("D"))));

    public static void Validate(GitHubRemoteTestGrantEvidence evidence,
        GitHubRemoteTestGrantOptions options, DateTimeOffset now)
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository) ||
            evidence is null || evidence.Preflight is null ||
            evidence.Fence is null || evidence.Attempt is null ||
            evidence.ActorId == Guid.Empty)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantDisabled");
        var preflight = evidence.Preflight;
        var fence = evidence.Fence;
        var attempt = evidence.Attempt;
        if (preflight.State !=
                GitHubRemoteAppPreflightIntegrity.VerifiedState ||
            !GitSourceBundleStorageCheck.ValidSha(preflight.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(preflight.PacketSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(preflight.CandidateSetSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(preflight.OriginalPolicySha256) ||
            !GitSourceBundleStorageCheck.ValidSha(preflight.CurrentPolicySha256) ||
            !GitSourceBundleStorageCheck.ValidSha(preflight.ObservedBaselineSha256) ||
            preflight.ExpectedBaselineSha256 !=
                preflight.ObservedBaselineSha256 ||
            preflight.MatchingBaselineRefs < 0 ||
            preflight.AbsentCreateCandidates is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            preflight.RepositoryId != options.AllowedRepositoryId ||
            preflight.AbsentCreateCandidates is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            preflight.ObservedAtUtc < now.Subtract(MaxPreflightAge) ||
            preflight.ObservedAtUtc > now.AddSeconds(5) ||
            preflight.InstallationTokenExpiresAtUtc <= now.AddMinutes(2) ||
            preflight.RemoteWritesPerformed || preflight.ExecutionAuthorized ||
            preflight.ReadyForExecution ||
            fence.RepositoryId != options.AllowedRepositoryId ||
            !string.Equals(options.AllowedRepository,
                fence.Organization + "/" + fence.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            fence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            fence.DeadlineUtc <= now ||
            fence.RequestId != preflight.RequestId ||
            fence.Generation != preflight.Generation ||
            fence.PacketSha256 != preflight.PacketSha256 ||
            fence.CandidateSetSha256 != preflight.CandidateSetSha256 ||
            fence.PolicySha256 != preflight.OriginalPolicySha256 ||
            fence.RequestedBy == evidence.ActorId ||
            fence.ReviewedBy is null ||
            fence.ReviewedBy == evidence.ActorId ||
            fence.RequestedBy == fence.ReviewedBy ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt.Id != fence.AttemptId ||
            attempt.PreparationId != fence.PreparationId ||
            attempt.PlanId != fence.PlanId ||
            attempt.ScopeId != fence.ScopeId ||
            attempt.RepositoryId != fence.RepositoryId ||
            !string.Equals(attempt.Organization + "/" + attempt.Repository,
                fence.Organization + "/" + fence.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            attempt.PacketSha256 != preflight.PacketSha256 ||
            attempt.CandidateSetSha256 != preflight.CandidateSetSha256 ||
            attempt.PolicySha256 != preflight.OriginalPolicySha256 ||
            attempt.CandidateCount != preflight.AbsentCreateCandidates ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.RequestedBy == evidence.ActorId ||
            attempt.ReviewedBy is null ||
            attempt.ReviewedBy == evidence.ActorId ||
            attempt.RequestedBy == attempt.ReviewedBy ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution)
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantEvidenceInvalid");
    }
}

public interface IGitHubRemoteTestGrantStore
{
    Task<GitHubRemoteTestGrantClaim> ProposeAsync(
        GitHubRemoteTestGrantEvidence evidence, CancellationToken ct);
    Task<GitHubRemoteTestGrantStatus> ReviewAsync(
        Guid grantId, GitHubRemoteTestGrantEvidence evidence,
        CancellationToken ct);
    Task<bool> CancelAsync(Guid grantId, long repositoryId, Guid actorId,
        Guid cancellationToken, CancellationToken ct);
    Task<bool> RecoverAsync(Guid grantId, long repositoryId,
        Guid actorId, string acknowledgement, CancellationToken ct);
    Task<GitHubRemoteTestGrantStatus?> GetAsync(
        Guid grantId, long repositoryId, CancellationToken ct);
    Task<IReadOnlyList<GitHubRemoteTestGrantEvent>> EventsAsync(
        Guid grantId, long repositoryId, CancellationToken ct);
    Task<bool> QuarantineNextExpiredAsync(CancellationToken ct);
}

// No transport interface is accepted; the only reachable result is
// ReviewedWriteDisabled, never a GitHub execution grant.
public sealed class GitHubRemoteTestGrantService(
    GitHubRemoteAppPreflightService preflight,
    IGitHubRemoteSandboxFenceStore fences,
    IGitHubTransferAttemptStore attempts,
    IGitHubRemoteTestGrantStore store,
    GitHubRemoteTestGrantOptions options,
    TimeProvider clock)
{
    public async Task<GitHubRemoteTestGrantClaim> ProposeAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid fenceRequestId, long generation, string packetSha256,
        Guid actorId, CancellationToken ct)
    {
        var evidence = await FreshEvidenceAsync(preparationId, planId, scopeId,
            attemptId, fenceRequestId, generation, packetSha256, actorId, ct);
        return await store.ProposeAsync(evidence, ct);
    }

    public async Task<GitHubRemoteTestGrantStatus> ReviewAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid fenceRequestId, long generation, Guid grantId,
        string packetSha256, Guid actorId, CancellationToken ct)
    {
        var evidence = await FreshEvidenceAsync(preparationId, planId, scopeId,
            attemptId, fenceRequestId, generation, packetSha256, actorId, ct);
        return await store.ReviewAsync(grantId, evidence, ct);
    }

    public async Task<bool> CancelAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid grantId, Guid token, Guid actorId, CancellationToken ct)
    {
        var repo = await ScopedRepositoryAsync(preparationId, planId,
            scopeId, attemptId, ct);
        await RequireScopedGrantAsync(grantId, repo, preparationId,
            planId, scopeId, attemptId, ct);
        return await store.CancelAsync(grantId, repo, actorId, token, ct);
    }

    public async Task<bool> RecoverAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid grantId, Guid actorId, string acknowledgement,
        CancellationToken ct)
    {
        var repo = await ScopedRepositoryAsync(preparationId, planId,
            scopeId, attemptId, ct);
        await RequireScopedGrantAsync(grantId, repo, preparationId,
            planId, scopeId, attemptId, ct);
        return await store.RecoverAsync(grantId, repo, actorId,
            acknowledgement, ct);
    }

    public async Task<GitHubRemoteTestGrantStatus?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid grantId, CancellationToken ct)
    {
        var repo = await ScopedRepositoryAsync(preparationId, planId,
            scopeId, attemptId, ct);
        return await RequireScopedGrantAsync(grantId, repo, preparationId,
            planId, scopeId, attemptId, ct);
    }

    public async Task<IReadOnlyList<GitHubRemoteTestGrantEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid grantId, CancellationToken ct)
    {
        var repo = await ScopedRepositoryAsync(preparationId, planId,
            scopeId, attemptId, ct);
        await RequireScopedGrantAsync(grantId, repo, preparationId,
            planId, scopeId, attemptId, ct);
        return await store.EventsAsync(grantId, repo, ct);
    }

    private async Task<GitHubRemoteTestGrantStatus?> RequireScopedGrantAsync(
        Guid grantId, long repositoryId, Guid preparationId, Guid planId,
        Guid scopeId, Guid attemptId, CancellationToken ct)
    {
        var grant = await store.GetAsync(grantId, repositoryId, ct);
        if (grant is not null &&
            (grant.PreparationId != preparationId ||
             grant.PlanId != planId || grant.ScopeId != scopeId ||
             grant.AttemptId != attemptId))
            throw new KeyNotFoundException("RemoteTestGrantNotFound");
        return grant;
    }

    private async Task<long> ScopedRepositoryAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        Guid attemptId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantDisabled");
        var attempt = await attempts.GetAsync(preparationId, planId,
            scopeId, attemptId, ct) ??
            throw new KeyNotFoundException("RemoteTestAttemptNotFound");
        if (attempt.RepositoryId != options.AllowedRepositoryId ||
            !string.Equals(options.AllowedRepository,
                attempt.Organization + "/" + attempt.Repository,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubRemoteTestGrantException(
                "RemoteTestGrantWrongRepository");
        return attempt.RepositoryId;
    }

    private async Task<GitHubRemoteTestGrantEvidence> FreshEvidenceAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid fenceRequestId, long generation, string packetSha256,
        Guid actorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw new GitHubRemoteTestGrantException("RemoteTestGrantDisabled");
        if (attemptId == Guid.Empty || actorId == Guid.Empty ||
            fenceRequestId == Guid.Empty || generation <= 0 ||
            !GitSourceBundleStorageCheck.ValidSha(packetSha256))
            throw new GitHubRemoteTestGrantException("RemoteTestGrantRequestInvalid");

        // The F-051 service physically re-verifies the F-044 bundle, obtains
        // a newly minted scoped App token and performs GET-only ref/rule checks.
        var snapshot = await preflight.InspectAsync(
            preparationId, planId, scopeId,
            fenceRequestId, generation, packetSha256, ct);
        var fence = await fences.GetAsync(snapshot.RepositoryId, ct) ??
            throw new KeyNotFoundException("RemoteTestFenceNotFound");
        var attempt = await attempts.GetAsync(preparationId, planId,
            scopeId, attemptId, ct) ??
            throw new KeyNotFoundException("RemoteTestAttemptNotFound");
        var evidence = new GitHubRemoteTestGrantEvidence(
            snapshot, fence, attempt, actorId);
        GitHubRemoteTestGrantIntegrity.Validate(evidence, options, clock.GetUtcNow());
        return evidence;
    }
}

// Pure independent post-event classifier, used for a future sender's
// reconciliation path. Never returns "may retry", nor any write authority.
public sealed record GitHubRemotePostSendInspection(
    string State, int ExactCreateRefs, int TotalRefs,
    string RefSetSha256, string EvidenceSha256,
    bool MayAutomaticallyRetry, bool RemoteWritesAuthorized);

public static class GitHubRemotePostSendIntegrity
{
    public static GitHubRemotePostSendInspection Inspect(
        IReadOnlyList<GitMirrorRef> baseline,
        IReadOnlyList<GitMirrorRef> creates,
        GitHubRefObservation actual)
    {
        if (baseline is null || creates is null || actual is null ||
            actual.Refs is null || actual.ObservedAtUtc == default ||
            creates.Count is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates)
            throw new GitHubRemoteTestGrantException("RemotePostSendInputInvalid");
        IReadOnlyList<GitMirrorRef> source;
        IReadOnlyList<GitMirrorRef> created;
        try
        {
            source = GitMirrorRefReconciler.CanonicalizeObservedRefs(baseline);
            created = GitMirrorRefReconciler.CanonicalizeObservedRefs(creates);
            if (!source.SequenceEqual(baseline) ||
                !created.SequenceEqual(creates) ||
                source.Any(x => created.Any(y => x.Name == y.Name)))
                throw new GitHubRemoteTestGrantException(
                    "RemotePostSendInputInvalid");
        }
        catch (GitMirrorRefReconciliationException)
        { throw new GitHubRemoteTestGrantException("RemotePostSendInputInvalid"); }

        // F-042 fingerprint includes annotated tag object type, not a peeled
        // commit OID. The observed snapshot must be provider-sealed.
        var sealedSnapshot = GitHubRefObservationIntegrity.Seal(
            actual.Organization, actual.Repository, actual.ObservedAtUtc,
            actual.Refs);
        if (sealedSnapshot.EvidenceSha256 != actual.EvidenceSha256 ||
            !sealedSnapshot.Refs.SequenceEqual(actual.Refs))
            throw new GitHubRemoteTestGrantException(
                "RemotePostSendObservationInvalid");

        var seen = actual.AsMirrorRefs();
        var expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(
            source.Concat(created).ToArray());
        var matching = created.Count(x => seen.Contains(x));
        var exact = seen.SequenceEqual(expected);
        var state = exact ? "ExactRemoteRefsObserved" :
            seen.SequenceEqual(source) ? "NoCreatedRefsObserved" :
            "RemoteDivergenceRequiresOperator";
        var fingerprint = GitHubRemoteRefFenceIntegrity.Fingerprint(actual.Refs);
        var proof = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object[] {
                "f052-remote-postsend-inspection-v1",
                state, fingerprint, actual.EvidenceSha256,
                source.Count, created.Count, matching, seen.Count,
                actual.ObservedAtUtc.ToUnixTimeMilliseconds(), false, false
            }))));
        return new(state, matching, seen.Count, fingerprint, proof,
            false, false);
    }
}
