using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-046: candidate-specific GET-only repository policy review.
// A reported empty ruleset list is NOT permission to create a Git ref.
public static class GitHubCandidatePolicyStates
{
    public const string NoActiveRulesReported = "NoActiveRulesReported";
    public const string RulesRequireReview = "RulesRequireReview";
    public const string PolicyUnverified = "PolicyUnverified";
    public const string RequiresRemoteReconciliation = "RequiresRemoteReconciliation";
}

public sealed record GitHubCandidateRuleStatus(
    string RefNameSha256, string Kind, string Status, int ActiveRules,
    string ProviderStatus);

public sealed record GitHubCandidatePolicyObservation(
    IReadOnlyList<GitHubCandidateRuleStatus> Items,
    DateTimeOffset ObservedAtUtc, string RulesetCatalogStatus);

public sealed record GitHubCandidatePolicyReview(
    Guid ScopeId, long RepositoryId, string PacketSha256,
    string EvidenceSha256, DateTimeOffset ObservedAtUtc,
    int Candidates, int NoActiveRulesReported, int RulesRequireReview,
    int PolicyUnverified, string Status, IReadOnlyList<GitHubCandidateRuleStatus> Items,
    bool WriteCapabilityVerified, bool ExecutionAuthorized,
    bool RemoteWritesPerformed, bool ReadyForExecution);

public sealed class GitHubCandidatePolicyException(string code)
    : Exception("GitHub candidate policy review rejected")
{
    public string Code { get; } = code;
}

public static class GitHubCandidatePolicyIntegrity
{
    // A bounded operator probe, NOT a 128-candidate permissions grant.
    // Large imports must be explicitly split/reviewed by later workers.
    public const int MaxOnlineCandidates = 16;

    public static string RefNameDigest(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "f046-ref-name-v1\n" + name)));

    public static GitHubCandidatePolicyReview Seal(
        GitHubCreateOnlyCandidatePacket source,
        GitHubCandidatePolicyObservation observation)
    {
        if (source is null || observation is null || source.Packet is null ||
            source.Candidates is null || observation.Items is null ||
            observation.ObservedAtUtc == default ||
            source.Candidates.Count is < 1 or > MaxOnlineCandidates ||
            source.Packet.CreateCandidates != source.Candidates.Count ||
            !GitSourceBundleStorageCheck.ValidSha(source.Packet.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(source.Packet.CandidateSetSha256) ||
            source.Packet.State != GitHubCreateOnlyPacketIntegrity.State ||
            source.Packet.ExecutionAuthorized || source.Packet.WriteCapabilityVerified ||
            source.Packet.RemoteWritesPerformed || source.Packet.ReadyForExecution ||
            observation.Items.Count != source.Candidates.Count ||
            observation.RulesetCatalogStatus is not (
                "Observed" or "RepoUnverified" or "CatalogUnverified"))
            throw new GitHubCandidatePolicyException("CandidatePolicyObservationInvalid");

        var expected = source.Candidates.Select(x => new {
            Hash = RefNameDigest(x.Name),
            Kind = x.Name.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? "Branch" : x.Name.StartsWith("refs/tags/", StringComparison.Ordinal)
                    ? "Tag" : "Invalid"
        }).ToArray();
        if (expected.Any(x => x.Kind == "Invalid") ||
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", source.Candidates)
                != source.Packet.CandidateSetSha256)
            throw new GitHubCandidatePolicyException("CandidatePolicyObservationInvalid");

        var safe = 0; var review = 0; var unknown = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var item = observation.Items[i];
            if (item.RefNameSha256 != expected[i].Hash ||
                item.Kind != expected[i].Kind || item.ActiveRules < 0 ||
                item.ActiveRules > 99 || item.ProviderStatus is null ||
                item.ProviderStatus.Length > 32 ||
                item.ProviderStatus.Any(x => !char.IsAsciiLetterOrDigit(x)) ||
                item.Status is not (
                    GitHubCandidatePolicyStates.NoActiveRulesReported or
                    GitHubCandidatePolicyStates.RulesRequireReview or
                    GitHubCandidatePolicyStates.PolicyUnverified) ||
                (item.Status == GitHubCandidatePolicyStates.RulesRequireReview
                    && item.ActiveRules == 0) ||
                (item.Status != GitHubCandidatePolicyStates.PolicyUnverified &&
                    (observation.RulesetCatalogStatus != "Observed" ||
                     item.ProviderStatus != "Observed")) ||
                (item.Status == GitHubCandidatePolicyStates.NoActiveRulesReported
                    && item.ActiveRules != 0))
                throw new GitHubCandidatePolicyException("CandidatePolicyObservationInvalid");
            if (item.Status == GitHubCandidatePolicyStates.NoActiveRulesReported) safe++;
            if (item.Status == GitHubCandidatePolicyStates.RulesRequireReview) review++;
            if (item.Status == GitHubCandidatePolicyStates.PolicyUnverified) unknown++;
        }

        var canonical = JsonSerializer.Serialize(new object?[] {
            "f046-candidate-policy-review-v1",
            source.Packet.ScopeId.ToString("D"), source.Packet.RepositoryId,
            source.Packet.EvidenceSha256, source.Packet.CandidateSetSha256,
            observation.ObservedAtUtc.ToUnixTimeMilliseconds(),
            observation.RulesetCatalogStatus, safe, review, unknown,
            observation.Items.Select(x => new object[] {
                x.RefNameSha256, x.Kind, x.Status, x.ActiveRules, x.ProviderStatus
            }).ToArray(), false, false, false, false
        });
        var sha = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(source.Packet.ScopeId, source.Packet.RepositoryId,
            source.Packet.EvidenceSha256, sha, observation.ObservedAtUtc,
            source.Candidates.Count, safe, review, unknown,
            unknown == 0 && review == 0
                ? "AdvisoryNoRulesReported" : "HumanReviewRequired",
            observation.Items, false, false, false, false);
    }
}

public interface IGitHubCandidatePolicyReader
{
    Task<GitHubCandidatePolicyObservation> ReadAsync(
        string organization, string repository, long expectedRepositoryId,
        string readOnlyToken, IReadOnlyList<GitMirrorRef> candidates,
        CancellationToken ct);
}

public sealed class GitHubCandidatePolicyService(
    GitHubCreateOnlyPacketService packets,
    IIncrementalOperationPlanRepository plans,
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IGitHubCandidatePolicyReader reader,
    TimeProvider clock)
{
    public async Task<GitHubCandidatePolicyReview> ReviewAsync(
        Guid preparationId, Guid planId, Guid scopeId,
        string expectedPacketSha256, CancellationToken ct)
    {
        if (!GitSourceBundleStorageCheck.ValidSha(expectedPacketSha256))
            throw new GitHubCandidatePolicyException("CandidatePolicyRequestInvalid");

        var source = await packets.PreviewWithCandidatesAsync(
            preparationId, planId, scopeId, ct);
        if (source.Packet.EvidenceSha256 != expectedPacketSha256)
            throw new GitHubCandidatePolicyException("CandidatePolicyPacketChanged");
        if (source.Candidates.Count > GitHubCandidatePolicyIntegrity.MaxOnlineCandidates)
            throw new GitHubCandidatePolicyException("CandidatePolicyOnlineBudgetExceeded");

        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        if (!plan.IsCurrent || plan.ManifestSha256 != source.Packet.ManifestSha256)
            throw new GitHubCandidatePolicyException("CandidatePolicyPlanChanged");
        var destination = await connections.GetAsync(
            plan.DestinationConnectionId, ct)
            ?? throw new KeyNotFoundException("DestinationConnectionNotFound");
        if (destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            !string.Equals(destination.Organization,
                source.Packet.Organization, StringComparison.OrdinalIgnoreCase))
            throw new GitHubCandidatePolicyException("CandidatePolicyDestinationChanged");
        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GitHubCandidatePolicyException("CandidatePolicyCredentialUnavailable");

        // F-043's one-use installation token was discarded. This is the
        // original server-held connection read credential, used for GET only.
        GitHubCandidatePolicyObservation observed;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(18));
            try
            {
                observed = await reader.ReadAsync(source.Packet.Organization,
                    source.Packet.Repository, source.Packet.RepositoryId, token,
                    source.Candidates, deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GitHubCandidatePolicyException("CandidatePolicyProviderTimeout");
            }
        }

        // A 30-second F-042 fence may expire while provider GETs complete.
        // Re-derive and re-hash the F-044 packet AFTER network I/O.
        var final = await packets.PreviewAsync(
            preparationId, planId, scopeId, ct);
        if (final.EvidenceSha256 != source.Packet.EvidenceSha256 ||
            final.CandidateSetSha256 != source.Packet.CandidateSetSha256 ||
            observed.ObservedAtUtc > clock.GetUtcNow().AddSeconds(5))
            throw new GitHubCandidatePolicyException("CandidatePolicyPacketChanged");
        return GitHubCandidatePolicyIntegrity.Seal(source, observed);
    }
}

// Future GitHub Git-transport worker MUST distinguish a definite refusal
// before transmission from an ambiguous network result. This type cannot
// initiate writes and never suggests an automatic retry after possible send.
public enum GitHubRemoteWriteSignal
{
    NotStarted, RejectedBeforeTransmit, TimeoutAfterTransmit,
    ConnectionLost, ServerErrorAfterTransmit, ReportedPartialSuccess,
    ReportedSuccess, UnexpectedRemoteRefs
}

public sealed record GitHubRemoteWriteOutcome(
    string State, bool NeedsIndependentRemoteReconciliation,
    bool MayAutomaticallyRetry, bool ExecutionAuthorized);

public static class GitHubRemoteWriteOutcomeGuard
{
    public static GitHubRemoteWriteOutcome Classify(
        GitHubRemoteWriteSignal signal) => signal switch
    {
        GitHubRemoteWriteSignal.NotStarted or
        GitHubRemoteWriteSignal.RejectedBeforeTransmit =>
            new("NoRemoteWriteAttempt", false, false, false),
        GitHubRemoteWriteSignal.UnexpectedRemoteRefs =>
            new("RemoteDivergenceBlocked", true, false, false),
        _ => new(GitHubCandidatePolicyStates.RequiresRemoteReconciliation,
            true, false, false)
    };
}
