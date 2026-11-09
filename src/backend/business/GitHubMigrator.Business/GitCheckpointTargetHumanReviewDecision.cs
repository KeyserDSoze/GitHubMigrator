using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-097c.4c.0b: a separate, EXPLICIT human decision about an exact
// later F-091/F-097 source checkpoint's reviewed mapping. This is not
// an F-031 target observation and grants NO GitHub provider access,
// execution permission, push, retry, dispatch or migration completion.
public static class GitCheckpointTargetHumanReviewStates
{
    public const string ApprovedForObservation = "ApprovedForObservation";
    public const string Rejected = "Rejected";
}

public sealed record GitCheckpointTargetHumanReviewDecision(
    int EvidenceVersion, Guid Id, Guid PreparationId, Guid PlanId,
    Guid CheckpointId, Guid SourceSnapshotId, Guid DestinationConnectionId,
    string SourceRepositoryId, string TargetRepositoryName,
    string CandidateEvidenceSha256, Guid RequestedBy, Guid ReviewedBy,
    string Decision, DateTimeOffset ReviewedAtUtc, string EvidenceSha256,
    bool CanObserveTarget, bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);

public sealed class GitCheckpointTargetHumanReviewException(string code)
    : Exception("Checkpoint-scoped human target review refused")
{
    public string Code { get; } = code;
}

public static class GitCheckpointTargetHumanReviewIntegrity
{
    private static GitCheckpointTargetHumanReviewException Invalid() =>
        new("CheckpointHumanReviewEvidenceInvalid");
    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitCheckpointTargetHumanReviewDecision Seal(
        GitCheckpointTargetReReviewCandidate candidate, Guid decisionId,
        Guid reviewerId, string decision, DateTimeOffset reviewedAtUtc)
    {
        if (candidate is null || candidate.EvidenceVersion != 1 ||
            candidate.PreparationId == Guid.Empty ||
            candidate.PlanId == Guid.Empty ||
            candidate.CheckpointId == Guid.Empty ||
            candidate.SourceSnapshotId == Guid.Empty ||
            candidate.SourceSnapshotId == candidate.BaselineSnapshotId ||
            candidate.DestinationConnectionId == Guid.Empty ||
            candidate.OriginalReviewerId == Guid.Empty ||
            candidate.RequestedBy == Guid.Empty ||
            !Sha(candidate.EvidenceSha256) ||
            !Sha(candidate.SourceFullRefProofSha256) ||
            !Sha(candidate.SourceManifestRootSha256) ||
            candidate.SourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(candidate.SourceRepositoryId) ||
            candidate.TargetRepositoryName is not { Length: >= 1 and <= 100 } ||
            string.IsNullOrWhiteSpace(candidate.TargetRepositoryName) ||
            candidate.CanObserveTarget || candidate.CanPush ||
            candidate.CanRetry || candidate.CanDispatch ||
            candidate.MigrationComplete ||
            decisionId == Guid.Empty || reviewerId == Guid.Empty ||
            reviewerId == candidate.RequestedBy ||
            decision is not (
                GitCheckpointTargetHumanReviewStates.ApprovedForObservation or
                GitCheckpointTargetHumanReviewStates.Rejected) ||
            reviewedAtUtc == default ||
            reviewedAtUtc.Offset != TimeSpan.Zero)
            throw Invalid();

        var digestFields = new object?[] {
            "f097c4c0b-human-reviewed-later-target-mapping-v1",
            decisionId.ToString("D"), candidate.PreparationId.ToString("D"),
            candidate.PlanId.ToString("D"), candidate.CheckpointId.ToString("D"),
            candidate.SourceSnapshotId.ToString("D"),
            candidate.DestinationConnectionId.ToString("D"),
            candidate.SourceRepositoryId, candidate.TargetRepositoryName,
            candidate.EvidenceSha256, candidate.RequestedBy.ToString("D"),
            reviewerId.ToString("D"), decision,
            reviewedAtUtc.ToUnixTimeMilliseconds(),
            false, false, false, false, false
        };
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(digestFields))))
            .ToLowerInvariant();
        return new(1, decisionId, candidate.PreparationId, candidate.PlanId,
            candidate.CheckpointId, candidate.SourceSnapshotId,
            candidate.DestinationConnectionId,
            candidate.SourceRepositoryId, candidate.TargetRepositoryName,
            candidate.EvidenceSha256, candidate.RequestedBy, reviewerId,
            decision, reviewedAtUtc, digest,
            false, false, false, false, false);
    }

    public static void Verify(
        GitCheckpointTargetHumanReviewDecision recorded,
        GitCheckpointTargetReReviewCandidate candidate)
    {
        if (recorded is null || recorded.EvidenceVersion != 1 ||
            recorded.CanObserveTarget || recorded.CanPush ||
            recorded.CanRetry || recorded.CanDispatch ||
            recorded.MigrationComplete ||
            recorded != Seal(candidate, recorded.Id, recorded.ReviewedBy,
                recorded.Decision, recorded.ReviewedAtUtc))
            throw Invalid();
    }
}
