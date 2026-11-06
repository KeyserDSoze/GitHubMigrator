using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-042: A fresh, GET-only REMOTE REF FENCE. Even RefSetUnchanged means
// ONLY that GitHub REST observations match F-031 at a moment in time.
// It is not a GitHub write capability, transport authorization or lease.
public sealed record GitHubRemoteRefFenceComparison(
    string Status, string RemoteRefFingerprintSha256, int BaselineCount,
    int RemoteCount, int ChangedRefs, DateTimeOffset ObservedAtUtc);

public sealed record GitHubRemoteRefFenceInput(
    Guid PreparationId, Guid PlanId, Guid WorkpackId, Guid IntentId,
    Guid RehearsalId, Guid TargetObservationId,
    Guid DestinationConnectionId, DateTimeOffset? DestinationValidatedAtUtc,
    string ManifestSha256, string ReconciliationSha256,
    string BundleSha256, string IntentEvidenceSha256,
    string RehearsalEvidenceSha256, string TargetEvidenceSha256,
    string RemoteEvidenceSha256, GitHubRemoteRefFenceComparison Comparison,
    Guid ActorId);

public sealed record GitHubRemoteRefFenceView(
    Guid Id, Guid PreparationId, Guid PlanId, Guid WorkpackId,
    Guid IntentId, Guid RehearsalId, Guid TargetObservationId,
    string ManifestSha256, string ReconciliationSha256,
    string BundleSha256, string IntentEvidenceSha256,
    string RehearsalEvidenceSha256, string TargetEvidenceSha256,
    string RemoteEvidenceSha256, string RemoteRefFingerprintSha256,
    string EvidenceSha256, string Status, int BaselineCount,
    int RemoteCount, int ChangedRefs, DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc, Guid ActorId, bool IsCurrent,
    bool RemoteWritesPerformed, bool WriteCapabilityVerified,
    bool ExecutionAuthorized, bool ReadyForExecution);

public sealed class GitHubRemoteRefFenceException(string code)
    : Exception("GitHub remote ref fence rejected")
{
    public string Code { get; } = code;
}

public static class GitHubRemoteRefFenceIntegrity
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private const string Match = "RefSetUnchanged";
    private const string Drift = "RemoteDriftBlocked";

    public static GitHubRemoteRefFenceComparison Compare(
        IReadOnlyList<GitHubObservedRef> baseline,
        GitHubRefObservation observed)
    {
        if (baseline is null || observed is null ||
            observed.Refs.Count > GitMirrorRefReconciler.MaxRefsPerSide)
            throw new GitHubRemoteRefFenceException("RemoteFenceObservationInvalid");
        // Both sides must be canonical and complete, including annotated tag
        // object types. This never assumes an empty 404 is an empty target.
        var expected = Canonicalize(baseline);
        var actual = Canonicalize(observed.Refs);
        if (!actual.SequenceEqual(observed.Refs) ||
            !expected.SequenceEqual(baseline))
            throw new GitHubRemoteRefFenceException("RemoteFenceObservationInvalid");

        var old = expected.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        var fresh = actual.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        var changes = old.Keys.Concat(fresh.Keys).Distinct(StringComparer.Ordinal)
            .Count(name => !old.TryGetValue(name, out var prior) ||
                !fresh.TryGetValue(name, out var current) || prior != current);

        return new(changes == 0 ? Match : Drift,
            Fingerprint(actual), expected.Count, actual.Count, changes,
            observed.ObservedAtUtc);
    }

    public static string Fingerprint(IReadOnlyList<GitHubObservedRef> refs)
    {
        var canonical = Canonicalize(refs);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(
            "github-remote-ref-set-v1\n" + canonical.Count + "\n"));
        foreach (var item in canonical)
            sha.AppendData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] {
                    item.Name, item.ObjectId, item.ObjectType
                }) + "\n"));
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static IReadOnlyList<GitHubObservedRef> Canonicalize(
        IReadOnlyList<GitHubObservedRef> source)
    {
        if (source is null || source.Count > GitMirrorRefReconciler.MaxRefsPerSide ||
            source.Any(x => x is null ||
                x.ObjectType is not ("commit" or "tag") ||
                x.Name.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                    x.ObjectType != "commit"))
            throw new GitHubRemoteRefFenceException("RemoteFenceObservationInvalid");
        try
        {
            var refs = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                source.Select(x => new GitMirrorRef(x.Name, x.ObjectId)).ToArray());
            var types = source.ToDictionary(x => x.Name, x => x.ObjectType,
                StringComparer.Ordinal);
            return refs.Select(x => new GitHubObservedRef(
                x.Name, x.ObjectId, types[x.Name])).ToArray();
        }
        catch (Exception ex) when (
            ex is GitMirrorRefReconciliationException or ArgumentException)
        { throw new GitHubRemoteRefFenceException("RemoteFenceObservationInvalid"); }
    }

    public static string Seal(GitHubRemoteRefFenceInput input)
    {
        if (input.PreparationId == Guid.Empty || input.PlanId == Guid.Empty ||
            input.WorkpackId == Guid.Empty || input.IntentId == Guid.Empty ||
            input.RehearsalId == Guid.Empty || input.TargetObservationId == Guid.Empty ||
            input.ActorId == Guid.Empty || input.DestinationConnectionId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(input.ManifestSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.ReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.BundleSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.IntentEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.RehearsalEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.TargetEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.RemoteEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.Comparison.RemoteRefFingerprintSha256) ||
            input.Comparison.Status is not (Match or Drift) ||
            input.Comparison.BaselineCount is < 0 or > 25000 ||
            input.Comparison.RemoteCount is < 0 or > 25000 ||
            input.Comparison.ChangedRefs is < 0 or > 50000 ||
            (input.Comparison.Status == Match) != (input.Comparison.ChangedRefs == 0) ||
            (input.Comparison.Status == Match &&
             input.Comparison.BaselineCount != input.Comparison.RemoteCount) ||
            input.Comparison.ObservedAtUtc == default)
            throw new GitHubRemoteRefFenceException("RemoteFenceEvidenceInvalid");

        var canonical = JsonSerializer.Serialize(new object?[] {
            "github-remote-ref-fence-v1",
            input.PreparationId.ToString("D"), input.PlanId.ToString("D"),
            input.WorkpackId.ToString("D"), input.IntentId.ToString("D"),
            input.RehearsalId.ToString("D"), input.TargetObservationId.ToString("D"),
            input.DestinationConnectionId.ToString("D"),
            input.DestinationValidatedAtUtc?.ToUnixTimeMilliseconds(),
            input.ManifestSha256, input.ReconciliationSha256,
            input.BundleSha256, input.IntentEvidenceSha256,
            input.RehearsalEvidenceSha256, input.TargetEvidenceSha256,
            input.RemoteEvidenceSha256,
            input.Comparison.RemoteRefFingerprintSha256,
            input.Comparison.Status, input.Comparison.BaselineCount,
            input.Comparison.RemoteCount, input.Comparison.ChangedRefs,
            input.Comparison.ObservedAtUtc.ToUnixTimeMilliseconds(),
            input.ActorId.ToString("D")
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public interface IGitHubRemoteRefFenceStore
{
    Task<GitHubRemoteRefFenceView> SaveAsync(
        GitHubRemoteRefFenceInput input, CancellationToken ct);
    Task<GitHubRemoteRefFenceView?> GetAsync(
        Guid preparationId, Guid planId, Guid fenceId, CancellationToken ct);
    Task<IReadOnlyList<GitHubRemoteRefFenceView>> ListAsync(
        Guid preparationId, Guid planId, Guid rehearsalId, CancellationToken ct);
}

public sealed class GitHubRemoteRefFenceService(
    IGitHubImportIntentStore intents,
    IGitImportRehearsalStore rehearsals,
    IGitHubImportReadinessStore readiness,
    IIncrementalGitRefReconciliationStore reconciliations,
    IGitHubTargetRefEvidenceStore targetEvidence,
    IIncrementalOperationPlanRepository plans,
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk,
    IGitHubRefObservationReader reader,
    IGitHubRemoteRefFenceStore store,
    TimeProvider clock)
{
    public async Task<GitHubRemoteRefFenceView> CheckAsync(
        Guid preparationId, Guid planId, Guid rehearsalId,
        string expectedRehearsalEvidenceSha256, Guid actorId,
        CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            rehearsalId == Guid.Empty || actorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(expectedRehearsalEvidenceSha256))
            throw new GitHubRemoteRefFenceException("RemoteFenceRequestInvalid");

        var rehearsal = await rehearsals.GetAsync(
            preparationId, planId, rehearsalId, ct)
            ?? throw new KeyNotFoundException("GitRehearsalNotFound");
        if (!rehearsal.IsCurrent ||
            rehearsal.EvidenceSha256 != expectedRehearsalEvidenceSha256 ||
            rehearsal.ExecutionAuthorized || rehearsal.ReadyForExecution ||
            rehearsal.DestinationWritesPerformed)
            throw new GitHubRemoteRefFenceException("RemoteFenceRehearsalStale");
        var intent = await intents.GetAsync(
            preparationId, planId, rehearsal.IntentId, ct)
            ?? throw new KeyNotFoundException("ImportIntentNotFound");
        if (!intent.IsCurrent || intent.IsExpired ||
            intent.State != GitHubImportIntentStates.Acknowledged ||
            intent.EvidenceSha256 != rehearsal.IntentEvidenceSha256 ||
            intent.SafeRefPlanSha256 != rehearsal.SafeRefPlanSha256)
            throw new GitHubRemoteRefFenceException("RemoteFenceIntentStale");
        var review = await readiness.GetAsync(preparationId,
            planId, intent.ImportReadinessId, ct)
            ?? throw new KeyNotFoundException("ImportReadinessNotFound");
        if (!review.IsCurrent ||
            review.EvidenceSha256 != intent.ImportReadinessSha256 ||
            review.SourceBundleSha256 != rehearsal.BundleSha256)
            throw new GitHubRemoteRefFenceException("RemoteFenceReadinessStale");
        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        var page = await reconciliations.PageAsync(
            preparationId, planId, review.ReconciliationId, null, 1, ct)
            ?? throw new KeyNotFoundException("RefReconciliationNotFound");
        if (!plan.IsCurrent || !page.Reconciliation.IsCurrent ||
            plan.ManifestSha256 != rehearsal.ManifestSha256 ||
            page.Reconciliation.ReconciliationSha256 != rehearsal.ReconciliationSha256 ||
            page.Reconciliation.TargetEvidenceSha256 != review.TargetEvidenceSha256 ||
            page.Reconciliation.DivergedCount != 0)
            throw new GitHubRemoteRefFenceException("RemoteFenceLineageStale");

        var bundle = await bundles.GetAsync(
            preparationId, planId, rehearsal.WorkpackId, ct)
            ?? throw new GitHubRemoteRefFenceException("RemoteFenceBundleMissing");
        if (!bundle.IsCurrent ||
            bundle.BundleSha256 != rehearsal.BundleSha256)
            throw new GitHubRemoteRefFenceException("RemoteFenceBundleChanged");
        var actualBundle = await disk.VerifyAsync(bundle, ct);
        if (!actualBundle.IsCurrent || actualBundle.ReadyForExecution ||
            actualBundle.BundleSha256 != rehearsal.BundleSha256)
            throw new GitHubRemoteRefFenceException("RemoteFenceDiskMismatch");

        var baseline = await LoadBaselineAsync(targetEvidence, preparationId,
            planId, page.Reconciliation.TargetObservationId,
            review.TargetEvidenceSha256, ct);
        var destination = await connections.GetAsync(
            plan.DestinationConnectionId, ct)
            ?? throw new GitHubRemoteRefFenceException("RemoteFenceDestinationMissing");
        if (destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            !string.Equals(destination.Organization, review.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubRemoteRefFenceException("RemoteFenceDestinationChanged");
        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GitHubRemoteRefFenceException("RemoteFenceCredentialMissing");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var observed = await reader.ReadAsync(
            review.Organization, review.TargetRepositoryName,
            token, timeout.Token);
        var resealed = GitHubRefObservationIntegrity.Seal(
            observed.Organization, observed.Repository, observed.ObservedAtUtc,
            observed.Refs);
        if (!string.Equals(observed.Organization, review.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observed.Repository, review.TargetRepositoryName,
                StringComparison.OrdinalIgnoreCase) ||
            observed.EvidenceSha256 != resealed.EvidenceSha256)
            throw new GitHubRemoteRefFenceException("RemoteFenceProviderMismatch");
        var age = clock.GetUtcNow() - observed.ObservedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) ||
            age > GitHubRemoteRefFenceIntegrity.MaximumAge)
            throw new GitHubRemoteRefFenceException("RemoteFenceObservationStale");
        var comparison = GitHubRemoteRefFenceIntegrity.Compare(
            baseline, observed);
        var input = new GitHubRemoteRefFenceInput(
            preparationId, planId, rehearsal.WorkpackId,
            intent.Id, rehearsal.Id, page.Reconciliation.TargetObservationId,
            plan.DestinationConnectionId, destination.ValidatedAtUtc,
            plan.ManifestSha256, rehearsal.ReconciliationSha256,
            rehearsal.BundleSha256, intent.EvidenceSha256,
            rehearsal.EvidenceSha256, review.TargetEvidenceSha256,
            observed.EvidenceSha256, comparison, actorId);
        // Source-scoped PostgreSQL transaction rechecks lineage AND bundle
        // after the network GET. A new remote ref is not silently accepted.
        return await store.SaveAsync(input, ct);
    }

    public static async Task<IReadOnlyList<GitHubObservedRef>> LoadBaselineAsync(
        IGitHubTargetRefEvidenceStore targets,
        Guid prepId, Guid planId, Guid targetObservationId,
        string expectedSha, CancellationToken ct)
    {
        var items = new List<GitHubObservedRef>();
        string? after = null;
        for (var i = 0; i < 251; i++)
        {
            var page = await targets.PageAsync(
                prepId, planId, targetObservationId, after, 100, ct)
                ?? throw new GitHubRemoteRefFenceException("RemoteFenceTargetProofMissing");
            if (!page.Evidence.IsCurrent ||
                page.Evidence.EvidenceSha256 != expectedSha ||
                page.Evidence.ReadyForExecution)
                throw new GitHubRemoteRefFenceException("RemoteFenceTargetProofStale");
            items.AddRange(page.Items);
            if (items.Count > GitMirrorRefReconciler.MaxRefsPerSide)
                throw new GitHubRemoteRefFenceException("RemoteFenceRefLimit");
            if (page.NextAfter is null)
            {
                if (items.Count != page.Evidence.RefCount)
                    throw new GitHubRemoteRefFenceException("RemoteFenceIncompleteBaseline");
                return items;
            }
            if (after == page.NextAfter || i == 250)
                throw new GitHubRemoteRefFenceException("RemoteFencePaginationInvalid");
            after = page.NextAfter;
        }
        throw new GitHubRemoteRefFenceException("RemoteFencePaginationInvalid");
    }

    public Task<GitHubRemoteRefFenceView?> GetAsync(
        Guid prepId, Guid planId, Guid fenceId, CancellationToken ct) =>
        store.GetAsync(prepId, planId, fenceId, ct);

    public Task<IReadOnlyList<GitHubRemoteRefFenceView>> ListAsync(
        Guid prepId, Guid planId, Guid rehearsalId, CancellationToken ct) =>
        store.ListAsync(prepId, planId, rehearsalId, ct);
}
