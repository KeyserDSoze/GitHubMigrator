using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-097c.4c.0a: a read-only, non-executable eligibility witness.
// In particular this is NOT a target F-031 observation, cannot mint a
// credential, cannot bypass human checkpoint review and cannot grant
// permission to call the destination provider. F-029 IsCurrent stays false.
public sealed record GitCheckpointTargetReReviewCandidate(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid BaselineSnapshotId, Guid SourceSnapshotId, Guid SourceConnectionId,
    Guid DestinationConnectionId, Guid CheckpointId,
    string SourceRepositoryId, string TargetRepositoryName, string Organization,
    string OperationManifestSha256, string OriginalReviewSha256,
    Guid OriginalReviewerId, string CheckpointAttestationSha256,
    string SourceFullRefProofSha256, string SourceManifestRootSha256,
    string ObjectFormat, int SourceRefCount, Guid RequestedBy,
    string EvidenceSha256,
    bool CanObserveTarget, bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);

public sealed class GitCheckpointTargetReReviewException(string code)
    : Exception("Later checkpoint target rereview prerequisite refused")
{
    public string Code { get; } = code;
}

public static class GitCheckpointTargetReReviewIntegrity
{
    private static GitCheckpointTargetReReviewException Invalid() =>
        new("CheckpointTargetReReviewEvidenceInvalid");
    private static bool Sha(string? sha) =>
        sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitCheckpointTargetReReviewCandidate Seal(
        IncrementalOperationPlan plan,
        GitHubSourceSyncCheckpointReceipt checkpoint,
        GitHubSourceCheckpointGitRefProof source,
        Guid originalReviewerId, Guid requestedBy)
    {
        if (plan is null || checkpoint is null || source is null ||
            plan.Id == Guid.Empty || plan.PreparationId == Guid.Empty ||
            plan.SnapshotId == Guid.Empty ||
            plan.SourceConnectionId == Guid.Empty ||
            plan.DestinationConnectionId == Guid.Empty ||
            plan.ReadyForExecution || plan.IsCurrent ||
            originalReviewerId == Guid.Empty || requestedBy == Guid.Empty ||
            checkpoint.EvidenceVersion != 2 ||
            checkpoint.Proof.PreparationId != plan.PreparationId ||
            checkpoint.Proof.PlanId != plan.Id ||
            checkpoint.Proof.SourceConnectionId != plan.SourceConnectionId ||
            checkpoint.Proof.ManifestSha256 != plan.ManifestSha256 ||
            checkpoint.Proof.BaselineSnapshotId != plan.SnapshotId ||
            checkpoint.Proof.SnapshotId == plan.SnapshotId ||
            checkpoint.Proof.PreviousCheckpointId is null ||
            !Sha(checkpoint.RefChangeAttestationSha256) ||
            source.EvidenceVersion != 1 ||
            source.CheckpointId != checkpoint.Id ||
            source.PreparationId != plan.PreparationId ||
            source.PlanId != plan.Id ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            source.SnapshotId != checkpoint.Proof.SnapshotId ||
            source.BaselineSnapshotId != plan.SnapshotId ||
            source.CheckpointEvidenceSha256 != checkpoint.EvidenceSha256 ||
            source.CheckpointRefAttestationSha256 !=
                checkpoint.RefChangeAttestationSha256 ||
            source.PreviousCheckpointId != checkpoint.Proof.PreviousCheckpointId ||
            !Sha(source.PreviousFullRefProofSha256) ||
            !Sha(source.EvidenceSha256) ||
            !Sha(source.SourceManifestRootSha256) ||
            source.CanPush || source.CanRetry ||
            source.CanDispatch || source.MigrationComplete ||
            source.Refs is null ||
            source.Refs.Count is < 1 or >
                GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            !Sha(plan.ManifestSha256) || !Sha(plan.ReviewSha256) ||
            plan.Organization is not { Length: >= 1 and <= 100 })
            throw Invalid();
        GitMirrorRef[] refs;
        try
        {
            refs = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                source.Refs).ToArray();
        }
        catch (GitMirrorRefReconciliationException) { throw Invalid(); }
        var oidLength = refs[0].ObjectId.Length;
        if (!refs.SequenceEqual(source.Refs) ||
            refs.Any(x => x.ObjectId.Length != oidLength) ||
            oidLength is not (40 or 64))
            throw Invalid();
        var mapping = plan.Steps.Where(x => x.Kind == "ReviewTargetName" &&
            x.SourceRepositoryId == source.SourceRepositoryId).ToArray();
        if (mapping.Length != 1 ||
            mapping[0].TargetRepositoryName is not
                { Length: >= 1 and <= 100 })
            throw Invalid();
        var target = mapping[0].TargetRepositoryName;
        var ids = new object?[] {
            "f097c4c0a-source-checkpoint-target-rereview-candidate-v1",
            plan.PreparationId.ToString("D"), plan.Id.ToString("D"),
            plan.SnapshotId.ToString("D"),
            checkpoint.Proof.SnapshotId.ToString("D"),
            plan.SourceConnectionId.ToString("D"),
            plan.DestinationConnectionId.ToString("D"),
            checkpoint.Id.ToString("D"), source.SourceRepositoryId,
            target, plan.Organization, plan.ManifestSha256, plan.ReviewSha256,
            originalReviewerId.ToString("D"),
            checkpoint.RefChangeAttestationSha256, source.EvidenceSha256,
            source.SourceManifestRootSha256,
            oidLength == 40 ? "sha1" : "sha256", refs.Length,
            requestedBy.ToString("D"), false, false, false, false, false
        };
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ids))))
            .ToLowerInvariant();
        return new(1, plan.PreparationId, plan.Id, plan.SnapshotId,
            checkpoint.Proof.SnapshotId, plan.SourceConnectionId,
            plan.DestinationConnectionId, checkpoint.Id,
            source.SourceRepositoryId, target, plan.Organization,
            plan.ManifestSha256, plan.ReviewSha256, originalReviewerId,
            checkpoint.RefChangeAttestationSha256!, source.EvidenceSha256,
            source.SourceManifestRootSha256, oidLength == 40 ? "sha1" : "sha256",
            refs.Length, requestedBy, digest,
            false, false, false, false, false);
    }

    public static void Verify(
        GitCheckpointTargetReReviewCandidate candidate,
        IncrementalOperationPlan plan, GitHubSourceSyncCheckpointReceipt checkpoint,
        GitHubSourceCheckpointGitRefProof source,
        Guid reviewerId)
    {
        if (candidate is null || candidate.EvidenceVersion != 1 ||
            candidate.CanObserveTarget || candidate.CanPush ||
            candidate.CanRetry || candidate.CanDispatch ||
            candidate.MigrationComplete ||
            candidate != Seal(plan, checkpoint, source,
                reviewerId, candidate.RequestedBy))
            throw Invalid();
    }
}
