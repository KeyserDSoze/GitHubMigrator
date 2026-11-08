using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-041 is a LOCAL, DISPOSABLE Git transport rehearsal, not a GitHub import.
// No PAT, GitHub remote URL or external target is accepted by this contract.
public sealed record GitImportRehearsalRequest(
    Guid IntentId, string IntentEvidenceSha256, Guid WorkpackId,
    string ManifestSha256, string ReconciliationSha256, string BundleSha256,
    string SafeRefPlanSha256, GitSourceBundleView Bundle,
    IReadOnlyList<GitMirrorRefComparison> Refs);

public sealed record GitImportRehearsalArtifact(
    string GitObjectFormat, string VerifiedSourceRefsSha256,
    int CreateCandidates, int AlreadyMatching, int TargetOnlyNotMaterialized,
    int DestinationCreated, string CreatedRefsSha256,
    DateTimeOffset CompletedAtUtc);

public sealed record GitImportRehearsalView(
    Guid Id, Guid IntentId, Guid PreparationId, Guid PlanId,
    Guid WorkpackId, string IntentEvidenceSha256,
    string ManifestSha256, string ReconciliationSha256, string BundleSha256,
    string SafeRefPlanSha256, string EvidenceSha256,
    string GitObjectFormat, string VerifiedSourceRefsSha256,
    int CreateCandidates, int AlreadyMatching, int TargetOnlyNotMaterialized,
    int DestinationCreated, string CreatedRefsSha256,
    DateTimeOffset CompletedAtUtc, Guid RequestedBy, bool IsCurrent,
    bool DestinationWritesPerformed, bool ExecutionAuthorized,
    bool ReadyForExecution);

public sealed record GitImportRehearsalInput(
    Guid PreparationId, Guid PlanId, Guid WorkpackId, Guid IntentId,
    string IntentEvidenceSha256, string ManifestSha256,
    string ReconciliationSha256, string BundleSha256, string SafeRefPlanSha256,
    GitImportRehearsalArtifact Artifact, Guid RequestedBy);

public sealed class GitImportRehearsalException(string code)
    : Exception("Local Git import rehearsal rejected")
{
    public string Code { get; } = code;
}

public static class GitImportRehearsalIntegrity
{
    public const int MaxCandidateRefs = 128;

    public static string Seal(GitImportRehearsalInput input)
    {
        if (input.PreparationId == Guid.Empty || input.PlanId == Guid.Empty ||
            input.WorkpackId == Guid.Empty || input.IntentId == Guid.Empty ||
            input.RequestedBy == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(input.IntentEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.ManifestSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.ReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.BundleSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.SafeRefPlanSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.Artifact.VerifiedSourceRefsSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(input.Artifact.CreatedRefsSha256) ||
            input.Artifact.GitObjectFormat is not ("sha1" or "sha256") ||
            input.Artifact.CreateCandidates is < 1 or > MaxCandidateRefs ||
            input.Artifact.AlreadyMatching < 0 ||
            input.Artifact.TargetOnlyNotMaterialized < 0 ||
            input.Artifact.DestinationCreated != input.Artifact.CreateCandidates ||
            input.Artifact.CompletedAtUtc == default)
            throw new GitImportRehearsalException("GitRehearsalEvidenceInvalid");

        var canonical = JsonSerializer.Serialize(new object?[] {
            "git-local-create-only-rehearsal-v1",
            input.PreparationId.ToString("D"), input.PlanId.ToString("D"),
            input.WorkpackId.ToString("D"), input.IntentId.ToString("D"),
            input.IntentEvidenceSha256, input.ManifestSha256,
            input.ReconciliationSha256, input.BundleSha256,
            input.SafeRefPlanSha256, input.Artifact.GitObjectFormat,
            input.Artifact.VerifiedSourceRefsSha256,
            input.Artifact.CreateCandidates, input.Artifact.AlreadyMatching,
            input.Artifact.TargetOnlyNotMaterialized,
            input.Artifact.DestinationCreated, input.Artifact.CreatedRefsSha256,
            input.Artifact.CompletedAtUtc.ToUnixTimeMilliseconds(),
            input.RequestedBy.ToString("D")
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // Verifiable ordered create-only ref digest, no credentials/URLs.
    public static string SealRefs(
        string domain, IReadOnlyList<GitMirrorRef> refs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(domain + "\n"));
        foreach (var item in refs.OrderBy(x => x.Name, StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { item.Name, item.ObjectId }) + "\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public interface IGitImportRehearsalRunner
{
    Task<GitImportRehearsalArtifact> RunAsync(
        GitImportRehearsalRequest request, CancellationToken ct);
}

public interface IGitImportRehearsalStore
{
    Task<GitImportRehearsalView> SaveAsync(
        GitImportRehearsalInput input, CancellationToken ct);
    Task<GitImportRehearsalView?> GetAsync(
        Guid preparationId, Guid planId, Guid rehearsalId, CancellationToken ct);
    Task<IReadOnlyList<GitImportRehearsalView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct);
}

public sealed class GitImportRehearsalService(
    IGitHubImportIntentStore intents,
    IIncrementalGitRefReconciliationStore reconciliations,
    IGitSourceBundleEvidenceStore bundles,
    IGitSourceBundleArtifactVerifier verifier,
    IGitImportRehearsalRunner runner,
    IGitImportRehearsalStore store,
    TimeProvider clock)
{
    public async Task<GitImportRehearsalView> RehearseAsync(
        Guid preparationId, Guid planId, Guid intentId,
        string expectedIntentEvidenceSha256, Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            intentId == Guid.Empty || actorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(expectedIntentEvidenceSha256))
            throw new GitImportRehearsalException("GitRehearsalRequestInvalid");

        var intent = await intents.GetAsync(preparationId, planId, intentId, ct)
            ?? throw new KeyNotFoundException("ImportIntentNotFound");
        if (!intent.IsCurrent || intent.IsExpired ||
            intent.State != GitHubImportIntentStates.Acknowledged ||
            intent.EvidenceSha256 != expectedIntentEvidenceSha256 ||
            intent.ExecutionAuthorized || intent.ReadyForExecution ||
            intent.WriteCapabilityVerified)
            throw new GitImportRehearsalException("GitRehearsalIntentNotCurrent");
        var bundle = await bundles.GetAsync(
            preparationId, planId, intent.WorkpackId, ct)
            ?? throw new GitImportRehearsalException("GitRehearsalBundleMissing");
        if (!bundle.IsCurrent || bundle.ReadyForExecution ||
            bundle.BundleSha256 != intent.BundleSha256 ||
            bundle.ReconciliationSha256 != intent.ReconciliationSha256)
            throw new GitImportRehearsalException("GitRehearsalBundleChanged");
        await verifier.VerifyAsync(bundle, ct);

        var first = await reconciliations.PageAsync(
            preparationId, planId, bundle.ReconciliationId, null, 1, ct)
            ?? throw new GitImportRehearsalException("GitRehearsalReconciliationMissing");
        var stub = new GitHubImportReadinessView(
            intent.ImportReadinessId, planId, intent.WorkpackId,
            bundle.ReconciliationId, bundle.SourceRepositoryId, "",
            "", intent.ManifestSha256, intent.ReconciliationSha256, "",
            intent.BundleSha256, intent.ImportReadinessSha256,
            clock.GetUtcNow(), "Visible", "ReportedAllowed", false, false,
            0, 0, [], true, false, false);
        // Load the original F-032 manifest/target binding from persisted
        // data. No ref candidates are accepted from HTTP request bodies.
        stub = stub with {
            TargetEvidenceSha256 = first.Reconciliation.TargetEvidenceSha256
        };
        var safe = await GitHubImportIntentService.BuildVerifiedPlanAsync(
            reconciliations, preparationId, planId,
            bundle.ReconciliationId, stub, ct);
        if (safe.EvidenceSha256 != intent.SafeRefPlanSha256 ||
            !safe.HasOnlyNonDestructiveCandidates ||
            safe.CreateCandidates != intent.CreateCandidates ||
            safe.AlreadyMatching != intent.AlreadyMatching ||
            safe.TargetOnlyPreserved != intent.TargetOnlyPreserved ||
            safe.DivergedBlocked != 0 ||
            safe.CreateCandidates > GitImportRehearsalIntegrity.MaxCandidateRefs)
            throw new GitImportRehearsalException("GitRehearsalSafePlanChanged");

        // A cached result is still checked against current F-040/035/032
        // and the entire physical bundle above before returning it.
        var previous = await store.ListAsync(preparationId, planId,
            intent.WorkpackId, ct);
        var stored = previous.SingleOrDefault(x => x.IntentId == intentId);
        if (stored is not null)
        {
            if (stored.IntentEvidenceSha256 != expectedIntentEvidenceSha256 ||
                !stored.IsCurrent)
                throw new GitImportRehearsalException("GitRehearsalResultStale");
            return stored;
        }

        var refs = new List<GitMirrorRefComparison>();
        string? after = null;
        for (var p = 0; p < 501; p++)
        {
            var page = await reconciliations.PageAsync(preparationId,
                planId, bundle.ReconciliationId, after, 100, ct)
                ?? throw new GitImportRehearsalException("GitRehearsalReconciliationMissing");
            refs.AddRange(page.Items);
            if (refs.Count > 50_000)
                throw new GitImportRehearsalException("GitRehearsalRefLimit");
            if (page.NextAfter is null) break;
            if (p == 500 || page.NextAfter == after)
                throw new GitImportRehearsalException("GitRehearsalPaginationInvalid");
            after = page.NextAfter;
        }

        var artifact = await runner.RunAsync(new(
            intentId, expectedIntentEvidenceSha256, intent.WorkpackId,
            intent.ManifestSha256, intent.ReconciliationSha256,
            intent.BundleSha256, intent.SafeRefPlanSha256, bundle,
            refs), ct);
        return await store.SaveAsync(new(
            preparationId, planId, intent.WorkpackId, intentId,
            expectedIntentEvidenceSha256, intent.ManifestSha256,
            intent.ReconciliationSha256, intent.BundleSha256,
            intent.SafeRefPlanSha256, artifact, actorId), ct);
    }

    public Task<GitImportRehearsalView?> GetAsync(
        Guid preparationId, Guid planId, Guid id, CancellationToken ct) =>
        store.GetAsync(preparationId, planId, id, ct);

    public Task<IReadOnlyList<GitImportRehearsalView>> ListAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct) =>
        store.ListAsync(preparationId, planId, workpackId, ct);
}
