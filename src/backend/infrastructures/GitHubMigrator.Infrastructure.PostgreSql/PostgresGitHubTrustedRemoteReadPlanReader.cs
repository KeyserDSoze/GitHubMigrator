using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-059: original source identity is derived from persisted F-029 plan,
// matched to exactly one F-029 TransferGitHistory step and the F-014
// verified source ref snapshot. No caller can choose local source path.
public sealed class PostgresGitHubTrustedRemoteReadPlanReader(
    AppDbContext db, TimeProvider clock,
    IIncrementalOperationPlanRepository planRepository,
    IVerifiedSourceGitRefReader sourceRefs,
    GitHubReadOnlyObjectMirrorOptions options)
    : IGitHubTrustedRemoteReadPlanReader
{
    public async Task<GitHubTrustedRemoteReadPlan> ReadAsync(
        GitHubTrustedRemoteReadCommand input, CancellationToken ct)
    {
        if (input.RepositoryId <= 0 || input.GrantId == Guid.Empty ||
            !options.Enabled || options.AllowedRepositoryId != input.RepositoryId ||
            string.IsNullOrWhiteSpace(options.AllowedRepository))
            throw Reject("TrustedRemoteReadDisabled");

        var grant = await db.GitHubRemoteTestGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.GrantId &&
                x.RepositoryId == input.RepositoryId, ct)
            ?? throw Reject("TrustedRemoteReadGrantNotFound");
        var hazard = await db.GitHubRemoteTransmitHazards.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.HazardId &&
                x.GrantId == input.GrantId &&
                x.RepositoryId == input.RepositoryId, ct)
            ?? throw Reject("TrustedRemoteReadHazardNotFound");
        var readback = await db.GitHubRemoteReadbacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.ReadbackId &&
                x.GrantId == input.GrantId &&
                x.RepositoryId == input.RepositoryId, ct)
            ?? throw Reject("TrustedRemoteReadReadbackNotFound");
        var fence = await db.GitHubRemoteSandboxFences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RepositoryId == input.RepositoryId, ct);
        var attempt = await db.GitHubTransferAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.AttemptId, ct);

        // Before network I/O, reject all obviously unrelated or revoked
        // historical state, even if some old hashes happen to match.
        var allowedGrant = grant.State is
            GitHubRemoteTestGrantStates.ReviewedWriteDisabled or
            GitHubRemoteTestGrantStates.RecoveryRequired;
        var allowedFence = fence is not null &&
            fence.State is GitHubRemoteSandboxFenceStates.ReviewedWriteDisabled or
                GitHubRemoteSandboxFenceStates.RecoveryRequired;
        if (!allowedGrant || !allowedFence || fence is null ||
            attempt is null || grant.ReviewedBy is null ||
            !string.Equals(options.AllowedRepository,
                grant.Organization + "/" + grant.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fence.Organization, grant.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fence.Repository, grant.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Organization, grant.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(attempt.Repository, grant.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            fence.RequestId != grant.FenceRequestId ||
            fence.Generation != grant.FenceGeneration ||
            fence.AttemptId != grant.AttemptId ||
            fence.PacketSha256 != grant.PacketSha256 ||
            fence.CandidateSetSha256 != grant.CandidateSetSha256 ||
            fence.PolicySha256 != grant.OriginalPolicySha256 ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution || grant.ExecutionAuthorized ||
            grant.RemoteWritesPerformed || grant.ReadyForExecution ||
            attempt.Id != grant.AttemptId ||
            attempt.RepositoryId != input.RepositoryId ||
            attempt.PreparationId != grant.PreparationId ||
            attempt.PlanId != grant.PlanId ||
            attempt.ScopeId != grant.ScopeId ||
            attempt.PacketSha256 != grant.PacketSha256 ||
            attempt.PolicySha256 != grant.OriginalPolicySha256 ||
            attempt.CandidateSetSha256 != grant.CandidateSetSha256 ||
            attempt.State != GitHubTransferAttemptStates.BlockedAtWriteBarrier ||
            attempt.TransportAttempted || attempt.ExecutionAuthorized ||
            attempt.RemoteWritesPerformed || attempt.ReadyForExecution ||
            hazard.State != GitHubRemoteTransmitHazardStates.MayHaveTransmitted ||
            hazard.MarkedBy is null ||
            hazard.GrantId != grant.Id ||
            hazard.FenceRequestId != grant.FenceRequestId ||
            hazard.FenceGeneration != grant.FenceGeneration ||
            hazard.PacketSha256 != grant.PacketSha256 ||
            hazard.CandidateSetSha256 != grant.CandidateSetSha256 ||
            hazard.BaselineSha256 != grant.BaselineSha256 ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry ||
            readback.State != GitHubRemoteReadbackStates.ExactTipEvidenceOnly ||
            readback.BaselineSha256 != grant.BaselineSha256 ||
            readback.CandidateSetSha256 != grant.CandidateSetSha256 ||
            readback.TransportAttempted || readback.RemoteWritesAuthorized ||
            readback.MayAutomaticallyRetry ||
            input.ObserverId == grant.RequestedBy ||
            input.ObserverId == grant.ReviewedBy ||
            input.ObserverId == fence.RequestedBy ||
            input.ObserverId == fence.ReviewedBy ||
            input.ObserverId == attempt.RequestedBy ||
            input.ObserverId == attempt.ReviewedBy ||
            input.ObserverId == readback.ObservedBy ||
            input.ObserverId == hazard.PreparedBy ||
            input.ObserverId == hazard.MarkedBy)
            throw Reject("TrustedRemoteReadBarrierChanged");

        IReadOnlyList<GitMirrorRef> candidates;
        try
        {
            var baseline = GitHubRefObservationIntegrity.Seal(
                grant.Organization, grant.Repository, clock.GetUtcNow(),
                input.OriginalBaseline);
            if (!baseline.Refs.SequenceEqual(input.OriginalBaseline))
                throw Reject("TrustedRemoteReadBaselineInvalid");
            candidates = GitHubRemoteTipIntegrity.CanonicalCandidates(
                input.CreateCandidates);
        }
        catch (GitHubRefObservationException)
        {
            throw Reject("TrustedRemoteReadBaselineInvalid");
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw Reject("TrustedRemoteReadBaselineInvalid");
        }
        catch (GitHubRemoteTipException)
        {
            throw Reject("TrustedRemoteReadCandidatesInvalid");
        }

        var baselineSha = GitHubRemoteRefFenceIntegrity.Fingerprint(
            input.OriginalBaseline);
        var candidatesSha = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", candidates);
        if (baselineSha != grant.BaselineSha256 ||
            candidatesSha != grant.CandidateSetSha256)
            throw Reject("TrustedRemoteReadStoredHashesMismatch");

        // F-054 stores a SHA of provider-typed refs. It is not possible
        // to reconstruct annotated tag object types from an OID alone.
        // The actual provider observation is checked by F-059/F-058.
        var plan = await planRepository.GetAsync(
            grant.PreparationId, grant.PlanId, ct)
            ?? throw Reject("TrustedRemoteReadSourcePlanUnavailable");
        if (!plan.IsCurrent || plan.SnapshotId == Guid.Empty ||
            plan.SourceConnectionId == Guid.Empty || plan.ReadyForExecution ||
            plan.Id != grant.PlanId ||
            plan.PreparationId != grant.PreparationId)
            throw Reject("TrustedRemoteReadSourcePlanInvalid");

        var sourceSteps = plan.Steps.Where(x =>
            x.Kind == "TransferGitHistory" &&
            string.Equals(x.TargetRepositoryName, grant.Repository,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sourceSteps.Length != 1 ||
            string.IsNullOrWhiteSpace(sourceSteps[0].SourceRepositoryId))
            throw Reject("TrustedRemoteReadSourceMappingInvalid");

        VerifiedSourceGitRefs source;
        try
        {
            source = await sourceRefs.ReadAsync(
                plan, sourceSteps[0].SourceRepositoryId, ct);
        }
        catch (IncrementalGitRefReconciliationException)
        {
            throw Reject("TrustedRemoteReadSourceSnapshotInvalid");
        }
        if (source.SnapshotId != plan.SnapshotId ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            !GitSourceBundleStorageCheck.ValidSha(source.SourceEvidenceSha256) ||
            source.Refs.Count == 0)
            throw Reject("TrustedRemoteReadSourceSnapshotInvalid");

        // The F-057 auditor must still verify that the physical local
        // <snapshotId>.git actually has precisely these trusted refs.
        // A staging snapshot alone is not physical Git-object evidence.
        GitMirrorRef[] canonical;
        try
        {
            canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                source.Refs).ToArray();
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw Reject("TrustedRemoteReadSourceSnapshotInvalid");
        }
        if (!canonical.SequenceEqual(source.Refs))
            throw Reject("TrustedRemoteReadSourceSnapshotInvalid");

        var result = new GitHubTrustedRemoteReadPlan(
            plan.SnapshotId, grant.Organization, grant.Repository,
            source.SourceEvidenceSha256, canonical,
            baselineSha, candidatesSha, readback.RefSetSha256, "");
        return result with {
            PlanEvidenceSha256 = GitHubTrustedRemoteReadCoordinator.Fingerprint(result)
        };
    }

    private static GitHubTrustedRemoteReadException Reject(string code) =>
        new(code);
}
