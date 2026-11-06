using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-051 is a remote GitHub App *preflight* adapter. It can only mint a
// one-repository installation token and issue GET requests. Neither this
// DTO nor any API can authorize a push or expose a bearer credential.
public sealed record GitHubRemoteAppPreflightObservation(
    long InstallationId, long RepositoryId,
    GitHubRefObservation TargetRefs,
    GitHubCandidatePolicyObservation CandidatePolicies,
    DateTimeOffset TokenExpiresAtUtc);

public sealed record GitHubRemoteAppPreflightReceipt(
    Guid RequestId, long Generation, long RepositoryId,
    string PacketSha256, string CandidateSetSha256,
    string OriginalPolicySha256, string CurrentPolicySha256,
    string ExpectedBaselineSha256, string ObservedBaselineSha256,
    int MatchingBaselineRefs, int AbsentCreateCandidates,
    DateTimeOffset ObservedAtUtc, DateTimeOffset InstallationTokenExpiresAtUtc,
    string EvidenceSha256, string State,
    bool RemoteWritesPerformed, bool ExecutionAuthorized,
    bool ReadyForExecution);

public sealed class GitHubRemoteAppPreflightException(string code)
    : Exception("GitHub App remote preflight refused")
{
    public string Code { get; } = code;
}

public interface IGitHubRemoteAppPreflightReader
{
    Task<GitHubRemoteAppPreflightObservation> InspectAsync(
        long appId, long installationId, string signingKeyPem,
        GitHubCreateOnlyCandidatePacket packet, CancellationToken ct);
}

public static class GitHubRemoteAppPreflightIntegrity
{
    public const string VerifiedState = "RemoteReadOnlyAppPreflightVerified";
    public const string WriteBlockedState = "RemotePreflightWriteBlocked";

    public static GitHubRemoteAppPreflightReceipt Seal(
        GitHubCreateOnlyCandidatePacket source,
        GitHubRemoteSandboxFenceStatus writeFence,
        GitHubRemoteRefFenceView previousFence,
        GitHubAppWriteScopeView scope,
        GitHubRemoteAppPreflightObservation current,
        DateTimeOffset now)
    {
        if (source is null || source.Packet is null || source.Candidates is null ||
            writeFence is null || previousFence is null || scope is null ||
            current is null || current.TargetRefs is null ||
            current.CandidatePolicies is null)
            throw new GitHubRemoteAppPreflightException("RemotePreflightEvidenceMissing");
        var packet = source.Packet;
        if (packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            packet.WriteCapabilityVerified || packet.ExecutionAuthorized ||
            packet.RemoteWritesPerformed || packet.ReadyForExecution ||
            writeFence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            writeFence.DeadlineUtc <= now || writeFence.RequestId == Guid.Empty ||
            writeFence.Generation <= 0 || writeFence.RepositoryId != packet.RepositoryId ||
            writeFence.PreparationId != packet.PreparationId ||
            writeFence.PlanId != packet.PlanId || writeFence.ScopeId != packet.ScopeId ||
            writeFence.PacketSha256 != packet.EvidenceSha256 ||
            writeFence.CandidateSetSha256 != packet.CandidateSetSha256 ||
            writeFence.ReviewedBy is null ||
            writeFence.RequestedBy == writeFence.ReviewedBy ||
            writeFence.ExecutionAuthorized || writeFence.RemoteWritesPerformed ||
            writeFence.ReadyForExecution ||
            !scope.IsCurrent || scope.Id != packet.ScopeId ||
            scope.RepositoryId != packet.RepositoryId ||
            scope.InstallationId <= 0 ||
            scope.FenceId != packet.FenceId ||
            scope.Status != GitHubAppWriteScopeIntegrity.Issued ||
            !scope.ScopedContentsWriteReported ||
            scope.WriteCapabilityVerified || scope.ExecutionAuthorized ||
            scope.RemoteWritesPerformed || scope.ReadyForExecution ||
            scope.EvidenceSha256 != packet.ScopeEvidenceSha256 ||
            previousFence.Id != packet.FenceId ||
            previousFence.EvidenceSha256 != packet.FenceEvidenceSha256 ||
            previousFence.Status != "RefSetUnchanged" ||
            current.InstallationId != scope.InstallationId ||
            current.RepositoryId != packet.RepositoryId ||
            current.TokenExpiresAtUtc <= now.AddMinutes(2) ||
            current.TokenExpiresAtUtc > now.AddMinutes(65) ||
            current.TargetRefs.ObservedAtUtc > now.AddSeconds(5) ||
            current.TargetRefs.ObservedAtUtc < now.AddSeconds(-25) ||
            current.CandidatePolicies.ObservedAtUtc > now.AddSeconds(5) ||
            current.CandidatePolicies.ObservedAtUtc < now.AddSeconds(-25) ||
            source.Candidates.Count is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            packet.CreateCandidates != source.Candidates.Count ||
            !GitSourceBundleStorageCheck.ValidSha(writeFence.PolicySha256) ||
            current.CandidatePolicies.RulesetCatalogStatus != "Observed")
            throw new GitHubRemoteAppPreflightException("RemotePreflightEvidenceInvalid");

        // F-042 includes annotated TAG object types in this digest. Compare
        // the entire fresh GitHub ref set, never just the candidate list.
        var fingerprint = GitHubRemoteRefFenceIntegrity.Fingerprint(
            current.TargetRefs.Refs);
        if (fingerprint != previousFence.RemoteRefFingerprintSha256 ||
            current.TargetRefs.Refs.Count != packet.AlreadyMatching +
                packet.TargetOnlyPreserved ||
            current.TargetRefs.Refs.Any(x =>
                source.Candidates.Any(c => c.Name == x.Name)))
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightTargetDriftBlocked");

        var policy = GitHubCandidatePolicyIntegrity.Seal(source,
            current.CandidatePolicies);
        if (policy.Status != "AdvisoryNoRulesReported" ||
            policy.NoActiveRulesReported != source.Candidates.Count ||
            policy.RulesRequireReview != 0 || policy.PolicyUnverified != 0)
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightPolicyReviewRequired");

        if (!GitSourceBundleStorageCheck.ValidSha(policy.EvidenceSha256) ||
            source.Packet.CandidateSetSha256 !=
                GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", source.Candidates))
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightCandidateChanged");

        var stable = JsonSerializer.Serialize(new object[] {
            "f051-github-app-readonly-preflight-v1",
            writeFence.RequestId.ToString("D"), writeFence.Generation,
            packet.RepositoryId, packet.EvidenceSha256,
            packet.CandidateSetSha256, writeFence.PolicySha256,
            previousFence.RemoteRefFingerprintSha256, fingerprint,
            policy.EvidenceSha256, packet.AlreadyMatching +
                packet.TargetOnlyPreserved,
            source.Candidates.Count,
            current.TargetRefs.ObservedAtUtc.ToUnixTimeMilliseconds(),
            current.CandidatePolicies.ObservedAtUtc.ToUnixTimeMilliseconds(),
            current.TokenExpiresAtUtc.ToUnixTimeMilliseconds(),
            VerifiedState, false, false, false
        });
        var proof = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(stable)));
        return new(writeFence.RequestId, writeFence.Generation,
            packet.RepositoryId, packet.EvidenceSha256,
            packet.CandidateSetSha256, writeFence.PolicySha256,
            policy.EvidenceSha256,
            previousFence.RemoteRefFingerprintSha256, fingerprint,
            packet.AlreadyMatching + packet.TargetOnlyPreserved,
            source.Candidates.Count, current.TargetRefs.ObservedAtUtc,
            current.TokenExpiresAtUtc, proof, VerifiedState,
            false, false, false);
    }
}

public sealed class GitHubRemoteAppPreflightService(
    GitHubCreateOnlyPacketService packets,
    IGitHubRemoteSandboxFenceStore fences,
    IGitHubTransferAttemptStore attempts,
    IGitHubRemoteRefFenceStore prior,
    IGitHubAppWriteScopeStore scopes,
    IGitHubRemoteAppPreflightReader reader,
    IConnectionSecretStore secrets,
    GitHubRemoteSandboxFenceOptions fenceOptions,
    GitHubAppWriteCredentialOptions credentials,
    TimeProvider clock)
{
    public async Task<GitHubRemoteAppPreflightReceipt> InspectAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        Guid requestId, long generation, string expectedPacketSha256,
        CancellationToken ct)
    {
        if (!fenceOptions.Enabled || !credentials.Enabled ||
            fenceOptions.AllowedRepositoryId <= 0 ||
            credentials.AppId <= 0 || credentials.InstallationId <= 0 ||
            string.IsNullOrWhiteSpace(credentials.PrivateKeySecretReference) ||
            requestId == Guid.Empty || generation <= 0 ||
            !GitSourceBundleStorageCheck.ValidSha(expectedPacketSha256))
            throw new GitHubRemoteAppPreflightException("RemotePreflightDisabledOrInvalid");

        var source = await packets.PreviewWithCandidatesAsync(
            preparationId, planId, scopeId, ct);
        var packet = source.Packet;
        if (packet.EvidenceSha256 != expectedPacketSha256 ||
            packet.RepositoryId != fenceOptions.AllowedRepositoryId ||
            !string.Equals(fenceOptions.AllowedRepository,
                packet.Organization + "/" + packet.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(credentials.AllowedRepository,
                packet.Organization + "/" + packet.Repository,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightNotAllowlisted");

        var writeFence = await fences.GetAsync(packet.RepositoryId, ct)
            ?? throw new KeyNotFoundException("RemoteSandboxWriteFenceNotFound");
        if (writeFence.RequestId != requestId ||
            writeFence.Generation != generation ||
            writeFence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled)
            throw new GitHubRemoteAppPreflightException("RemotePreflightWriteFenceLost");

        var attempt = await attempts.GetAsync(preparationId, planId,
            scopeId, writeFence.AttemptId, ct);
        if (!MatchesHardWriteBarrier(attempt, packet, writeFence))
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightHardBarrierChanged");

        var original = await prior.GetAsync(preparationId,
            planId, packet.FenceId, ct)
            ?? throw new KeyNotFoundException("PriorRemoteRefFenceNotFound");
        var scope = await scopes.GetAsync(preparationId,
            planId, scopeId, ct)
            ?? throw new KeyNotFoundException("AppScopeNotFound");
        if (scope.InstallationId != credentials.InstallationId ||
            scope.RepositoryId != packet.RepositoryId)
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightInstallationChanged");

        // A private signing key remains only in process memory. The reader
        // mints a one-repository installation token and consumes it for
        // REST GETs; no credential is returned, logged or persisted.
        var signingKey = await secrets.ReadAsync(
            credentials.PrivateKeySecretReference, ct);
        if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length > 32_768)
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightSigningKeyMissing");

        GitHubRemoteAppPreflightObservation result;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(18));
            try
            {
                result = await reader.InspectAsync(credentials.AppId,
                    credentials.InstallationId, signingKey, source,
                    deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GitHubRemoteAppPreflightException(
                    "RemotePreflightProviderTimeout");
            }
        }

        // F-050 may expire while GitHub GET calls are running. Re-read both
        // the physical F-044 bundle packet and the F-050 row afterwards.
        var freshPacket = await packets.PreviewAsync(
            preparationId, planId, scopeId, ct);
        var freshFence = await fences.GetAsync(packet.RepositoryId, ct)
            ?? throw new GitHubRemoteAppPreflightException(
                "RemotePreflightWriteFenceLost");
        var freshAttempt = await attempts.GetAsync(preparationId,
            planId, scopeId, writeFence.AttemptId, ct);
        if (freshPacket.EvidenceSha256 != packet.EvidenceSha256 ||
            freshPacket.CandidateSetSha256 != packet.CandidateSetSha256 ||
            freshFence.RequestId != requestId ||
            freshFence.Generation != generation ||
            freshFence.State != GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled ||
            !MatchesHardWriteBarrier(freshAttempt, packet, freshFence))
            throw new GitHubRemoteAppPreflightException(
                "RemotePreflightChangedDuringIO");

        return GitHubRemoteAppPreflightIntegrity.Seal(
            source, freshFence, original, scope, result, clock.GetUtcNow());
    }

    private static bool MatchesHardWriteBarrier(
        GitHubTransferAttemptView? attempt, GitHubCreateOnlyPacket packet,
        GitHubRemoteSandboxFenceStatus fence) =>
        attempt is not null &&
        attempt.Id == fence.AttemptId &&
        attempt.PreparationId == packet.PreparationId &&
        attempt.PlanId == packet.PlanId &&
        attempt.ScopeId == packet.ScopeId &&
        attempt.RepositoryId == packet.RepositoryId &&
        attempt.PacketSha256 == packet.EvidenceSha256 &&
        attempt.CandidateSetSha256 == packet.CandidateSetSha256 &&
        attempt.PolicySha256 == fence.PolicySha256 &&
        attempt.CandidateCount == packet.CreateCandidates &&
        attempt.State == GitHubTransferAttemptStates.BlockedAtWriteBarrier &&
        attempt.ReviewedBy is not null &&
        attempt.ReviewedBy != attempt.RequestedBy &&
        !attempt.TransportAttempted && !attempt.ExecutionAuthorized &&
        !attempt.RemoteWritesPerformed && !attempt.ReadyForExecution;
}
