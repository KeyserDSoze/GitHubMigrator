using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-058: immutable, hash-only reconciliation of F-054, F-055, F-057.
// A recorded F-057 observation is NOT a GitHub send acknowledgement,
// migration success, or a safe authorization to retry.
public static class GitHubRemoteObjectReconciliationStates
{
    public const string FullCopyEvidenceOnly = "FullCopyEvidenceOnly";
}

public sealed class GitHubRemoteObjectReconciliationException(string code)
    : Exception("Durable remote object reconciliation refused")
{
    public string Code { get; } = code;
}

public sealed record GitHubRemoteObjectReconciliationEvidence(
    Guid GrantId, Guid ReadbackId, Guid HazardId, long RepositoryId,
    Guid ObserverId, IReadOnlyList<GitHubObservedRef> OriginalBaseline,
    IReadOnlyList<GitMirrorRef> CreateCandidates,
    GitHubReadOnlyObjectMirrorRequest MirrorRequest,
    GitHubReadOnlyObjectMirrorReceipt MirrorReceipt);

public sealed record GitHubRemoteObjectReconciliationDecision(
    string State, Guid SourceSnapshotId,
    string OriginalBaselineSha256, string CandidateSetSha256,
    string TargetRefSetSha256, string RemoteRefObservationSha256,
    string FullObjectClosureSha256, string RemoteMirrorReceiptSha256,
    string EvidenceSha256, DateTimeOffset VerifiedAtUtc);

public sealed record GitHubRemoteObjectReconciliationStatus(
    Guid Id, Guid GrantId, Guid ReadbackId, Guid HazardId,
    long RepositoryId, Guid SourceSnapshotId, string State,
    string OriginalBaselineSha256, string CandidateSetSha256,
    string TargetRefSetSha256, string RemoteRefObservationSha256,
    string FullObjectClosureSha256, string RemoteMirrorReceiptSha256,
    string EvidenceSha256, Guid ObservedBy, DateTimeOffset VerifiedAtUtc,
    DateTimeOffset RecordedAtUtc, bool RemotePushInvoked,
    bool RemoteWritesAuthorized, bool MayAutomaticallyRetry,
    bool MigrationComplete);

public sealed record GitHubRemoteObjectReconciliationEvent(
    long Id, Guid ReconciliationId, string Type, Guid ActorId,
    DateTimeOffset OccurredAtUtc);

public interface IGitHubRemoteObjectReconciliationJournal
{
    Task<GitHubRemoteObjectReconciliationStatus> RecordAsync(
        GitHubRemoteObjectReconciliationEvidence evidence, CancellationToken ct);
    Task<GitHubRemoteObjectReconciliationStatus?> GetAsync(
        Guid hazardId, long repositoryId, CancellationToken ct);
    Task<IReadOnlyList<GitHubRemoteObjectReconciliationEvent>> EventsAsync(
        Guid hazardId, long repositoryId, CancellationToken ct);
}

public static class GitHubRemoteObjectReconciliationIntegrity
{
    public static readonly TimeSpan MaxReceiptAge = TimeSpan.FromSeconds(90);

    public static GitHubRemoteObjectReconciliationDecision Verify(
        GitHubRemoteObjectReconciliationEvidence evidence,
        GitHubReadOnlyObjectMirrorOptions options,
        DateTimeOffset now)
    {
        if (evidence is null || evidence.GrantId == Guid.Empty ||
            evidence.ReadbackId == Guid.Empty || evidence.HazardId == Guid.Empty ||
            evidence.RepositoryId <= 0 || evidence.ObserverId == Guid.Empty ||
            evidence.MirrorRequest is null || evidence.MirrorReceipt is null ||
            evidence.OriginalBaseline is null ||
            evidence.CreateCandidates is null)
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationInputInvalid");

        var request = evidence.MirrorRequest;
        var receipt = evidence.MirrorReceipt;
        if (receipt.VerifiedAtUtc < now - MaxReceiptAge ||
            receipt.VerifiedAtUtc > now.AddSeconds(5) ||
            request.ExpectedRemoteRefs is null ||
            receipt.VerifiedAtUtc < request.ExpectedRemoteRefs.ObservedAtUtc ||
            request.RepositoryId != evidence.RepositoryId ||
            receipt.RepositoryId != evidence.RepositoryId ||
            receipt.SourceSnapshotId != request.SourceSnapshotId ||
            !string.Equals(receipt.Organization, request.Organization,
                StringComparison.Ordinal) ||
            !string.Equals(receipt.Repository, request.Repository,
                StringComparison.Ordinal))
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationReceiptStaleOrWrongTarget");

        try
        {
            // Revalidate the original F-057 snapshot at the time of its
            // physical mirror audit, not at a guessed future send time.
            GitHubReadOnlyObjectMirrorIntegrity.Verify(
                request, options, receipt.VerifiedAtUtc);
        }
        catch (GitHubReadOnlyObjectMirrorException)
        {
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationObservationInvalid");
        }

        if (!receipt.GitHubFetchPerformed || receipt.RemotePushInvoked ||
            receipt.RemoteWritesAuthorized || receipt.MigrationComplete ||
            receipt.GitObjectFormat !=
                (request.ExpectedSourceRefs[0].ObjectId.Length == 40
                    ? "sha1" : "sha256") ||
            receipt.VerifiedReachableObjects is < 1 or >
                GitObjectClosureAuditIntegrity.MaxObjects ||
            receipt.ObservedRemoteRefs !=
                request.ExpectedRemoteRefs.Refs.Count ||
            !GitSourceBundleStorageCheck.ValidSha(
                receipt.SourceObjectSetSha256) ||
            receipt.SourceObjectSetSha256 != receipt.RemoteObjectSetSha256 ||
            !GitSourceBundleStorageCheck.ValidSha(
                receipt.OfflineClosureEvidenceSha256) ||
            receipt.RemoteRefFingerprintSha256 !=
                GitHubRemoteRefFenceIntegrity.Fingerprint(
                    request.ExpectedRemoteRefs.Refs))
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationReceiptInvalid");

        // F-057's domain SHA uses all these fields; recomputing it detects
        // accidental/unaudited changes, NOT a cryptographic provider
        // signature. This is an internal, trusted-worker-only contract.
        var originalReceiptHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
                "f057-github-readonly-remote-full-closure-v1",
                request.SourceSnapshotId.ToString("D"), request.RepositoryId,
                request.Organization.ToLowerInvariant(),
                request.Repository.ToLowerInvariant(),
                request.ExpectedRemoteRefs.EvidenceSha256,
                receipt.RemoteRefFingerprintSha256,
                receipt.OfflineClosureEvidenceSha256,
                receipt.SourceObjectSetSha256,
                receipt.RemoteObjectSetSha256,
                receipt.VerifiedReachableObjects,
                true, false, false, false
            }))));
        if (originalReceiptHash != receipt.EvidenceSha256)
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationReceiptInvalid");

        IReadOnlyList<GitMirrorRef> baseline;
        IReadOnlyList<GitMirrorRef> creates;
        GitHubRemotePostSendInspection inspection;
        try
        {
            var sealedBaseline = GitHubRefObservationIntegrity.Seal(
                request.Organization, request.Repository,
                request.ExpectedRemoteRefs.ObservedAtUtc,
                evidence.OriginalBaseline);
            if (!sealedBaseline.Refs.SequenceEqual(evidence.OriginalBaseline))
                throw new GitHubRemoteObjectReconciliationException(
                    "RemoteObjectReconciliationBaselineInvalid");
            baseline = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                evidence.OriginalBaseline.Select(x =>
                    new GitMirrorRef(x.Name, x.ObjectId)).ToArray());
            creates = GitHubRemoteTipIntegrity.CanonicalCandidates(
                evidence.CreateCandidates);
            inspection = GitHubRemotePostSendIntegrity.Inspect(
                baseline, creates, request.ExpectedRemoteRefs);
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationBaselineInvalid");
        }
        catch (GitHubRefObservationException)
        {
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationBaselineInvalid");
        }
        catch (GitHubRemoteTipException)
        {
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationCandidatesInvalid");
        }
        catch (GitHubRemoteTestGrantException)
        {
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationRefsInvalid");
        }

        // Exactly the intended existing refs + create-only refs are
        // observed: no additional, missing or diverged branches/tags.
        if (inspection.State != "ExactRemoteRefsObserved")
            throw new GitHubRemoteObjectReconciliationException(
                "RemoteObjectReconciliationRefsDiverged");

        var baselineSha = GitHubRemoteRefFenceIntegrity.Fingerprint(
            evidence.OriginalBaseline);
        var candidatesSha = GitImportRehearsalIntegrity.SealRefs(
            "f044-create-only-candidate-refs-v1", creates);
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
                "f058-immutable-independent-remote-object-evidence-v1",
                evidence.GrantId.ToString("D"),
                evidence.ReadbackId.ToString("D"),
                evidence.HazardId.ToString("D"),
                evidence.RepositoryId,
                request.SourceSnapshotId.ToString("D"),
                GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
                baselineSha, candidatesSha, inspection.RefSetSha256,
                request.ExpectedRemoteRefs.EvidenceSha256,
                receipt.OfflineClosureEvidenceSha256,
                receipt.EvidenceSha256,
                receipt.VerifiedAtUtc.ToUnixTimeMilliseconds(),
                false, false, false, false
            }))));
        return new(GitHubRemoteObjectReconciliationStates.FullCopyEvidenceOnly,
            request.SourceSnapshotId, baselineSha, candidatesSha,
            inspection.RefSetSha256, request.ExpectedRemoteRefs.EvidenceSha256,
            receipt.OfflineClosureEvidenceSha256, receipt.EvidenceSha256,
            digest, receipt.VerifiedAtUtc);
    }
}
