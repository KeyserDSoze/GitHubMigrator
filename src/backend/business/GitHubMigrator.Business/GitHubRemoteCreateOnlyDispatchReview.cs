using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-074 is a pre-dispatch REVIEW, not an executable command, token,
// remote reservation, GitHub mutation or authorization. The only
// possible output is sealed, short-lived WriteDisabled evidence.
public sealed record GitHubRemoteCreateOnlyDispatchReview(
    Guid HazardId, Guid GrantId, Guid FenceRequestId, long FenceGeneration,
    long RepositoryId, string PacketSha256, string CandidateSetSha256,
    string OriginalPolicySha256, string ReviewedPolicySha256,
    string PreflightSha256, string BaselineSha256,
    string LastRefObservationSha256, int AbsentCreateCandidates,
    int ExistingPreservedRefs, DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc, string EvidenceSha256, string State,
    bool RemoteTransportInvoked, bool RemoteWritesAuthorized,
    bool MayAutomaticallyRetry, bool MigrationComplete);

public sealed class GitHubRemoteCreateOnlyDispatchException(string code)
    : Exception("GitHub create-only dispatch safety review refused")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteCreateOnlyDispatchIntegrity
{
    public const string State = "ReviewedWriteDisabled";
    public static readonly TimeSpan MaxPreflightAge =
        TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaxLastObservationAge =
        TimeSpan.FromSeconds(10);

    // Never accepts a body-supplied refspec or a GitHub credential. All
    // evidence must originate from the F-044 packet, F-051 read-only
    // preflight, F-052 reviewed grant and F-055 hazard journal.
    // This pure check does NOT replace fetching those four persisted
    // records and re-verifying their lineage immediately beforehand.
    public static GitHubRemoteCreateOnlyDispatchReview Seal(
        GitHubCreateOnlyCandidatePacket source,
        GitHubRemoteAppPreflightReceipt preflight,
        GitHubRemoteTestGrantStatus grant,
        GitHubRemoteTransmitHazardStatus hazard,
        GitHubRefObservation latest,
        Guid independentActor, DateTimeOffset now)
    {
        if (source?.Packet is null || source.Candidates is null ||
            preflight is null || grant is null || hazard is null ||
            latest?.Refs is null || independentActor == Guid.Empty ||
            now == default)
            throw Invalid();
        var packet = source.Packet;
        if (!GitHubRefObservationIntegrity.ValidOwner(packet.Organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(packet.Repository) ||
            packet.RepositoryId <= 0 ||
            packet.PreparationId == Guid.Empty || packet.PlanId == Guid.Empty ||
            packet.ScopeId == Guid.Empty || packet.FenceId == Guid.Empty ||
            packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            packet.WriteCapabilityVerified || packet.ExecutionAuthorized ||
            packet.RemoteWritesPerformed || packet.ReadyForExecution ||
            packet.CreateCandidates is < 1 or >
                GitHubCandidatePolicyIntegrity.MaxOnlineCandidates ||
            source.Candidates.Count != packet.CreateCandidates ||
            !ValidSha(packet.EvidenceSha256) ||
            !ValidSha(packet.CandidateSetSha256))
            throw Invalid();

        IReadOnlyList<GitMirrorRef> candidates;
        try
        {
            candidates = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                source.Candidates);
            if (!source.Candidates.SequenceEqual(candidates) ||
                candidates.Any(x => x.Name.StartsWith(
                    "refs/heads/", StringComparison.Ordinal) == false &&
                    x.Name.StartsWith(
                        "refs/tags/", StringComparison.Ordinal) == false))
                throw Invalid();
        }
        catch (GitMirrorRefReconciliationException) { throw Invalid(); }
        if (GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", candidates)
            != packet.CandidateSetSha256)
            throw Invalid();

        if (preflight.State !=
                GitHubRemoteAppPreflightIntegrity.VerifiedState ||
            preflight.RepositoryId != packet.RepositoryId ||
            preflight.RequestId == Guid.Empty ||
            preflight.Generation <= 0 ||
            preflight.PacketSha256 != packet.EvidenceSha256 ||
            preflight.CandidateSetSha256 != packet.CandidateSetSha256 ||
            preflight.AbsentCreateCandidates != candidates.Count ||
            preflight.MatchingBaselineRefs !=
                packet.AlreadyMatching + packet.TargetOnlyPreserved ||
            preflight.MatchingBaselineRefs < 0 ||
            !ValidSha(preflight.EvidenceSha256) ||
            !ValidSha(preflight.OriginalPolicySha256) ||
            preflight.OriginalPolicySha256 != packet.PolicySha256 ||
            !ValidSha(preflight.CurrentPolicySha256) ||
            !ValidSha(preflight.ExpectedBaselineSha256) ||
            preflight.ExpectedBaselineSha256 !=
                preflight.ObservedBaselineSha256 ||
            preflight.ObservedAtUtc > now.AddSeconds(5) ||
            preflight.ObservedAtUtc <= now.Subtract(MaxPreflightAge) ||
            preflight.InstallationTokenExpiresAtUtc <= now.AddMinutes(2) ||
            preflight.RemoteWritesPerformed ||
            preflight.ExecutionAuthorized || preflight.ReadyForExecution)
            throw Invalid();

        // F-052/F-055 are the ONLY authoritative state-machine witnesses:
        // the F-051 receipt alone says nothing about a previous possible
        // transmission. Refuse any old or marked hazard for this epoch.
        if (grant.Id == Guid.Empty || grant.AttemptId == Guid.Empty ||
            grant.RepositoryId != packet.RepositoryId ||
            !SameTarget(grant.Organization, grant.Repository, packet) ||
            grant.PreparationId != packet.PreparationId ||
            grant.PlanId != packet.PlanId || grant.ScopeId != packet.ScopeId ||
            grant.FenceRequestId != preflight.RequestId ||
            grant.FenceGeneration != preflight.Generation ||
            grant.PacketSha256 != packet.EvidenceSha256 ||
            grant.CandidateSetSha256 != packet.CandidateSetSha256 ||
            grant.OriginalPolicySha256 != preflight.OriginalPolicySha256 ||
            grant.BaselineSha256 != preflight.ObservedBaselineSha256 ||
            grant.PreflightSha256 != preflight.EvidenceSha256 ||
            grant.State != GitHubRemoteTestGrantStates.ReviewedWriteDisabled ||
            grant.RequestedBy == Guid.Empty || grant.ReviewedBy is null ||
            grant.RequestedBy == grant.ReviewedBy ||
            independentActor == grant.RequestedBy ||
            independentActor == grant.ReviewedBy ||
            grant.DeadlineUtc <= now ||
            grant.UpdatedAtUtc > now.AddSeconds(5) ||
            grant.ExecutionAuthorized || grant.RemoteWritesPerformed ||
            grant.ReadyForExecution)
            throw Invalid();

        if (hazard.Id == Guid.Empty || hazard.GrantId != grant.Id ||
            hazard.RepositoryId != packet.RepositoryId ||
            hazard.FenceRequestId != preflight.RequestId ||
            hazard.FenceGeneration != preflight.Generation ||
            hazard.PacketSha256 != packet.EvidenceSha256 ||
            hazard.CandidateSetSha256 != packet.CandidateSetSha256 ||
            hazard.BaselineSha256 != preflight.ObservedBaselineSha256 ||
            hazard.State != GitHubRemoteTransmitHazardStates.PreparedWriteDisabled ||
            hazard.PreparedBy == Guid.Empty || hazard.MarkedBy is not null ||
            hazard.DeadlineUtc <= now ||
            hazard.UpdatedAtUtc > now.AddSeconds(5) ||
            hazard.TransportAuthorized || hazard.TransportInvoked ||
            hazard.RemoteWritesPerformed || hazard.MayAutomaticallyRetry)
            throw Invalid();

        GitHubRefObservation reSealed;
        try
        {
            reSealed = GitHubRefObservationIntegrity.Seal(
                latest.Organization, latest.Repository,
                latest.ObservedAtUtc, latest.Refs);
        }
        catch (GitHubRefObservationException) { throw Invalid(); }
        if (!SameTarget(latest.Organization, latest.Repository, packet) ||
            reSealed.EvidenceSha256 != latest.EvidenceSha256 ||
            !reSealed.Refs.SequenceEqual(latest.Refs) ||
            latest.ObservedAtUtc < preflight.ObservedAtUtc ||
            latest.ObservedAtUtc > now.AddSeconds(5) ||
            latest.ObservedAtUtc <= now.Subtract(MaxLastObservationAge))
            throw Invalid();
        if (GitHubRemoteRefFenceIntegrity.Fingerprint(latest.Refs)
                != preflight.ObservedBaselineSha256 ||
            latest.Refs.Count != preflight.MatchingBaselineRefs ||
            latest.Refs.Any(x => candidates.Any(y => y.Name == x.Name)))
            throw new GitHubRemoteCreateOnlyDispatchException(
                "RemoteDispatchTargetDriftBlocked");

        var deadline = new[] {
            grant.DeadlineUtc, hazard.DeadlineUtc,
            preflight.ObservedAtUtc.Add(MaxPreflightAge),
            latest.ObservedAtUtc.Add(MaxLastObservationAge),
            preflight.InstallationTokenExpiresAtUtc.AddMinutes(-2)
        }.Min();
        if (deadline <= now)
            throw Invalid();

        // Only digests and counts leave this component. No Git commands,
        // exact refspecs, bearer credentials or remote-write capability.
        var canonical = JsonSerializer.Serialize(new object?[] {
            "f074-create-only-dispatch-readonly-v1",
            packet.PreparationId.ToString("D"), packet.PlanId.ToString("D"),
            packet.ScopeId.ToString("D"), packet.FenceId.ToString("D"),
            packet.RepositoryId, packet.Organization.ToLowerInvariant(),
            packet.Repository.ToLowerInvariant(),
            grant.Id.ToString("D"), hazard.Id.ToString("D"),
            preflight.RequestId.ToString("D"), preflight.Generation,
            packet.EvidenceSha256, packet.CandidateSetSha256,
            preflight.OriginalPolicySha256, preflight.CurrentPolicySha256,
            preflight.EvidenceSha256, preflight.ObservedBaselineSha256,
            latest.EvidenceSha256, candidates.Count,
            latest.Refs.Count, latest.ObservedAtUtc.ToUnixTimeMilliseconds(),
            deadline.ToUnixTimeMilliseconds(),
            independentActor.ToString("D"),
            State, false, false, false, false
        });
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical)));
        return new(hazard.Id, grant.Id, preflight.RequestId,
            preflight.Generation, packet.RepositoryId,
            packet.EvidenceSha256, packet.CandidateSetSha256,
            preflight.OriginalPolicySha256,
            preflight.CurrentPolicySha256, preflight.EvidenceSha256,
            preflight.ObservedBaselineSha256, latest.EvidenceSha256,
            candidates.Count, latest.Refs.Count, latest.ObservedAtUtc,
            deadline, digest, State, false, false, false, false);
    }

    // F-074 deliberately has no transport adapter and grants no permission.
    // A future executor must define its own independently reviewed
    // write authorization and destination-side compare-and-create fencing.
    private static GitHubRemoteCreateOnlyDispatchException Invalid() =>
        new("RemoteDispatchEvidenceInvalid");

    private static bool ValidSha(string? hash) =>
        GitSourceBundleStorageCheck.ValidSha(hash);

    private static bool SameTarget(string? org, string? repo,
        GitHubCreateOnlyPacket packet) =>
        string.Equals(org, packet.Organization,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(repo, packet.Repository,
            StringComparison.OrdinalIgnoreCase);
}
