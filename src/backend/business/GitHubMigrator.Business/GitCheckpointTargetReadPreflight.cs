using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #109, phase 1. This is ONLY an ephemeral, read-only prerequisite witness;
// it is neither a GitHub GET authorization nor a target F-031 observation.
// A later component MUST independently authorize provider transport and
// revalidate these inputs after observing GitHub.
public sealed record GitCheckpointTargetReadPreflight(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid CheckpointId, Guid SourceSnapshotId, Guid DestinationConnectionId,
    string SourceRepositoryId, string TargetRepositoryName, string Organization,
    string ObjectFormat, int SourceRefCount,
    Guid ReviewedBy, string CandidateEvidenceSha256,
    string HumanReviewEvidenceSha256, DateTimeOffset ValidatedAtUtc,
    DateTimeOffset ExpiresAtUtc, string EvidenceSha256,
    bool ProviderReadAuthorized, bool CanObserveTarget,
    bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);

public sealed class GitCheckpointTargetReadPreflightException(string code)
    : Exception("Independent checkpoint target-read prerequisites refused")
{
    public string Code { get; } = code;
}

public static class GitCheckpointTargetReadPreflightIntegrity
{
    private static GitCheckpointTargetReadPreflightException Invalid() =>
        new("CheckpointTargetReadPreflightInvalidEvidence");
    private static bool Sha(string? x) =>
        x is { Length: 64 } &&
        x.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitCheckpointTargetReadPreflight Seal(
        GitCheckpointTargetReReviewCandidate candidate,
        GitCheckpointTargetHumanReviewDecision review,
        DateTimeOffset validatedAtUtc, DateTimeOffset expiresAtUtc)
    {
        if (candidate is null || review is null ||
            candidate.EvidenceVersion != 1 ||
            candidate.PreparationId == Guid.Empty || candidate.PlanId == Guid.Empty ||
            candidate.CheckpointId == Guid.Empty ||
            candidate.SourceSnapshotId == Guid.Empty ||
            candidate.SourceSnapshotId == candidate.BaselineSnapshotId ||
            candidate.DestinationConnectionId == Guid.Empty ||
            candidate.SourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            candidate.TargetRepositoryName is not { Length: >= 1 and <= 100 } ||
            candidate.Organization is not { Length: >= 1 and <= 100 } ||
            candidate.SourceRefCount is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            candidate.ObjectFormat is not ("sha1" or "sha256") ||
            !Sha(candidate.EvidenceSha256) ||
            !Sha(candidate.SourceFullRefProofSha256) ||
            !Sha(candidate.SourceManifestRootSha256) ||
            candidate.CanObserveTarget || candidate.CanPush ||
            candidate.CanRetry || candidate.CanDispatch || candidate.MigrationComplete ||
            review.Decision != GitCheckpointTargetHumanReviewStates.ApprovedForObservation ||
            validatedAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc.Offset != TimeSpan.Zero ||
            validatedAtUtc == default ||
            expiresAtUtc <= validatedAtUtc ||
            expiresAtUtc - validatedAtUtc > TimeSpan.FromMinutes(5))
            throw Invalid();

        try { GitCheckpointTargetHumanReviewIntegrity.Verify(review, candidate); }
        catch (GitCheckpointTargetHumanReviewException) { throw Invalid(); }

        var fields = new object?[] {
            "f097c4c0d-checkpoint-target-read-preflight-v1",
            candidate.PreparationId.ToString("D"), candidate.PlanId.ToString("D"),
            candidate.CheckpointId.ToString("D"),
            candidate.SourceSnapshotId.ToString("D"),
            candidate.DestinationConnectionId.ToString("D"),
            candidate.SourceRepositoryId, candidate.TargetRepositoryName,
            candidate.Organization, candidate.ObjectFormat,
            candidate.SourceRefCount, review.ReviewedBy.ToString("D"),
            candidate.EvidenceSha256, review.EvidenceSha256,
            validatedAtUtc.ToUnixTimeMilliseconds(),
            expiresAtUtc.ToUnixTimeMilliseconds(),
            false, false, false, false, false, false
        };
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))))
            .ToLowerInvariant();
        return new(1, candidate.PreparationId, candidate.PlanId,
            candidate.CheckpointId, candidate.SourceSnapshotId,
            candidate.DestinationConnectionId, candidate.SourceRepositoryId,
            candidate.TargetRepositoryName, candidate.Organization,
            candidate.ObjectFormat, candidate.SourceRefCount,
            review.ReviewedBy, candidate.EvidenceSha256,
            review.EvidenceSha256, validatedAtUtc, expiresAtUtc,
            digest, false, false, false, false, false, false);
    }

    public static void Verify(
        GitCheckpointTargetReadPreflight witness,
        GitCheckpointTargetReReviewCandidate candidate,
        GitCheckpointTargetHumanReviewDecision review)
    {
        if (witness is null || witness.EvidenceVersion != 1 ||
            witness.ProviderReadAuthorized || witness.CanObserveTarget ||
            witness.CanPush || witness.CanRetry || witness.CanDispatch ||
            witness.MigrationComplete ||
            witness != Seal(candidate, review, witness.ValidatedAtUtc,
                witness.ExpiresAtUtc))
            throw Invalid();
    }
}
