using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-044: deterministic, transport-free CREATE-ONLY candidate packet.
// The only output is inert evidence. This is NOT a GitHub ref reservation,
// proof of effective push privileges, or authorization to execute a push.
public sealed record GitHubCreateOnlyPacket(
    Guid PreparationId, Guid PlanId, Guid ScopeId, Guid FenceId,
    Guid IntentId, Guid RehearsalId, Guid WorkpackId, long RepositoryId,
    string Organization, string Repository, string ManifestSha256,
    string BundleSha256, string ReconciliationSha256, string ScopeEvidenceSha256,
    string FenceEvidenceSha256, string IntentEvidenceSha256,
    string RehearsalEvidenceSha256, string PolicySha256,
    string SafeRefPlanSha256, string CandidateSetSha256, string EvidenceSha256,
    int CreateCandidates, int AlreadyMatching, int TargetOnlyPreserved,
    string State, bool WriteCapabilityVerified, bool ExecutionAuthorized,
    bool RemoteWritesPerformed, bool ReadyForExecution);

public sealed class GitHubCreateOnlyPacketException(string code)
    : Exception("GitHub create-only execution packet rejected")
{
    public string Code { get; } = code;
}

public static class GitHubCreateOnlyPacketIntegrity
{
    public const string State = "ReviewOnlyBlocked";

    public static GitHubCreateOnlyPacket Build(
        GitHubAppWriteScopeView scope, GitHubRemoteRefFenceView fence,
        GitHubImportIntentView intent, GitImportRehearsalView rehearsal,
        IncrementalOperationPlan plan, GitHubImportReadinessView readiness,
        GitSourceBundleView bundle, IReadOnlyList<GitMirrorRefComparison> refs)
    {
        if (scope is null || fence is null || intent is null ||
            rehearsal is null || plan is null || readiness is null ||
            bundle is null || refs is null)
            throw new GitHubCreateOnlyPacketException("PacketInputMissing");
        if (!scope.IsCurrent || !scope.ScopedContentsWriteReported ||
            scope.Status != GitHubAppWriteScopeIntegrity.Issued ||
            scope.RepositoryId <= 0 || scope.InstallationId <= 0 ||
            scope.WriteCapabilityVerified || scope.ExecutionAuthorized ||
            scope.RemoteWritesPerformed || scope.ReadyForExecution ||
            !fence.IsCurrent || fence.Status != "RefSetUnchanged" ||
            fence.ChangedRefs != 0 || fence.WriteCapabilityVerified ||
            fence.ExecutionAuthorized || fence.RemoteWritesPerformed ||
            fence.ReadyForExecution || !intent.IsCurrent || intent.IsExpired ||
            intent.State != GitHubImportIntentStates.Acknowledged ||
            intent.WriteCapabilityVerified || intent.ExecutionAuthorized ||
            intent.ReadyForExecution || !rehearsal.IsCurrent ||
            rehearsal.DestinationWritesPerformed || rehearsal.ExecutionAuthorized ||
            rehearsal.ReadyForExecution || !plan.IsCurrent ||
            plan.ReadyForExecution || !readiness.IsCurrent ||
            readiness.WriteCapabilityVerified || readiness.ReadyForExecution ||
            !bundle.IsCurrent || bundle.ReadyForExecution)
            throw new GitHubCreateOnlyPacketException("PacketDependencyStale");

        if (scope.PreparationId != plan.PreparationId || scope.PlanId != plan.Id ||
            fence.PreparationId != scope.PreparationId ||
            fence.PlanId != scope.PlanId || scope.FenceId != fence.Id ||
            scope.WorkpackId != fence.WorkpackId ||
            scope.WorkpackId != intent.WorkpackId ||
            scope.WorkpackId != rehearsal.WorkpackId ||
            scope.WorkpackId != bundle.WorkpackId ||
            scope.IntentId != intent.Id || fence.IntentId != intent.Id ||
            rehearsal.IntentId != intent.Id ||
            scope.RehearsalId != rehearsal.Id || fence.RehearsalId != rehearsal.Id ||
            readiness.Id != intent.ImportReadinessId ||
            readiness.PlanId != plan.Id ||
            readiness.WorkpackId != bundle.WorkpackId ||
            readiness.ReconciliationId != bundle.ReconciliationId ||
            bundle.PlanId != plan.Id ||
            scope.ActorId != intent.ReviewedBy ||
            intent.RequestedBy == scope.ActorId || scope.ActorId == Guid.Empty ||
            !string.Equals(scope.Organization, readiness.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(scope.Repository, readiness.TargetRepositoryName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.Organization, scope.Organization,
                StringComparison.OrdinalIgnoreCase))
            throw new GitHubCreateOnlyPacketException("PacketLineageMismatch");

        if (readiness.RepositoryStatus != "Visible" ||
            readiness.ReportedPush != "ReportedAllowed" ||
            readiness.Archived || readiness.Disabled ||
            !GitSourceBundleStorageCheck.ValidSha(scope.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(fence.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(intent.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(rehearsal.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(intent.PolicySha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.ManifestSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(bundle.BundleSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(readiness.ReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(scope.RemoteRefFingerprintSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(intent.SafeRefPlanSha256) ||
            scope.ManifestSha256 != plan.ManifestSha256 ||
            fence.ManifestSha256 != plan.ManifestSha256 ||
            intent.ManifestSha256 != plan.ManifestSha256 ||
            rehearsal.ManifestSha256 != plan.ManifestSha256 ||
            readiness.ManifestSha256 != plan.ManifestSha256 ||
            scope.BundleSha256 != bundle.BundleSha256 ||
            fence.BundleSha256 != bundle.BundleSha256 ||
            intent.BundleSha256 != bundle.BundleSha256 ||
            rehearsal.BundleSha256 != bundle.BundleSha256 ||
            readiness.SourceBundleSha256 != bundle.BundleSha256 ||
            scope.FenceEvidenceSha256 != fence.EvidenceSha256 ||
            scope.IntentEvidenceSha256 != intent.EvidenceSha256 ||
            scope.RehearsalEvidenceSha256 != rehearsal.EvidenceSha256 ||
            scope.RemoteRefFingerprintSha256 != fence.RemoteRefFingerprintSha256 ||
            fence.IntentEvidenceSha256 != intent.EvidenceSha256 ||
            fence.RehearsalEvidenceSha256 != rehearsal.EvidenceSha256 ||
            fence.ReconciliationSha256 != readiness.ReconciliationSha256 ||
            fence.TargetEvidenceSha256 != readiness.TargetEvidenceSha256 ||
            intent.ReconciliationSha256 != readiness.ReconciliationSha256 ||
            rehearsal.ReconciliationSha256 != readiness.ReconciliationSha256 ||
            bundle.ReconciliationSha256 != readiness.ReconciliationSha256 ||
            intent.ImportReadinessSha256 != readiness.EvidenceSha256 ||
            intent.SafeRefPlanSha256 != rehearsal.SafeRefPlanSha256)
            throw new GitHubCreateOnlyPacketException("PacketLineageMismatch");

        if (refs.Count is < 1 or > 50_000)
            throw new GitHubCreateOnlyPacketException("PacketRefSetInvalid");
        GitHubSafeRefPlan safe;
        try
        {
            safe = GitHubSafeRefPlanner.Build(plan.Id, plan.ManifestSha256,
                readiness.ReconciliationSha256, bundle.BundleSha256, refs);
        }
        catch (GitHubImportIntentException)
        { throw new GitHubCreateOnlyPacketException("PacketRefSetInvalid"); }
        if (safe.EvidenceSha256 != intent.SafeRefPlanSha256 ||
            !safe.HasOnlyNonDestructiveCandidates || safe.DivergedBlocked != 0 ||
            safe.CreateCandidates is < 1 or > GitImportRehearsalIntegrity.MaxCandidateRefs ||
            safe.CreateCandidates != intent.CreateCandidates ||
            safe.CreateCandidates != rehearsal.CreateCandidates ||
            safe.CreateCandidates != rehearsal.DestinationCreated ||
            safe.AlreadyMatching != intent.AlreadyMatching ||
            safe.AlreadyMatching != rehearsal.AlreadyMatching ||
            safe.TargetOnlyPreserved != intent.TargetOnlyPreserved ||
            safe.TargetOnlyPreserved != rehearsal.TargetOnlyNotMaterialized ||
            readiness.DivergedRefs != 0 ||
            readiness.TargetOnlyRefs != safe.TargetOnlyPreserved)
            throw new GitHubCreateOnlyPacketException("PacketRefPlanChanged");

        // The packet must agree with the exact refs that real Git wrote
        // inside F-041's disposable local bare destination.
        var sourceRefs = refs.Where(x => x.SourceObjectId is not null)
            .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
        var creates = refs.Where(x => x.Disposition ==
            GitMirrorRefDisposition.CreateCandidate)
            .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
        try
        {
            if (!GitMirrorRefReconciler.CanonicalizeObservedRefs(sourceRefs)
                    .SequenceEqual(sourceRefs) ||
                !GitMirrorRefReconciler.CanonicalizeObservedRefs(creates)
                    .SequenceEqual(creates) ||
                sourceRefs.Count() != bundle.RefCount ||
                sourceRefs.Select(x => x.ObjectId.Length).Distinct().Count() != 1)
                throw new GitHubCreateOnlyPacketException("PacketRefSetInvalid");
        }
        catch (GitMirrorRefReconciliationException)
        { throw new GitHubCreateOnlyPacketException("PacketRefSetInvalid"); }

        if (GitImportRehearsalIntegrity.SealRefs(
                "f041-created-refs-v1", creates) != rehearsal.CreatedRefsSha256 ||
            GitImportRehearsalIntegrity.SealRefs(
                "f041-source-refs-v1", sourceRefs) != rehearsal.VerifiedSourceRefsSha256)
            throw new GitHubCreateOnlyPacketException("PacketRehearsalChanged");

        var candidateSha = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", creates);
        // No clock-dependent value: repeatable for identical persisted
        // proofs and candidate refs. No token, key, path, URL or Git args.
        var stable = JsonSerializer.Serialize(new object?[] {
            "f044-create-only-packet-v1", scope.PreparationId.ToString("D"),
            plan.Id.ToString("D"), scope.Id.ToString("D"), fence.Id.ToString("D"),
            intent.Id.ToString("D"), rehearsal.Id.ToString("D"),
            bundle.WorkpackId.ToString("D"), plan.DestinationConnectionId.ToString("D"),
            scope.RepositoryId, scope.InstallationId,
            scope.Organization.ToLowerInvariant(), scope.Repository.ToLowerInvariant(),
            plan.ManifestSha256, bundle.BundleSha256, readiness.ReconciliationSha256,
            scope.EvidenceSha256, fence.EvidenceSha256, intent.EvidenceSha256,
            rehearsal.EvidenceSha256, intent.PolicySha256, intent.SafeRefPlanSha256,
            fence.RemoteRefFingerprintSha256, candidateSha, safe.CreateCandidates,
            safe.AlreadyMatching, safe.TargetOnlyPreserved,
            State, false, false, false, false
        });
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(stable)));
        return new(scope.PreparationId, plan.Id, scope.Id, fence.Id, intent.Id,
            rehearsal.Id, bundle.WorkpackId, scope.RepositoryId,
            scope.Organization, scope.Repository, plan.ManifestSha256,
            bundle.BundleSha256, readiness.ReconciliationSha256,
            scope.EvidenceSha256, fence.EvidenceSha256, intent.EvidenceSha256,
            rehearsal.EvidenceSha256, intent.PolicySha256, intent.SafeRefPlanSha256,
            candidateSha, digest, safe.CreateCandidates,
            safe.AlreadyMatching, safe.TargetOnlyPreserved, State,
            false, false, false, false);
    }
}

// Independently regenerates the packet from server-side immutable evidence;
// no body-supplied ref names, remote URLs, Git commands or credentials.
public sealed class GitHubCreateOnlyPacketService(
    IGitHubAppWriteScopeStore scopes, IGitHubRemoteRefFenceStore fences,
    IGitHubImportIntentStore intents, IGitHubImportReadinessStore readiness,
    IGitImportRehearsalStore rehearsals, IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier disk,
    IIncrementalGitRefReconciliationStore reconciliations,
    IIncrementalOperationPlanRepository plans, TimeProvider clock)
{
    public async Task<GitHubCreateOnlyPacket> PreviewAsync(
        Guid preparationId, Guid planId, Guid scopeId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            scopeId == Guid.Empty)
            throw new GitHubCreateOnlyPacketException("PacketRequestInvalid");

        var scope = await scopes.GetAsync(preparationId, planId, scopeId, ct)
            ?? throw new KeyNotFoundException("AppScopeNotFound");
        var fence = await fences.GetAsync(preparationId, planId, scope.FenceId, ct)
            ?? throw new KeyNotFoundException("RemoteFenceNotFound");
        var intent = await intents.GetAsync(preparationId, planId, scope.IntentId, ct)
            ?? throw new KeyNotFoundException("ImportIntentNotFound");
        var rehearsal = await rehearsals.GetAsync(
            preparationId, planId, scope.RehearsalId, ct)
            ?? throw new KeyNotFoundException("RehearsalNotFound");
        var review = await readiness.GetAsync(
            preparationId, planId, intent.ImportReadinessId, ct)
            ?? throw new KeyNotFoundException("ImportReadinessNotFound");
        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        var bundle = await bundles.GetAsync(
            preparationId, planId, scope.WorkpackId, ct)
            ?? throw new KeyNotFoundException("SourceBundleNotFound");

        // Avoid a disk scan for already-stale/rejected attestations. Recheck
        // physical bytes both before and after reading the full F-032 pages.
        if (!scope.IsCurrent || !fence.IsCurrent || !intent.IsCurrent ||
            !rehearsal.IsCurrent || !plan.IsCurrent || !review.IsCurrent ||
            !bundle.IsCurrent)
            throw new GitHubCreateOnlyPacketException("PacketDependencyStale");
        EnsureFresh(scope, fence, intent, clock.GetUtcNow());
        await VerifyDiskAsync(bundle, disk, ct);

        var refs = new List<GitMirrorRefComparison>();
        string? after = null;
        var completed = false;
        for (var i = 0; i < 501; i++)
        {
            var page = await reconciliations.PageAsync(preparationId,
                planId, review.ReconciliationId, after, 100, ct)
                ?? throw new GitHubCreateOnlyPacketException("PacketReconciliationMissing");
            if (!page.Reconciliation.IsCurrent ||
                page.Reconciliation.Id != review.ReconciliationId ||
                page.Reconciliation.ReconciliationSha256 != review.ReconciliationSha256 ||
                page.Reconciliation.TargetEvidenceSha256 != review.TargetEvidenceSha256 ||
                page.Reconciliation.ManifestSha256 != plan.ManifestSha256)
                throw new GitHubCreateOnlyPacketException("PacketReconciliationChanged");
            refs.AddRange(page.Items);
            if (refs.Count > 50_000)
                throw new GitHubCreateOnlyPacketException("PacketRefSetInvalid");
            if (page.NextAfter is null)
            { completed = true; break; }
            if (i == 500 || string.CompareOrdinal(page.NextAfter, after) <= 0)
                throw new GitHubCreateOnlyPacketException("PacketPaginationInvalid");
            after = page.NextAfter;
        }
        if (!completed)
            throw new GitHubCreateOnlyPacketException("PacketPaginationInvalid");

        await VerifyDiskAsync(bundle, disk, ct);
        // Fence and human-intent expiry can pass during 2x full disk hashing
        // or while paginating a large comparison. Refuse a stale response.
        EnsureFresh(scope, fence, intent, clock.GetUtcNow());
        var latestScope = await scopes.GetAsync(preparationId, planId, scopeId, ct);
        var latestFence = await fences.GetAsync(preparationId, planId, fence.Id, ct);
        var latestIntent = await intents.GetAsync(preparationId, planId, intent.Id, ct);
        var latestPlan = await plans.GetAsync(preparationId, planId, ct);
        if (latestScope is not { IsCurrent: true } ||
            latestFence is not { IsCurrent: true } ||
            latestIntent is not { IsCurrent: true, IsExpired: false } ||
            latestPlan is not { IsCurrent: true } ||
            latestScope.EvidenceSha256 != scope.EvidenceSha256 ||
            latestFence.EvidenceSha256 != fence.EvidenceSha256 ||
            latestIntent.EvidenceSha256 != intent.EvidenceSha256 ||
            latestPlan.ManifestSha256 != plan.ManifestSha256)
            throw new GitHubCreateOnlyPacketException("PacketDependencyStale");

        // This method is read-only: these rechecks do NOT constitute a
        // transaction/lease over external GitHub state.
        return GitHubCreateOnlyPacketIntegrity.Build(
            latestScope, latestFence, latestIntent,
            rehearsal, latestPlan, review, bundle, refs);

    }

    private static void EnsureFresh(
        GitHubAppWriteScopeView scope, GitHubRemoteRefFenceView fence,
        GitHubImportIntentView intent, DateTimeOffset now)
    {
        if (now >= fence.ExpiresAtUtc || now >= intent.ExpiresAtUtc ||
            now >= scope.TokenExpiresAtUtc || now < fence.ObservedAtUtc.AddSeconds(-5))
            throw new GitHubCreateOnlyPacketException("PacketDependencyStale");
    }

    private static async Task VerifyDiskAsync(
        GitSourceBundleView bundle, IGitSourceBundleArtifactVerifier disk,
        CancellationToken ct)
    {
        var verified = await disk.VerifyAsync(bundle, ct);
        if (!verified.IsCurrent || verified.ReadyForExecution ||
            verified.WorkpackId != bundle.WorkpackId ||
            verified.BundleSha256 != bundle.BundleSha256 ||
            verified.BundleBytes != bundle.BundleBytes)
            throw new GitHubCreateOnlyPacketException("PacketBundleChanged");
    }
}
