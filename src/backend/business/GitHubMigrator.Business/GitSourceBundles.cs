namespace GitHubMigrator.Business;

// F-035: source-side, offline Git bundle export. Nothing in this contract
// grants GitHub permissions or contains a credential, remote URI or push URL.
public sealed record GitSourceBundleRequest(
    Guid WorkpackId, Guid SourceConnectionId, string SourceRepositoryId,
    string ReconciliationSha256, string SourceEvidenceSha256,
    IReadOnlyList<GitMirrorRef> ExpectedRefs);

public sealed record GitSourceBundleArtifact(
    Guid WorkpackId, string SourceEvidenceSha256, string BundleFileName,
    string BundleSha256, long BundleBytes, int RefCount,
    DateTimeOffset AcquiredAtUtc);

public sealed record GitSourceBundleView(
    Guid WorkpackId, Guid PlanId, Guid ReconciliationId,
    string SourceRepositoryId, string SourceEvidenceSha256,
    string ReconciliationSha256, string BundleFileName,
    string BundleSha256, long BundleBytes, int RefCount,
    DateTimeOffset AcquiredAtUtc, bool IsCurrent, bool ReadyForExecution);

public sealed class GitSourceBundleException(string code)
    : Exception("Source Git bundle acquisition rejected")
{
    public string Code { get; } = code;
}

// A stored database checksum is not proof that on-disk bytes still match.
// Every actual reuse must re-hash the named local file (no arbitrary paths).
public sealed record GitSourceBundleStorageCheck(
    Guid WorkpackId, string BundleSha256, long BundleBytes,
    DateTimeOffset VerifiedAtUtc, bool IsCurrent, bool ReadyForExecution)
{
    public static bool ValidSha(string? sha) => sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public interface IGitSourceBundleArtifactVerifier
{
    Task<GitSourceBundleStorageCheck> VerifyAsync(
        GitSourceBundleView evidence, CancellationToken ct);
}

public interface IGitSourceBundleAcquirer
{
    Task<GitSourceBundleArtifact> AcquireAsync(
        GitSourceBundleRequest request, CancellationToken ct);
}

public interface IGitSourceBundleEvidenceStore
{
    Task<GitSourceBundleView> SaveAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, GitSourceBundleArtifact artifact,
        CancellationToken ct);

    Task<GitSourceBundleView?> GetAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        CancellationToken ct);
}

// The caller is the authenticated/authorized ASP.NET boundary; it never
// supplies a local filesystem path, remote endpoint or Git argument.
public sealed class GitSourceBundleService(
    IGitSourceBundleAcquirer acquirer, IGitSourceBundleEvidenceStore store,
    IGitTransferWorkpackRepository workpacks,
    IIncrementalGitRefReconciliationStore reconciliations,
    IIncrementalOperationPlanRepository plans,
    IVerifiedSourceGitRefReader sourceRefs,
    IGitSourceBundleArtifactVerifier verifier)
{
    public async Task<GitSourceBundleView> AcquireAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        string expectedReconciliationSha256, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            workpackId == Guid.Empty || !ValidSha(expectedReconciliationSha256))
            throw new GitSourceBundleException("InvalidBundleRequest");

        var pack = await workpacks.GetAsync(
            preparationId, planId, workpackId, ct)
            ?? throw new KeyNotFoundException("TransferWorkpackNotFound");
        if (!pack.IsCurrent ||
            pack.State != GitTransferWorkpackStates.Blocked ||
            pack.Phase != GitTransferWorkpackPhases.WriteBlocked ||
            pack.ReconciliationSha256 != expectedReconciliationSha256)
            throw new GitSourceBundleException("BundleWorkpackNotCurrent");

        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw new KeyNotFoundException("OperationPlanNotFound");
        if (!plan.IsCurrent || plan.ManifestSha256 != pack.ManifestSha256 ||
            plan.SourceConnectionId == Guid.Empty)
            throw new GitSourceBundleException("BundlePlanNotCurrent");

        var reconciliation = await reconciliations.PageAsync(
            preparationId, planId, pack.ReconciliationId, null, 1, ct)
            ?? throw new KeyNotFoundException("RefReconciliationNotFound");
        var proven = reconciliation.Reconciliation;
        if (!proven.IsCurrent ||
            proven.ReconciliationSha256 != pack.ReconciliationSha256 ||
            proven.TargetEvidenceSha256 != pack.TargetEvidenceSha256 ||
            proven.SourceRepositoryId != pack.SourceRepositoryId)
            throw new GitSourceBundleException("BundleReconciliationNotCurrent");

        var source = await sourceRefs.ReadAsync(plan, pack.SourceRepositoryId, ct);
        if (source.SourceEvidenceSha256 != proven.SourceEvidenceSha256 ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            source.SnapshotId != plan.SnapshotId)
            throw new GitSourceBundleException("BundleSourceEvidenceMismatch");

        var existing = await store.GetAsync(
            preparationId, planId, workpackId, ct);
        if (existing is not null)
        {
            if (!existing.IsCurrent ||
                existing.ReconciliationSha256 != expectedReconciliationSha256 ||
                existing.SourceEvidenceSha256 != source.SourceEvidenceSha256)
                throw new GitSourceBundleException("BundleProofMismatch");
            await verifier.VerifyAsync(existing, ct);
            return existing;
        }

        var artifact = await acquirer.AcquireAsync(new(
            workpackId, plan.SourceConnectionId, pack.SourceRepositoryId,
            pack.ReconciliationSha256, source.SourceEvidenceSha256,
            source.Refs), ct);

        // Revalidate all mutable-current checks under the DB source lock
        // inside SaveAsync. A provider/plan change during bundle export must
        // not result in falsely current saved evidence.
        var saved = await store.SaveAsync(preparationId, planId, workpackId,
            expectedReconciliationSha256, artifact, ct);
        await verifier.VerifyAsync(saved, ct);
        return saved;
    }

    public Task<GitSourceBundleView?> GetAsync(
        Guid preparationId, Guid planId, Guid workpackId,
        CancellationToken ct) =>
        store.GetAsync(preparationId, planId, workpackId, ct);

    // Dedicated non-mutating endpoint/consumer check; raw GET keeps exposing
    // only immutable DB metadata, not an assertion about live filesystem.
    public async Task<GitSourceBundleStorageCheck?> VerifyStoredAsync(
        Guid preparationId, Guid planId, Guid workpackId, CancellationToken ct)
    {
        var evidence = await store.GetAsync(
            preparationId, planId, workpackId, ct);
        return evidence is null ? null : await verifier.VerifyAsync(evidence, ct);
    }

    private static bool ValidSha(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// Exposes the F-014 snapshot-scoped source reader through a narrow, verified
// interface so neither the CLI adapter nor the UI can inject expected refs.
public interface IVerifiedSourceGitRefReader
{
    Task<VerifiedSourceGitRefs> ReadAsync(
        IncrementalOperationPlan plan, string repositoryId, CancellationToken ct);
}
