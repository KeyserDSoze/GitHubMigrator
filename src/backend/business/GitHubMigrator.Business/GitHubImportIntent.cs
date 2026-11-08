using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// Policy signals are GET-only and advisory. A 404 from protection lookup does
// NOT prove there are no applicable organization rules or hidden policies.
public sealed record GitHubImportPolicyObservation(
    string RulesetStatus, int RulesetCount, int ActiveRulesets,
    string DefaultBranchProtection, DateTimeOffset ObservedAtUtc);

public interface IGitHubImportPolicyReader
{
    Task<GitHubImportPolicyObservation> InspectAsync(
        string organization, string repository, string token, CancellationToken ct);
}

public sealed record GitHubSafeRefPlan(
    string EvidenceSha256, int CreateCandidates, int AlreadyMatching,
    int DivergedBlocked, int TargetOnlyPreserved, bool HasOnlyNonDestructiveCandidates);

public static class GitHubSafeRefPlanner
{
    public static GitHubSafeRefPlan Build(
        Guid planId, string manifestSha256, string reconciliationSha256,
        string bundleSha256, IReadOnlyList<GitMirrorRefComparison> items)
    {
        if (planId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(manifestSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(reconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(bundleSha256) ||
            items is null || items.Count is < 1 or > 50_000)
            throw new GitHubImportIntentException("ImportIntentRefPlanInvalid");
        var created = 0; var matching = 0; var diverged = 0; var preserved = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(object?[] value) => hash.AppendData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"));
        Append(["github-safe-ref-intent-v1", planId.ToString("D"),
            manifestSha256, reconciliationSha256, bundleSha256, items.Count]);

        string? last = null;
        foreach (var item in items)
        {
            if (last is not null && string.CompareOrdinal(last, item.Name) >= 0 ||
                item.Name.Length > 255 || !item.Name.StartsWith("refs/", StringComparison.Ordinal))
                throw new GitHubImportIntentException("ImportIntentRefPlanInvalid");
            last = item.Name;
            switch (item.Disposition)
            {
                case GitMirrorRefDisposition.CreateCandidate
                    when item.SourceObjectId is not null &&
                         ValidGitOid(item.SourceObjectId) && item.TargetObjectId is null:
                    created++; break;
                case GitMirrorRefDisposition.MatchingObjectId
                    when item.SourceObjectId is not null &&
                         item.SourceObjectId == item.TargetObjectId &&
                         ValidGitOid(item.SourceObjectId):
                    matching++; break;
                case GitMirrorRefDisposition.DivergedBlocked
                    when item.SourceObjectId is not null && item.TargetObjectId is not null &&
                         ValidGitOid(item.SourceObjectId) && ValidGitOid(item.TargetObjectId) &&
                         item.SourceObjectId != item.TargetObjectId:
                    diverged++; break;
                case GitMirrorRefDisposition.TargetOnlyPreserved
                    when item.SourceObjectId is null && item.TargetObjectId is not null &&
                         ValidGitOid(item.TargetObjectId):
                    preserved++; break;
                default: throw new GitHubImportIntentException("ImportIntentRefPlanInvalid");
            }
            // A plan contains dispositions, NOT commands or remote write URLs.
            Append([item.Name, item.SourceObjectId, item.TargetObjectId,
                item.Disposition.ToString()]);
        }
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()),
            created, matching, diverged, preserved, diverged == 0);
    }

    private static bool ValidGitOid(string value) =>
        value.Length is 40 or 64 &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class GitHubImportIntentStates
{
    public const string Pending = "Pending";
    public const string Acknowledged = "Acknowledged";
    public const string Rejected = "Rejected";
}

public sealed class GitHubImportIntentException(string code)
    : Exception("GitHub import intent rejected")
{
    public string Code { get; } = code;
}

// Pending/Acknowledged are human review states, NEVER execution grants.
public sealed record GitHubImportIntentView(
    Guid Id, Guid PreparationId, Guid PlanId, Guid WorkpackId,
    Guid ImportReadinessId, string ImportReadinessSha256,
    string ManifestSha256, string ReconciliationSha256, string BundleSha256,
    string SafeRefPlanSha256, string PolicySha256, string EvidenceSha256,
    int CreateCandidates, int AlreadyMatching, int DivergedBlocked,
    int TargetOnlyPreserved, string RulesetStatus, int RulesetCount,
    int ActiveRulesets, string DefaultBranchProtection,
    string State, Guid RequestedBy, Guid? ReviewedBy,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ReviewedAtUtc, bool IsCurrent, bool IsExpired,
    bool WriteCapabilityVerified, bool ExecutionAuthorized,
    bool ReadyForExecution);

public sealed record GitHubImportIntentEvent(
    long Id, Guid IntentId, string Kind, Guid ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record GitHubImportIntentDraft(
    Guid PreparationId, Guid PlanId, Guid WorkpackId, Guid ImportReadinessId,
    string ExpectedReadinessSha256, string ManifestSha256,
    string ReconciliationSha256, string BundleSha256,
    GitHubSafeRefPlan Plan, GitHubImportPolicyObservation Policy,
    Guid RequestedBy);

public interface IGitHubImportIntentStore
{
    Task<GitHubImportIntentView> CreateAsync(
        GitHubImportIntentDraft draft, CancellationToken ct);
    Task<GitHubImportIntentView> DecideAsync(
        Guid preparationId, Guid planId, Guid intentId, Guid actorId,
        string expectedReadinessSha256, string decision,
        CancellationToken ct);
    Task<GitHubImportIntentView?> GetAsync(
        Guid preparationId, Guid planId, Guid intentId, CancellationToken ct);
    Task<IReadOnlyList<GitHubImportIntentView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct);
    Task<IReadOnlyList<GitHubImportIntentEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid intentId, CancellationToken ct);
}

public sealed class GitHubImportIntentService(
    IGitHubImportReadinessStore readiness,
    IGitTransferWorkpackRepository workpacks,
    IIncrementalGitRefReconciliationStore comparisons,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk,
    IIncrementalOperationPlanRepository plans,
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IGitHubImportPolicyReader policyReader,
    IGitHubImportIntentStore store)
{
    public static readonly TimeSpan IntentTtl = TimeSpan.FromMinutes(10);

    public async Task<GitHubImportIntentView> CreateAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        Guid readinessId, string expectedReadinessSha256, Guid actorId,
        CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            workpackId == Guid.Empty || readinessId == Guid.Empty ||
            actorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(expectedReadinessSha256))
            throw new GitHubImportIntentException("InvalidImportIntentRequest");

        var review = await readiness.GetAsync(preparationId, planId, readinessId, ct)
            ?? throw new KeyNotFoundException("ImportReadinessNotFound");
        if (!review.IsCurrent || review.WorkpackId != workpackId ||
            review.EvidenceSha256 != expectedReadinessSha256 ||
            review.RepositoryStatus != "Visible" ||
            review.Archived || review.Disabled ||
            review.ReportedPush != "ReportedAllowed")
            throw new GitHubImportIntentException("ImportIntentReadinessNotCurrent");
        var pack = await workpacks.GetAsync(preparationId, planId, workpackId, ct)
            ?? throw new KeyNotFoundException("TransferWorkpackNotFound");
        if (!pack.IsCurrent || pack.ReconciliationSha256 != review.ReconciliationSha256 ||
            pack.State != GitTransferWorkpackStates.Blocked ||
            pack.Phase != GitTransferWorkpackPhases.WriteBlocked)
            throw new GitHubImportIntentException("ImportIntentWorkpackNotCurrent");
        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        if (!plan.IsCurrent || plan.ManifestSha256 != review.ManifestSha256)
            throw new GitHubImportIntentException("ImportIntentPlanNotCurrent");
        var bundle = await bundles.GetAsync(preparationId, planId, workpackId, ct)
            ?? throw new GitHubImportIntentException("ImportIntentBundleMissing");
        if (!bundle.IsCurrent || bundle.BundleSha256 != review.SourceBundleSha256)
            throw new GitHubImportIntentException("ImportIntentBundleChanged");
        await disk.VerifyAsync(bundle, ct);

        var safe = await BuildVerifiedPlanAsync(
            comparisons, preparationId, planId, pack.ReconciliationId,
            review, ct);
        if (!safe.HasOnlyNonDestructiveCandidates || safe.CreateCandidates < 1)
            throw new GitHubImportIntentException("ImportIntentNoSafeCreateCandidates");

        // Repeated HTTP submissions of one currently valid review return
        // the original immutable intent, without a fresh policy GET (which
        // would legitimately have a different observation timestamp).
        var existing = (await store.ListAsync(
            preparationId, planId, workpackId, ct))
            .SingleOrDefault(x => x.ImportReadinessId == readinessId);
        if (existing is not null)
        {
            if (!existing.IsCurrent || existing.IsExpired ||
                existing.RequestedBy != actorId ||
                existing.ImportReadinessSha256 != expectedReadinessSha256 ||
                existing.ManifestSha256 != review.ManifestSha256 ||
                existing.ReconciliationSha256 != review.ReconciliationSha256 ||
                existing.BundleSha256 != review.SourceBundleSha256 ||
                existing.SafeRefPlanSha256 != safe.EvidenceSha256)
                throw new GitHubImportIntentException("ImportIntentConflictingReplay");
            return existing;
        }

        var destination = await connections.GetAsync(plan.DestinationConnectionId, ct)
            ?? throw new GitHubImportIntentException("ImportIntentDestinationMissing");
        if (destination.Direction != GitHubMigrator.Domain.ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            destination.Organization != review.Organization)
            throw new GitHubImportIntentException("ImportIntentDestinationChanged");
        var credential = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))
            throw new GitHubImportIntentException("ImportIntentCredentialMissing");

        GitHubImportPolicyObservation observation;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                observation = await policyReader.InspectAsync(
                    review.Organization, review.TargetRepositoryName,
                    credential, timeout.Token);
            }
            catch (HttpRequestException)
            {
                observation = new("Unavailable", 0, 0, "Unverified",
                    DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                observation = new("Unavailable", 0, 0, "Unverified",
                    DateTimeOffset.UtcNow);
            }
        }
        return await store.CreateAsync(new(preparationId, planId, workpackId,
            readinessId, expectedReadinessSha256, review.ManifestSha256,
            review.ReconciliationSha256, review.SourceBundleSha256,
            safe, observation, actorId), ct);
    }

    public Task<GitHubImportIntentView> DecideAsync(
        Guid preparationId, Guid planId, Guid intentId, Guid actorId,
        string expectedReadinessSha256, string decision, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            intentId == Guid.Empty || actorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(expectedReadinessSha256) ||
            decision is not ("Acknowledge" or "Reject"))
            throw new GitHubImportIntentException("InvalidImportIntentDecision");
        return store.DecideAsync(preparationId, planId, intentId,
            actorId, expectedReadinessSha256, decision, ct);
    }

    public Task<GitHubImportIntentView?> GetAsync(
        Guid preparationId, Guid planId, Guid intentId, CancellationToken ct) =>
        store.GetAsync(preparationId, planId, intentId, ct);

    public Task<IReadOnlyList<GitHubImportIntentView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct) =>
        store.ListAsync(preparationId, planId, workpackId, ct);

    public Task<IReadOnlyList<GitHubImportIntentEvent>> EventsAsync(
        Guid preparationId, Guid planId, Guid intentId, CancellationToken ct) =>
        store.EventsAsync(preparationId, planId, intentId, ct);

    public static async Task<GitHubSafeRefPlan> BuildVerifiedPlanAsync(
        IIncrementalGitRefReconciliationStore comparisons,
        Guid preparationId, Guid planId, Guid reconciliationId,
        GitHubImportReadinessView review, CancellationToken ct)
    {
        const int batchSize = 100;
        string? after = null;
        var items = new List<GitMirrorRefComparison>();
        for (var page = 0; page < 501; page++)
        {
            var result = await comparisons.PageAsync(
                preparationId, planId, reconciliationId, after, batchSize, ct)
                ?? throw new GitHubImportIntentException("ImportIntentReconciliationMissing");
            if (!result.Reconciliation.IsCurrent ||
                result.Reconciliation.ReconciliationSha256 != review.ReconciliationSha256 ||
                result.Reconciliation.TargetEvidenceSha256 != review.TargetEvidenceSha256 ||
                result.Reconciliation.ManifestSha256 != review.ManifestSha256 ||
                result.Reconciliation.Id != reconciliationId)
                throw new GitHubImportIntentException("ImportIntentReconciliationChanged");
            items.AddRange(result.Items);
            if (items.Count > 50_000)
                throw new GitHubImportIntentException("ImportIntentRefLimit");
            if (result.NextAfter is null) break;
            if (result.NextAfter == after || page == 500)
                throw new GitHubImportIntentException("ImportIntentRefPaginationInvalid");
            after = result.NextAfter;
        }
        var safe = GitHubSafeRefPlanner.Build(planId,
            review.ManifestSha256, review.ReconciliationSha256,
            review.SourceBundleSha256, items);
        var firstPage = await comparisons.PageAsync(
            preparationId, planId, reconciliationId, null, 1, ct);
        if (firstPage is null ||
            safe.CreateCandidates != firstPage.Reconciliation.CreateCandidateCount ||
            safe.AlreadyMatching != firstPage.Reconciliation.MatchingCount ||
            safe.DivergedBlocked != firstPage.Reconciliation.DivergedCount ||
            safe.TargetOnlyPreserved != firstPage.Reconciliation.TargetOnlyCount)
            throw new GitHubImportIntentException("ImportIntentRefCountsChanged");
        return safe;
    }
}
