using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-058 verifies historical, permanently write-disabled lineage.
// Independent read-only reconciliation is allowed after an F-052 grant
// has expired; expiry never becomes execution/retry authority.
internal static class GitHubRemoteObjectReconciliationGuard
{
    public static void Require(
        GitHubRemoteObjectReconciliationEvidence input,
        GitHubRemoteObjectReconciliationDecision proof,
        GitHubReadOnlyObjectMirrorOptions options,
        GitHubRemoteTestGrantRecord grant,
        GitHubRemoteReadbackRecord readback,
        GitHubRemoteTransmitHazardRecord hazard,
        GitHubRemoteSandboxFenceRecord? fence,
        GitHubTransferAttemptRecord? attempt)
    {
        if (fence is null || attempt is null)
            throw Blocked();
        var grantNoWrite = grant.State is
            GitHubRemoteTestGrantStates.ReviewedWriteDisabled or
            GitHubRemoteTestGrantStates.RecoveryRequired;
        var fenceNoWrite = fence.State is
            GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled or
            GitHubRemoteSandboxFenceStates.RecoveryRequired;
        Guid?[] oldActors = [
            grant.RequestedBy, grant.ReviewedBy,
            fence.RequestedBy, fence.ReviewedBy,
            attempt.RequestedBy, attempt.ReviewedBy,
            readback.ObservedBy, hazard.PreparedBy, hazard.MarkedBy
        ];
        if (!grantNoWrite || !fenceNoWrite ||
            !string.Equals(options.AllowedRepository,
                grant.Organization + "/" + grant.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(input.MirrorRequest.Organization,
                grant.Organization, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(input.MirrorRequest.Repository,
                grant.Repository, StringComparison.OrdinalIgnoreCase) ||
            grant.ReviewedBy is null ||
            grant.FenceRequestId != hazard.FenceRequestId ||
            grant.FenceGeneration != hazard.FenceGeneration ||
            grant.PacketSha256 != hazard.PacketSha256 ||
            grant.CandidateSetSha256 != hazard.CandidateSetSha256 ||
            grant.BaselineSha256 != hazard.BaselineSha256 ||
            grant.BaselineSha256 != proof.OriginalBaselineSha256 ||
            grant.CandidateSetSha256 != proof.CandidateSetSha256 ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution ||
            hazard.State != GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
            hazard.MarkedBy is null ||
            hazard.UpdatedAtUtc > proof.VerifiedAtUtc ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            readback.State != GitHubRemoteReadbackStates.ExactTipEvidenceOnly ||
            readback.BaselineSha256 != proof.OriginalBaselineSha256 ||
            readback.CandidateSetSha256 != proof.CandidateSetSha256 ||
            readback.RefSetSha256 != proof.TargetRefSetSha256 ||
            readback.ObservedAtUtc > proof.VerifiedAtUtc ||
            readback.TransportAttempted || readback.RemoteWritesAuthorized ||
            readback.MayAutomaticallyRetry ||
            fence.RequestId != grant.FenceRequestId ||
            fence.Generation != grant.FenceGeneration ||
            fence.AttemptId != grant.AttemptId ||
            fence.PacketSha256 != grant.PacketSha256 ||
            fence.CandidateSetSha256 != grant.CandidateSetSha256 ||
            fence.PolicySha256 != grant.OriginalPolicySha256 ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution ||
            attempt.Id != grant.AttemptId ||
            attempt.RepositoryId != input.RepositoryId ||
            attempt.PreparationId != grant.PreparationId ||
            attempt.PlanId != grant.PlanId ||
            attempt.ScopeId != grant.ScopeId ||
            attempt.PacketSha256 != grant.PacketSha256 ||
            attempt.CandidateSetSha256 != grant.CandidateSetSha256 ||
            attempt.PolicySha256 != grant.OriginalPolicySha256 ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution ||
            oldActors.Any(x => x is null || x == Guid.Empty ||
                x == input.ObserverId))
            throw Blocked();
    }

    private static GitHubRemoteObjectReconciliationException Blocked() =>
        new("RemoteObjectReconciliationBarrierChanged");
}
