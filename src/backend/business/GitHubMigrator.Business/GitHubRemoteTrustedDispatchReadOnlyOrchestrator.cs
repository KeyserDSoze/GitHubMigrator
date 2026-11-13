namespace GitHubMigrator.Business;

// F-076: the only authority adapter exposed to the orchestrator is the
// real F-075 PostgreSQL verifier. It must re-read and lock the current
// F-045/F-047/F-050/F-052/F-055 rows before sealing an inert receipt.
public interface IGitHubRemoteDispatchAuthorityVerifier
{
    Task<GitHubRemoteCreateOnlyDispatchReview> VerifyAsync(
        GitHubCreateOnlyCandidatePacket source,
        GitHubRemoteAppPreflightReceipt preflight,
        GitHubRefObservation latest, Guid hazardId,
        Guid independentActor, CancellationToken ct);
}

// The F-051 preflight receipt must already be bound to the F-052 grant.
// F-075 checks its persisted digest and matching fence epoch. The source
// ref inventory, target ref inventory, private key, URL and Git command
// are NEVER accepted from callers by this read-only orchestrator.
public sealed class GitHubRemoteTrustedDispatchReadOnlyOrchestrator(
    IGitHubCreateOnlyPacketEvidenceReader packets,
    IGitHubRemoteAppPreflightReader github,
    IConnectionSecretStore secrets,
    IGitHubRemoteDispatchAuthorityVerifier authority,
    GitHubRemoteSandboxFenceOptions fenceOptions,
    GitHubAppWriteCredentialOptions credentials,
    TimeProvider clock,
    IGitHubRemoteAppPreflightEvidenceJournal? preflightJournal = null,
    IGitHubRemoteSandboxReviewSessionRecorder? sessionJournal = null)
{
    // F-078: the ONLY session creation path composes the trusted F-077
    // hazard-bound historical receipt with F-076 live GitHub GETs,
    // twice-rebuilt F-044 source and F-075 locked PostgreSQL review.
    // The separate F-078 journal re-locks all authority before INSERT.
    public async Task<GitHubRemoteSandboxReviewSessionReceipt>
        ReviewAndRecordSessionFromJournalAsync(Guid preparationId,
            Guid planId, Guid scopeId, Guid hazardId,
            Guid independentActor, CancellationToken ct)
    {
        if (sessionJournal is null)
            throw new GitHubRemoteSandboxReviewSessionException(
                "SandboxReviewSessionJournalUnavailable");
        var reviewed = await ReviewFromJournalAsync(preparationId,
            planId, scopeId, hazardId, independentActor, ct);
        return await sessionJournal.RecordAsync(reviewed, independentActor, ct);
    }

    // F-077: new trusted entrypoint. The reviewed F-051 hash comes from
    // the F-052 PostgreSQL grant, followed by a lookup of that exact
    // append-only receipt. Callers do not supply its body or digest.
    public async Task<GitHubRemoteCreateOnlyDispatchReview>
        ReviewFromJournalAsync(Guid preparationId, Guid planId,
            Guid scopeId, Guid hazardId, Guid independentActor,
            CancellationToken ct)
    {
        if (preflightJournal is null || hazardId == Guid.Empty)
            throw new GitHubRemoteCreateOnlyDispatchException(
                "RemoteDispatchPreflightJournalUnavailable");
        var prior = await preflightJournal.GetForHazardAsync(hazardId, ct)
            ?? throw new GitHubRemoteCreateOnlyDispatchException(
                "RemoteDispatchPreflightJournalMissing");
        return await ReviewAsync(preparationId, planId, scopeId,
            hazardId, independentActor, prior, ct);
    }

    public async Task<GitHubRemoteCreateOnlyDispatchReview> ReviewAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        Guid hazardId, Guid independentActor,
        GitHubRemoteAppPreflightReceipt pinnedPreflight,
        CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty || hazardId == Guid.Empty ||
            independentActor == Guid.Empty || pinnedPreflight is null ||
            !fenceOptions.Enabled || !credentials.Enabled ||
            fenceOptions.AllowedRepositoryId <= 0 ||
            credentials.AppId <= 0 || credentials.InstallationId <= 0 ||
            string.IsNullOrWhiteSpace(credentials.PrivateKeySecretReference) ||
            pinnedPreflight.State !=
                GitHubRemoteAppPreflightIntegrity.VerifiedState ||
            !GitSourceBundleStorageCheck.ValidSha(
                pinnedPreflight.EvidenceSha256))
            throw Refused();

        // Real F-044: reconstruct every candidate from immutable PostgreSQL
        // journal provenance, audit the physical bundle twice and reject
        // stale scope/operation-plan or human-intent evidence.
        var original = await packets.PreviewWithCandidatesAsync(
            preparationId, planId, scopeId, ct);
        if (!SourceAllowed(original, pinnedPreflight))
            throw Refused();

        // Secret reference is configured by the operator, not the caller.
        // Only the established F-072/F-073 reader ever sees key material.
        var privateKey = await secrets.ReadAsync(
            credentials.PrivateKeySecretReference, ct);
        if (string.IsNullOrWhiteSpace(privateKey) ||
            privateKey.Length > 32_768)
            throw Refused();

        GitHubRemoteAppPreflightObservation live;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(18));
            try
            {
                live = await github.InspectAsync(
                    credentials.AppId, credentials.InstallationId,
                    privateKey, original, deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GitHubRemoteCreateOnlyDispatchException(
                    "RemoteDispatchProviderTimeout");
            }
        }

        if (live is null || live.TargetRefs is null ||
            live.CandidatePolicies is null ||
            live.InstallationId != credentials.InstallationId ||
            live.RepositoryId != original.Packet.RepositoryId)
            throw Refused();

        var now = clock.GetUtcNow();
        if (live.TokenExpiresAtUtc <= now.AddMinutes(2) ||
            live.TokenExpiresAtUtc > now.AddMinutes(65) ||
            live.TargetRefs.ObservedAtUtc > now.AddSeconds(5) ||
            live.TargetRefs.ObservedAtUtc <= now.AddSeconds(-10) ||
            live.CandidatePolicies.ObservedAtUtc > now.AddSeconds(5) ||
            live.CandidatePolicies.ObservedAtUtc <= now.AddSeconds(-25))
            throw Refused();

        GitHubRefObservation normalized;
        GitHubCandidatePolicyReview policy;
        try
        {
            normalized = GitHubRefObservationIntegrity.Seal(
                live.TargetRefs.Organization,
                live.TargetRefs.Repository,
                live.TargetRefs.ObservedAtUtc,
                live.TargetRefs.Refs);
            policy = GitHubCandidatePolicyIntegrity.Seal(
                original, live.CandidatePolicies);
        }
        catch (GitHubRefObservationException)
        {
            throw Refused();
        }
        catch (GitHubCandidatePolicyException)
        {
            throw Refused();
        }
        if (!normalized.Refs.SequenceEqual(live.TargetRefs.Refs) ||
            normalized.EvidenceSha256 != live.TargetRefs.EvidenceSha256 ||
            !SameTarget(live.TargetRefs.Organization,
                live.TargetRefs.Repository, original.Packet) ||
            GitHubRemoteRefFenceIntegrity.Fingerprint(
                normalized.Refs) != pinnedPreflight.ObservedBaselineSha256 ||
            normalized.Refs.Count != pinnedPreflight.MatchingBaselineRefs ||
            normalized.Refs.Any(x => original.Candidates.Any(
                c => c.Name == x.Name)) ||
            policy.Status != "AdvisoryNoRulesReported" ||
            policy.NoActiveRulesReported != original.Candidates.Count ||
            policy.RulesRequireReview != 0 || policy.PolicyUnverified != 0 ||
            policy.WriteCapabilityVerified || policy.ExecutionAuthorized ||
            policy.RemoteWritesPerformed || policy.ReadyForExecution)
            throw new GitHubRemoteCreateOnlyDispatchException(
                "RemoteDispatchLiveTargetChanged");

        // Full server-side re-read AFTER GitHub network I/O. A lease,
        // reviewed source scope or on-disk bundle may have changed during
        // the two-pass F-073 GETs. The first packet cannot be replayed
        // as an old in-memory object after source drift.
        var fresh = await packets.PreviewWithCandidatesAsync(
            preparationId, planId, scopeId, ct);
        if (!SourceAllowed(fresh, pinnedPreflight) ||
            fresh.Packet != original.Packet ||
            !fresh.Candidates.SequenceEqual(original.Candidates))
            throw new GitHubRemoteCreateOnlyDispatchException(
                "RemoteDispatchSourceChangedDuringRead");

        // PostgreSQL F-075 samples the DATABASE clock AFTER acquiring both
        // repository locks, rechecks persisted authority/lease/audit and
        // signs F-074 write-disabled evidence under the same transaction.
        return await authority.VerifyAsync(fresh, pinnedPreflight,
            normalized, hazardId, independentActor, ct);
    }

    private bool SourceAllowed(
        GitHubCreateOnlyCandidatePacket? source,
        GitHubRemoteAppPreflightReceipt preflight)
    {
        if (source?.Packet is null || source.Candidates is null)
            return false;
        var p = source.Packet;
        return p.RepositoryId == fenceOptions.AllowedRepositoryId &&
            p.RepositoryId == preflight.RepositoryId &&
            p.PreparationId != Guid.Empty && p.PlanId != Guid.Empty &&
            p.ScopeId != Guid.Empty &&
            string.Equals(fenceOptions.AllowedRepository,
                p.Organization + "/" + p.Repository,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(credentials.AllowedRepository,
                p.Organization + "/" + p.Repository,
                StringComparison.OrdinalIgnoreCase) &&
            p.PreparationId != Guid.Empty &&
            p.State == GitHubCreateOnlyPacketIntegrity.State &&
            p.EvidenceSha256 == preflight.PacketSha256 &&
            p.CandidateSetSha256 == preflight.CandidateSetSha256 &&
            p.PolicySha256 == preflight.OriginalPolicySha256 &&
            preflight.AbsentCreateCandidates == p.CreateCandidates &&
            !p.WriteCapabilityVerified && !p.ExecutionAuthorized &&
            !p.RemoteWritesPerformed && !p.ReadyForExecution &&
            source.Candidates.Count == p.CreateCandidates &&
            source.Candidates.Count is >= 1 and <=
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates &&
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1",
                source.Candidates) == p.CandidateSetSha256;
    }

    private static bool SameTarget(string owner, string repo,
        GitHubCreateOnlyPacket packet) =>
        string.Equals(owner, packet.Organization,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(repo, packet.Repository,
            StringComparison.OrdinalIgnoreCase);

    private static GitHubRemoteCreateOnlyDispatchException Refused() =>
        new("RemoteDispatchTrustedEvidenceInvalid");
}
