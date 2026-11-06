using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Business;

// F-039 is a READ-ONLY, NON-AUTHORIZING destination review. A repository's
// reported "permissions.push" is NOT a verified Contents:write token scope,
// and GitHub rulesets/branch protection can still prevent a Git push.
public sealed record GitHubImportRepositoryObservation(
    string Status, string ReportedPush, bool Archived, bool Disabled,
    DateTimeOffset ObservedAtUtc);

public interface IGitHubImportRepositoryReader
{
    Task<GitHubImportRepositoryObservation> ObserveAsync(
        string organization, string repository, string token, CancellationToken ct);
}

public sealed record GitHubImportReadinessView(
    Guid Id, Guid PlanId, Guid WorkpackId, Guid ReconciliationId,
    string SourceRepositoryId, string TargetRepositoryName, string Organization,
    string ManifestSha256, string ReconciliationSha256, string TargetEvidenceSha256,
    string SourceBundleSha256, string EvidenceSha256, DateTimeOffset ObservedAtUtc,
    string RepositoryStatus, string ReportedPush, bool Archived, bool Disabled,
    int DivergedRefs, int TargetOnlyRefs, IReadOnlyList<string> Blockers,
    bool IsCurrent, bool WriteCapabilityVerified, bool ReadyForExecution);

public sealed record GitHubImportReadinessInput(
    Guid PreparationId, Guid PlanId, Guid WorkpackId, Guid ReconciliationId,
    string SourceRepositoryId, string TargetRepositoryName, string Organization,
    Guid DestinationConnectionId, DateTimeOffset? DestinationValidatedAtUtc,
    string ManifestSha256, string ReconciliationSha256, string TargetEvidenceSha256,
    string SourceBundleSha256, int DivergedRefs, int TargetOnlyRefs,
    GitHubImportRepositoryObservation Observation, Guid ActorId);

public sealed class GitHubImportReadinessException(string code)
    : Exception("GitHub import readiness review rejected")
{
    public string Code { get; } = code;
}

public static class GitHubImportReadinessIntegrity
{
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        "Visible", "NotVisibleOrMissing", "Unauthorized", "Forbidden",
        "RateLimited", "Unavailable", "InvalidResponse"
    };
    private static readonly HashSet<string> PushStates = new(StringComparer.Ordinal)
    {
        "ReportedAllowed", "ReportedDenied", "Unverified"
    };

    public static string Seal(GitHubImportReadinessInput input)
    {
        if (input.PlanId == Guid.Empty || input.WorkpackId == Guid.Empty ||
            input.ReconciliationId == Guid.Empty || input.DestinationConnectionId == Guid.Empty ||
            input.ActorId == Guid.Empty || string.IsNullOrWhiteSpace(input.Organization) ||
            string.IsNullOrWhiteSpace(input.SourceRepositoryId) ||
            string.IsNullOrWhiteSpace(input.TargetRepositoryName) ||
            !GitSourceBundleStorageCheck.ValidSha(input.ManifestSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.ReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.TargetEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.SourceBundleSha256) ||
            !Statuses.Contains(input.Observation.Status) ||
            !PushStates.Contains(input.Observation.ReportedPush) ||
            input.DivergedRefs < 0 || input.TargetOnlyRefs < 0 ||
            input.DivergedRefs > GitMirrorRefReconciler.MaxRefsPerSide ||
            input.TargetOnlyRefs > GitMirrorRefReconciler.MaxRefsPerSide ||
            input.Observation.ObservedAtUtc == default)
            throw new GitHubImportReadinessException("ImportReadinessEvidenceInvalid");

        var serialized = JsonSerializer.Serialize(new object?[]
        {
            "github-import-readiness-v1",
            input.PreparationId.ToString("D"), input.PlanId.ToString("D"),
            input.WorkpackId.ToString("D"), input.ReconciliationId.ToString("D"),
            input.SourceRepositoryId, input.TargetRepositoryName, input.Organization,
            input.DestinationConnectionId.ToString("D"),
            input.DestinationValidatedAtUtc?.ToUnixTimeMilliseconds(),
            input.ManifestSha256, input.ReconciliationSha256,
            input.TargetEvidenceSha256, input.SourceBundleSha256,
            input.DivergedRefs, input.TargetOnlyRefs,
            input.Observation.Status, input.Observation.ReportedPush,
            input.Observation.Archived, input.Observation.Disabled,
            input.Observation.ObservedAtUtc.ToUnixTimeMilliseconds()
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }

    public static IReadOnlyList<string> Blockers(GitHubImportReadinessInput input)
    {
        var blockers = new List<string>();
        if (input.Observation.Status != "Visible")
            blockers.Add("TargetRepositoryNotVerifiedVisible");
        if (input.Observation.Archived)
            blockers.Add("TargetRepositoryArchived");
        if (input.Observation.Disabled)
            blockers.Add("TargetRepositoryDisabled");
        if (input.Observation.ReportedPush != "ReportedAllowed")
            blockers.Add("RepositoryPushPermissionNotReported");
        if (input.DivergedRefs > 0) blockers.Add("DivergedRefsBlocked");
        if (input.TargetOnlyRefs > 0) blockers.Add("TargetOnlyRefsPreserved");
        blockers.Add("ContentsWriteScopeNotVerified");
        blockers.Add("BranchRulesNotVerified");
        blockers.Add("GitHubImportExecutorNotImplemented");
        blockers.Add("WriteAuthorizationNotGranted");
        return blockers;
    }
}

public interface IGitHubImportReadinessStore
{
    Task<GitHubImportReadinessView> SaveAsync(
        GitHubImportReadinessInput input, CancellationToken ct);
    Task<GitHubImportReadinessView?> GetAsync(
        Guid preparationId, Guid planId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<GitHubImportReadinessView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct);
}

public sealed class GitHubImportReadinessService(
    IIncrementalOperationPlanRepository plans,
    IGitTransferWorkpackRepository workpacks,
    IIncrementalGitRefReconciliationStore reconciliations,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier verifier,
    IConnectionRepository connections,
    IConnectionSecretStore secrets,
    IGitHubImportRepositoryReader reader,
    IGitHubImportReadinessStore store,
    TimeProvider clock)
{
    public async Task<GitHubImportReadinessView> CheckAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, string expectedBundleSha256,
        Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            workpackId == Guid.Empty || actorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(expectedReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(expectedBundleSha256))
            throw new GitHubImportReadinessException("InvalidImportReadinessRequest");

        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        var pack = await workpacks.GetAsync(preparationId, planId, workpackId, ct)
            ?? throw new KeyNotFoundException("TransferWorkpackNotFound");
        if (!plan.IsCurrent || !pack.IsCurrent ||
            pack.State != GitTransferWorkpackStates.Blocked ||
            pack.Phase != GitTransferWorkpackPhases.WriteBlocked ||
            pack.PlanId != planId || pack.ManifestSha256 != plan.ManifestSha256 ||
            pack.ReconciliationSha256 != expectedReconciliationSha256)
            throw new GitHubImportReadinessException("ImportReadinessWorkpackStale");

        var page = await reconciliations.PageAsync(
            preparationId, planId, pack.ReconciliationId, null, 1, ct)
            ?? throw new KeyNotFoundException("RefReconciliationNotFound");
        var comparison = page.Reconciliation;
        if (!comparison.IsCurrent ||
            comparison.ReconciliationSha256 != expectedReconciliationSha256 ||
            comparison.TargetEvidenceSha256 != pack.TargetEvidenceSha256 ||
            comparison.SourceRepositoryId != pack.SourceRepositoryId ||
            comparison.TargetRepositoryName != pack.TargetRepositoryName)
            throw new GitHubImportReadinessException("ImportReadinessComparisonStale");

        var bundle = await bundles.GetAsync(preparationId, planId, workpackId, ct)
            ?? throw new GitHubImportReadinessException("ImportReadinessBundleMissing");
        if (!bundle.IsCurrent ||
            bundle.ReconciliationSha256 != expectedReconciliationSha256 ||
            bundle.BundleSha256 != expectedBundleSha256 ||
            bundle.SourceEvidenceSha256 != comparison.SourceEvidenceSha256 ||
            bundle.RefCount != comparison.SourceRefCount)
            throw new GitHubImportReadinessException("ImportReadinessBundleMismatch");
        var disk = await verifier.VerifyAsync(bundle, ct);
        if (!disk.IsCurrent || disk.BundleSha256 != expectedBundleSha256 ||
            disk.BundleBytes != bundle.BundleBytes || disk.ReadyForExecution)
            throw new GitHubImportReadinessException("ImportReadinessDiskMismatch");

        var destination = await connections.GetAsync(plan.DestinationConnectionId, ct)
            ?? throw new GitHubImportReadinessException("ImportReadinessDestinationMissing");
        if (destination.Direction != ConnectionDirection.Destination ||
            destination.ProviderKey != "github-enterprise-cloud" ||
            destination.ValidationCode != "Verified" ||
            !string.Equals(destination.Organization, plan.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubImportReadinessException("ImportReadinessDestinationStale");
        var token = await secrets.ReadAsync(destination.SecretReference, ct);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new GitHubImportReadinessException("ImportReadinessCredentialMissing");

        GitHubImportRepositoryObservation observed;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                observed = await reader.ObserveAsync(
                    destination.Organization, comparison.TargetRepositoryName,
                    token, timeout.Token);
            }
            catch (HttpRequestException)
            { observed = new("Unavailable", "Unverified", false, false, clock.GetUtcNow()); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { observed = new("Unavailable", "Unverified", false, false, clock.GetUtcNow()); }
        }

        var input = new GitHubImportReadinessInput(
            preparationId, planId, workpackId, pack.ReconciliationId,
            pack.SourceRepositoryId, pack.TargetRepositoryName, plan.Organization,
            plan.DestinationConnectionId, destination.ValidatedAtUtc,
            plan.ManifestSha256, expectedReconciliationSha256,
            pack.TargetEvidenceSha256, expectedBundleSha256,
            comparison.DivergedCount, comparison.TargetOnlyCount,
            observed, actorId);
        _ = GitHubImportReadinessIntegrity.Seal(input);

        // SaveAsync repeats EVERY mutable proof check inside the original
        // source-scoped PostgreSQL lock after network GET; this cannot approve
        // any GitHub mutation or unlock the F-029/F-034 write barriers.
        return await store.SaveAsync(input, ct);
    }

    public Task<GitHubImportReadinessView?> GetAsync(
        Guid preparationId, Guid planId, Guid id, CancellationToken ct) =>
        store.GetAsync(preparationId, planId, id, ct);

    public Task<IReadOnlyList<GitHubImportReadinessView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct) =>
        store.ListAsync(preparationId, planId, workpackId, ct);
}
