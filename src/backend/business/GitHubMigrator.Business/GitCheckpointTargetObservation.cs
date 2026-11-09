using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #109 / F-031 later epoch: sealed, immutable, checkpoint-scoped
// target Git-data GET. Never grants execution or future GET authority.
// This is separate from the historical current-F-029 F-031 record.
public sealed record GitCheckpointTargetObservation(
    int EvidenceVersion, Guid Id, Guid PreparationId, Guid PlanId,
    Guid CheckpointId, Guid SourceSnapshotId, Guid DestinationConnectionId,
    string SourceRepositoryId, string TargetRepositoryName, string Organization,
    string ObjectFormat, Guid ReviewedBy,
    string CandidateEvidenceSha256, string HumanReviewEvidenceSha256,
    string ReadPreflightEvidenceSha256, string TargetEvidenceSha256,
    DateTimeOffset ObservedAtUtc, int RefCount,
    IReadOnlyList<GitHubObservedRef> Refs, string EvidenceSha256,
    bool CanObserveTarget, bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);

public sealed class GitCheckpointTargetObservationException(string code)
    : Exception("Checkpoint-scoped GitHub target observation rejected")
{
    public string Code { get; } = code;
}

public static class GitCheckpointTargetObservationIntegrity
{
    private static GitCheckpointTargetObservationException Invalid() =>
        new("CheckpointTargetObservationEvidenceInvalid");

    private static bool Sha(string? text) =>
        text is { Length: 64 } &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitCheckpointTargetObservation Seal(
        GitCheckpointTargetReadPreflight preflight,
        GitHubRefObservation observation, Guid observationId)
    {
        if (preflight is null || observation is null ||
            preflight.EvidenceVersion != 1 ||
            preflight.PreparationId == Guid.Empty || preflight.PlanId == Guid.Empty ||
            preflight.CheckpointId == Guid.Empty ||
            preflight.SourceSnapshotId == Guid.Empty ||
            preflight.DestinationConnectionId == Guid.Empty ||
            preflight.ReviewedBy == Guid.Empty || observationId == Guid.Empty ||
            !Sha(preflight.CandidateEvidenceSha256) ||
            !Sha(preflight.HumanReviewEvidenceSha256) ||
            !Sha(preflight.EvidenceSha256) ||
            preflight.ObjectFormat is not ("sha1" or "sha256") ||
            preflight.ProviderReadAuthorized || preflight.CanObserveTarget ||
            preflight.CanPush || preflight.CanRetry ||
            preflight.CanDispatch || preflight.MigrationComplete ||
            !GitHubRefObservationIntegrity.ValidOwner(preflight.Organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(
                preflight.TargetRepositoryName) ||
            !string.Equals(preflight.Organization, observation.Organization,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(preflight.TargetRepositoryName, observation.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            observation.ObservedAtUtc.Offset != TimeSpan.Zero ||
            observation.ObservedAtUtc < preflight.ValidatedAtUtc ||
            observation.ObservedAtUtc >= preflight.ExpiresAtUtc ||
            observation.Refs is not { Count: >= 1 } ||
            observation.Refs.Count > GitMirrorRefReconciler.MaxRefsPerSide)
            throw Invalid();

        GitHubRefObservation sealedTarget;
        try
        {
            sealedTarget = GitHubRefObservationIntegrity.Seal(
                observation.Organization, observation.Repository,
                observation.ObservedAtUtc, observation.Refs);
        }
        catch (GitHubRefObservationException) { throw Invalid(); }

        var objectLength = preflight.ObjectFormat == "sha1" ? 40 : 64;
        if (!string.Equals(observation.EvidenceSha256,
                sealedTarget.EvidenceSha256, StringComparison.Ordinal) ||
            observation.Refs.Any(r => r.ObjectId.Length != objectLength) ||
            sealedTarget.Refs.Any(r => r.ObjectId.Length != objectLength))
            throw Invalid();

        var digest = Digest(1, observationId, preflight.PreparationId,
            preflight.PlanId, preflight.CheckpointId, preflight.SourceSnapshotId,
            preflight.DestinationConnectionId, preflight.SourceRepositoryId,
            preflight.TargetRepositoryName, preflight.Organization,
            preflight.ObjectFormat, preflight.ReviewedBy,
            preflight.CandidateEvidenceSha256,
            preflight.HumanReviewEvidenceSha256, preflight.EvidenceSha256,
            sealedTarget.EvidenceSha256, sealedTarget.ObservedAtUtc,
            sealedTarget.Refs.Count);
        return new(1, observationId, preflight.PreparationId,
            preflight.PlanId, preflight.CheckpointId,
            preflight.SourceSnapshotId, preflight.DestinationConnectionId,
            preflight.SourceRepositoryId, preflight.TargetRepositoryName,
            preflight.Organization, preflight.ObjectFormat,
            preflight.ReviewedBy, preflight.CandidateEvidenceSha256,
            preflight.HumanReviewEvidenceSha256, preflight.EvidenceSha256,
            sealedTarget.EvidenceSha256, sealedTarget.ObservedAtUtc,
            sealedTarget.Refs.Count, sealedTarget.Refs, digest,
            false, false, false, false, false);
    }

    // A historical receipt is verifiable without trusting a stale F-029
    // mapping, but it is NEVER proof of current source/target currency.
    public static void VerifyStored(GitCheckpointTargetObservation receipt)
    {
        if (receipt is null || receipt.EvidenceVersion != 1 ||
            receipt.Id == Guid.Empty || receipt.PreparationId == Guid.Empty ||
            receipt.PlanId == Guid.Empty || receipt.CheckpointId == Guid.Empty ||
            receipt.SourceSnapshotId == Guid.Empty ||
            receipt.DestinationConnectionId == Guid.Empty ||
            receipt.ReviewedBy == Guid.Empty ||
            receipt.SourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(receipt.SourceRepositoryId) ||
            !GitHubRefObservationIntegrity.ValidOwner(receipt.Organization) ||
            !GitHubRefObservationIntegrity.ValidRepository(receipt.TargetRepositoryName) ||
            receipt.ObjectFormat is not ("sha1" or "sha256") ||
            !Sha(receipt.CandidateEvidenceSha256) ||
            !Sha(receipt.HumanReviewEvidenceSha256) ||
            !Sha(receipt.ReadPreflightEvidenceSha256) ||
            !Sha(receipt.TargetEvidenceSha256) || !Sha(receipt.EvidenceSha256) ||
            receipt.ObservedAtUtc == default ||
            receipt.ObservedAtUtc.Offset != TimeSpan.Zero ||
            receipt.Refs is not { Count: >= 1 } ||
            receipt.Refs.Count > GitMirrorRefReconciler.MaxRefsPerSide ||
            receipt.Refs.Count != receipt.RefCount ||
            receipt.CanObserveTarget || receipt.CanPush ||
            receipt.CanRetry || receipt.CanDispatch || receipt.MigrationComplete)
            throw Invalid();

        GitHubRefObservation observed;
        try
        {
            observed = GitHubRefObservationIntegrity.Seal(
                receipt.Organization, receipt.TargetRepositoryName,
                receipt.ObservedAtUtc, receipt.Refs);
        }
        catch (GitHubRefObservationException) { throw Invalid(); }
        var objectLength = receipt.ObjectFormat == "sha1" ? 40 : 64;
        if (observed.Refs.Any(r => r.ObjectId.Length != objectLength) ||
            observed.EvidenceSha256 != receipt.TargetEvidenceSha256 ||
            receipt.EvidenceSha256 != Digest(
                receipt.EvidenceVersion, receipt.Id, receipt.PreparationId,
                receipt.PlanId, receipt.CheckpointId, receipt.SourceSnapshotId,
                receipt.DestinationConnectionId, receipt.SourceRepositoryId,
                receipt.TargetRepositoryName, receipt.Organization,
                receipt.ObjectFormat, receipt.ReviewedBy,
                receipt.CandidateEvidenceSha256, receipt.HumanReviewEvidenceSha256,
                receipt.ReadPreflightEvidenceSha256, receipt.TargetEvidenceSha256,
                receipt.ObservedAtUtc, receipt.RefCount))
            throw Invalid();
    }

    public static void VerifyCurrent(
        GitCheckpointTargetObservation receipt,
        GitCheckpointTargetReadPreflight preflight)
    {
        VerifyStored(receipt);
        if (preflight is null || receipt.PreparationId != preflight.PreparationId ||
            receipt.PlanId != preflight.PlanId ||
            receipt.CheckpointId != preflight.CheckpointId ||
            receipt.SourceSnapshotId != preflight.SourceSnapshotId ||
            receipt.DestinationConnectionId != preflight.DestinationConnectionId ||
            receipt.SourceRepositoryId != preflight.SourceRepositoryId ||
            receipt.TargetRepositoryName != preflight.TargetRepositoryName ||
            receipt.Organization != preflight.Organization ||
            receipt.ObjectFormat != preflight.ObjectFormat ||
            receipt.ReviewedBy != preflight.ReviewedBy ||
            receipt.CandidateEvidenceSha256 != preflight.CandidateEvidenceSha256 ||
            receipt.HumanReviewEvidenceSha256 != preflight.HumanReviewEvidenceSha256 ||
            receipt.ReadPreflightEvidenceSha256 != preflight.EvidenceSha256)
            throw Invalid();
    }

    private static string Digest(int version, Guid id, Guid prep, Guid plan,
        Guid checkpoint, Guid snapshot, Guid destination, string sourceRepository,
        string targetRepository, string organization, string objectFormat,
        Guid reviewer, string candidateSha, string humanSha, string preflightSha,
        string targetSha, DateTimeOffset observedAt, int refCount)
    {
        var fields = new object?[] {
            "f097c4c0d-checkpoint-target-observation-v1", version,
            id.ToString("D"), prep.ToString("D"), plan.ToString("D"),
            checkpoint.ToString("D"), snapshot.ToString("D"),
            destination.ToString("D"), sourceRepository, targetRepository,
            organization, objectFormat, reviewer.ToString("D"),
            candidateSha, humanSha, preflightSha, targetSha,
            observedAt.ToUnixTimeMilliseconds(), refCount,
            false, false, false, false, false
        };
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))))
            .ToLowerInvariant();
    }
}
