using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-032 combines F-014 immutable source observations and F-031 verified
// destination observations. It is a review artifact, never an executable job.
public sealed record VerifiedSourceGitRefs(
    Guid SnapshotId, Guid SourceConnectionId, string SourceRepositoryId,
    string SourceEvidenceSha256, string? RefManifestRootSha256,
    IReadOnlyList<GitMirrorRef> Refs);

public static class VerifiedSourceGitRefIntegrity
{
    public static VerifiedSourceGitRefs Seal(
        Guid snapshotId, Guid connectionId, string repositoryId,
        string? manifestRootSha256, IReadOnlyList<GitMirrorRef> refs)
    {
        if (snapshotId == Guid.Empty || connectionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(repositoryId) || repositoryId.Length > 256 ||
            manifestRootSha256 is not null && !ValidSha(manifestRootSha256))
            throw new IncrementalGitRefReconciliationException("SourceRefEvidenceInvalid");

        IReadOnlyList<GitMirrorRef> canonical;
        try { canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(refs); }
        catch (GitMirrorRefReconciliationException)
        { throw new IncrementalGitRefReconciliationException("SourceRefEvidenceInvalid"); }

        // The v1 proof hashes the snapshot identity, source connection,
        // repository selection, sealed F-014 manifest root (or explicit
        // legacy-inline marker), and the full sorted ref/OID set.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new object?[] {
            "verified-source-git-refs-v1", snapshotId.ToString("D"),
            connectionId.ToString("D"), repositoryId,
            manifestRootSha256 ?? "legacy-inline", canonical.Count
        });
        foreach (var item in canonical)
            Append(new object?[] { item.Name, item.ObjectId });
        return new(snapshotId, connectionId, repositoryId,
            Convert.ToHexStringLower(hash.GetHashAndReset()),
            manifestRootSha256, canonical);

        void Append(object?[] row) => hash.AppendData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(row) + "\n"));
    }

    private static bool ValidSha(string sha) => sha.Length == 64 &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record IncrementalGitRefReconciliationView(
    Guid Id, Guid PlanId, string SourceRepositoryId, string TargetRepositoryName,
    Guid TargetObservationId, Guid SnapshotId, string ManifestSha256,
    string SourceEvidenceSha256, string? SourceManifestRootSha256,
    string TargetEvidenceSha256, string ReconciliationSha256,
    DateTimeOffset CreatedAtUtc, int SourceRefCount, int TargetRefCount,
    int MatchingCount, int CreateCandidateCount, int DivergedCount,
    int TargetOnlyCount, bool IsCurrent, bool ReadyForExecution);

public sealed record IncrementalGitRefReconciliationPage(
    IncrementalGitRefReconciliationView Reconciliation,
    IReadOnlyList<GitMirrorRefComparison> Items, string? NextAfter);

public sealed class IncrementalGitRefReconciliationException(string code)
    : Exception("Incremental Git ref reconciliation rejected")
{
    public string Code { get; } = code;
}

public interface IIncrementalGitRefReconciliationStore
{
    Task<IncrementalGitRefReconciliationView> CreateAsync(
        Guid preparationId, Guid planId, string sourceRepositoryId,
        Guid targetObservationId, string expectedManifestSha256,
        string expectedTargetEvidenceSha256, Guid actorId, CancellationToken ct);
    Task<IReadOnlyList<IncrementalGitRefReconciliationView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct);
    Task<IncrementalGitRefReconciliationPage?> PageAsync(
        Guid preparationId, Guid planId, Guid reconciliationId,
        string? after, int take, CancellationToken ct);
}

public sealed class IncrementalGitRefReconciliationService(
    IIncrementalGitRefReconciliationStore store)
{
    public Task<IncrementalGitRefReconciliationView> CreateAsync(
        Guid preparationId, Guid planId, string sourceRepositoryId,
        Guid targetObservationId, string expectedManifestSha256,
        string expectedTargetEvidenceSha256, Guid actorId, CancellationToken ct)
    {
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            targetObservationId == Guid.Empty || actorId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            sourceRepositoryId.Length > 256 ||
            !ValidSha(expectedManifestSha256) ||
            !ValidSha(expectedTargetEvidenceSha256))
            throw new IncrementalGitRefReconciliationException("InvalidReconciliationRequest");
        return store.CreateAsync(preparationId, planId, sourceRepositoryId,
            targetObservationId, expectedManifestSha256,
            expectedTargetEvidenceSha256, actorId, ct);
    }

    public Task<IReadOnlyList<IncrementalGitRefReconciliationView>> ListAsync(
        Guid preparationId, Guid planId, CancellationToken ct) =>
        store.ListAsync(preparationId, planId, ct);

    public Task<IncrementalGitRefReconciliationPage?> PageAsync(
        Guid preparationId, Guid planId, Guid reconciliationId,
        string? after, int take, CancellationToken ct)
    {
        if (take is < 1 or > 100 || (after?.Length ?? 0) > 255)
            throw new IncrementalGitRefReconciliationException("InvalidReconciliationPage");
        return store.PageAsync(preparationId, planId, reconciliationId,
            after, take, ct);
    }

    private static bool ValidSha(string? sha) => sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
