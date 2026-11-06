using System.Security.Cryptography;
using System.Text;

namespace GitHubMigrator.Business;

// F-050: a cross-replica, globally keyed, PostgreSQL *future-write* fence.
// All states permanently forbid execution; no GitHub transport is injected.
// This is NOT the 20-second F-045 read-only preview lease.
public static class GitHubRemoteSandboxFenceStates
{
    public const string Proposed = "Proposed";
    public const string ReviewedWriteDisabled = "ReviewedWriteDisabled";
    public const string Released = "Released";
    public const string RecoveryRequired = "RecoveryRequired";
    public const string RecoveredNoWrites = "RecoveredNoWrites";
}

public sealed record GitHubRemoteSandboxFenceOptions(
    bool Enabled, long AllowedRepositoryId, string AllowedRepository);

public sealed record GitHubRemoteSandboxFenceClaim(
    Guid RequestId, long RepositoryId, long Generation, Guid FenceToken,
    string State, DateTimeOffset DeadlineUtc,
    bool ExecutionAuthorized, bool RemoteWritesPerformed, bool ReadyForExecution);

public sealed record GitHubRemoteSandboxFenceStatus(
    Guid RequestId, long RepositoryId, string Organization, string Repository,
    Guid PreparationId, Guid PlanId, Guid ScopeId, Guid AttemptId,
    string PacketSha256, string CandidateSetSha256, string PolicySha256,
    long Generation, string State, Guid RequestedBy, Guid? ReviewedBy,
    DateTimeOffset DeadlineUtc, DateTimeOffset UpdatedAtUtc,
    bool ExecutionAuthorized, bool RemoteWritesPerformed, bool ReadyForExecution);

public sealed record GitHubRemoteSandboxFenceEvent(
    long Id, long RepositoryId, Guid RequestId, long Generation,
    string Type, Guid ActorId, DateTimeOffset OccurredAtUtc);

public sealed record GitHubRemoteSandboxFenceInput(
    GitHubCreateOnlyPacket Packet, GitHubTransferAttemptView Attempt,
    Guid ActorId);

public sealed class GitHubRemoteSandboxFenceException(string code)
    : Exception("Remote sandbox write fence refused")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteSandboxFenceIntegrity
{
    public static readonly TimeSpan ReviewWindow = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan ReviewedWindow = TimeSpan.FromSeconds(20);
    public const string RecoveryText =
        "I CONFIRM F050 HAS NO REMOTE TRANSPORT AND REQUEST MANUAL FENCE RECOVERY";

    public static string TokenDigest(Guid token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "f050-write-fence-token-v1\n" + token.ToString("D"))));

    public static void Verify(GitHubRemoteSandboxFenceInput input,
        GitHubRemoteSandboxFenceOptions options)
    {
        if (input is null || input.Packet is null || input.Attempt is null ||
            !options.Enabled || options.AllowedRepositoryId <= 0 ||
            input.ActorId == Guid.Empty)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceDisabledOrInvalid");
        var packet = input.Packet;
        var attempt = input.Attempt;
        GitHubTargetPreviewLeaseIntegrity.VerifyPacket(packet);
        if (packet.RepositoryId != options.AllowedRepositoryId ||
            !string.Equals(packet.Organization + "/" + packet.Repository,
                options.AllowedRepository, StringComparison.OrdinalIgnoreCase) ||
            attempt.Id == Guid.Empty ||
            attempt.PreparationId != packet.PreparationId ||
            attempt.PlanId != packet.PlanId ||
            attempt.ScopeId != packet.ScopeId ||
            attempt.RepositoryId != packet.RepositoryId ||
            attempt.PacketSha256 != packet.EvidenceSha256 ||
            attempt.CandidateSetSha256 != packet.CandidateSetSha256 ||
            attempt.CandidateCount != packet.CreateCandidates ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.ReviewedBy is null ||
            attempt.ReviewedBy == attempt.RequestedBy ||
            attempt.TransportAttempted ||
            attempt.ExecutionAuthorized || attempt.RemoteWritesPerformed ||
            attempt.ReadyForExecution ||
            packet.CreateCandidates is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            !GitSourceBundleStorageCheck.ValidSha(attempt.PolicySha256))
            throw new GitHubRemoteSandboxFenceException("RemoteFenceEvidenceInvalid");
    }
}

public interface IGitHubRemoteSandboxFenceStore
{
    Task<GitHubRemoteSandboxFenceClaim> ProposeAsync(
        GitHubRemoteSandboxFenceInput input, CancellationToken ct);
    Task<GitHubRemoteSandboxFenceStatus> ReviewAsync(
        GitHubRemoteSandboxFenceInput input, Guid requestId,
        long generation, CancellationToken ct);
    Task<bool> ReleaseAsync(
        long repositoryId, Guid requestId, Guid token,
        long generation, Guid actorId, CancellationToken ct);
    Task<GitHubRemoteSandboxFenceStatus?> GetAsync(
        long repositoryId, CancellationToken ct);
    Task<IReadOnlyList<GitHubRemoteSandboxFenceEvent>> EventsAsync(
        long repositoryId, CancellationToken ct);
    Task<bool> QuarantineNextExpiredAsync(CancellationToken ct);
    Task<bool> RecoverAsync(
        GitHubRemoteSandboxFenceInput freshInput,
        Guid expectedOldRequestId, long expectedGeneration,
        string acknowledgementText, CancellationToken ct);
}

public sealed class GitHubRemoteSandboxFenceService(
    GitHubCreateOnlyPacketService packets,
    IGitHubTransferAttemptStore attempts,
    IGitHubAppWriteScopeStore scopes,
    IGitHubRemoteSandboxFenceStore store,
    GitHubRemoteSandboxFenceOptions options)
{
    public async Task<GitHubRemoteSandboxFenceClaim> ProposeAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        string expectedPacketSha256, Guid actorId, CancellationToken ct)
    {
        var input = await FreshInputAsync(preparationId, planId,
            scopeId, attemptId, expectedPacketSha256, actorId, ct);
        return await store.ProposeAsync(input, ct);
    }

    public async Task<GitHubRemoteSandboxFenceStatus> ReviewAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid requestId, long generation, string expectedPacketSha256,
        Guid actorId, CancellationToken ct)
    {
        var input = await FreshInputAsync(preparationId, planId,
            scopeId, attemptId, expectedPacketSha256, actorId, ct);
        return await store.ReviewAsync(input, requestId, generation, ct);
    }

    public async Task<bool> ReleaseAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid requestId, long generation, Guid fenceToken,
        Guid actorId, CancellationToken ct)
    {
        EnsureEnabled();
        var attempt = await attempts.GetAsync(preparationId, planId,
            scopeId, attemptId, ct)
            ?? throw new KeyNotFoundException("TransferAttemptNotFound");
        // Release remains possible AFTER the 30s F-042 remote ref fence
        // expires; does not imply GitHub refs still match.
        if (attempt.RepositoryId != options.AllowedRepositoryId)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceWrongRepository");
        return await store.ReleaseAsync(attempt.RepositoryId, requestId,
            fenceToken, generation, actorId, ct);
    }

    public async Task<GitHubRemoteSandboxFenceStatus?> GetAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
    {
        EnsureEnabled();
        var scope = await scopes.GetAsync(preparationId, planId, scopeId, ct)
            ?? throw new KeyNotFoundException("AppScopeNotFound");
        if (scope.RepositoryId != options.AllowedRepositoryId)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceWrongRepository");
        return await store.GetAsync(scope.RepositoryId, ct);
    }

    public async Task<IReadOnlyList<GitHubRemoteSandboxFenceEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
    {
        EnsureEnabled();
        var scope = await scopes.GetAsync(preparationId, planId, scopeId, ct)
            ?? throw new KeyNotFoundException("AppScopeNotFound");
        if (scope.RepositoryId != options.AllowedRepositoryId)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceWrongRepository");
        return await store.EventsAsync(scope.RepositoryId, ct);
    }

    public async Task<bool> RecoverAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        Guid oldRequestId, long generation, string expectedPacketSha256,
        string acknowledgementText, Guid actorId, CancellationToken ct)
    {
        if (acknowledgementText != GitHubRemoteSandboxFenceIntegrity.RecoveryText)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceRecoveryAttestationRequired");
        var fresh = await FreshInputAsync(preparationId, planId,
            scopeId, attemptId, expectedPacketSha256, actorId, ct);
        return await store.RecoverAsync(fresh, oldRequestId,
            generation, acknowledgementText, ct);
    }

    private async Task<GitHubRemoteSandboxFenceInput> FreshInputAsync(
        Guid preparationId, Guid planId, Guid scopeId, Guid attemptId,
        string expectedPacketSha256, Guid actorId, CancellationToken ct)
    {
        EnsureEnabled();
        if (!GitSourceBundleStorageCheck.ValidSha(expectedPacketSha256) ||
            attemptId == Guid.Empty || actorId == Guid.Empty)
            throw new GitHubRemoteSandboxFenceException("RemoteFenceRequestInvalid");
        var packet = await packets.PreviewAsync(
            preparationId, planId, scopeId, ct);
        if (packet.EvidenceSha256 != expectedPacketSha256)
            throw new GitHubRemoteSandboxFenceException("RemoteFencePacketChanged");
        var attempt = await attempts.GetAsync(preparationId,
            planId, scopeId, attemptId, ct)
            ?? throw new KeyNotFoundException("TransferAttemptNotFound");
        var input = new GitHubRemoteSandboxFenceInput(packet, attempt, actorId);
        GitHubRemoteSandboxFenceIntegrity.Verify(input, options);
        return input;
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled || options.AllowedRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw new GitHubRemoteSandboxFenceException("RemoteFenceDisabledOrInvalid");
    }
}
