using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-097c.4c.1: a new v1, non-executable, SHA-256 sealed witness.
// Deliberately NOT an F-029 baseline F-032/F-038 receipt; never grant
// target GitHub send, retry, dispatch or migration-complete authority.
public sealed record GitIncrementalCheckpointWorkpackProof(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid BaselineSnapshotId, Guid SourceSnapshotId,
    Guid CheckpointId, string SourceRepositoryId,
    string TargetRepositoryName, Guid TargetObservationId,
    string OperationManifestSha256, string SourceEvidenceSha256,
    string FullRefProofSha256, string SourceManifestRootSha256,
    string TargetEvidenceSha256, Guid ReconciliationId,
    string ReconciliationSha256, Guid WorkpackId, Guid RecordedBy,
    string EvidenceSha256,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public sealed class GitIncrementalCheckpointWorkpackException(string code)
    : Exception("Incremental checkpoint workpack proof refused")
{
    public string Code { get; } = code;
}

public static class GitIncrementalCheckpointWorkpackIntegrity
{
    private static GitIncrementalCheckpointWorkpackException Invalid() =>
        new("CheckpointWorkpackEvidenceInvalid");

    private static bool Sha(string? sha) =>
        sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitIncrementalCheckpointWorkpackProof Seal(
        IncrementalOperationPlan plan,
        GitIncrementalCheckpointAuditInputs selected,
        GitHubSourceCheckpointGitRefProof source,
        GitMirrorRefReconciliation compared,
        Guid reconciliationId, Guid workpackId, Guid actorId)
    {
        if (plan is null || selected is null || source is null ||
            compared is null || selected.EvidenceVersion != 1 ||
            plan.Id == Guid.Empty || plan.SnapshotId == Guid.Empty ||
            plan.PreparationId == Guid.Empty ||
            !plan.IsCurrent || plan.ReadyForExecution ||
            selected.PlanId != plan.Id ||
            selected.PreparationId != plan.PreparationId ||
            selected.SourceConnectionId != plan.SourceConnectionId ||
            selected.SourceSnapshotId == Guid.Empty ||
            selected.SourceSnapshotId == plan.SnapshotId ||
            selected.SourceCheckpointId == Guid.Empty ||
            !Sha(selected.SourceCheckpointAttestationSha256) ||
            !Sha(selected.SourceFullRefProofSha256) ||
            !Sha(selected.TargetEvidenceSha256) ||
            selected.TargetObservationId == Guid.Empty ||
            selected.CanPush || selected.CanRetry ||
            selected.CanDispatch || selected.MigrationComplete ||
            source.EvidenceVersion != 1 ||
            source.CheckpointId != selected.SourceCheckpointId ||
            source.PreparationId != plan.PreparationId ||
            source.PlanId != plan.Id ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            source.SnapshotId != selected.SourceSnapshotId ||
            source.BaselineSnapshotId != plan.SnapshotId ||
            source.CheckpointRefAttestationSha256 !=
                selected.SourceCheckpointAttestationSha256 ||
            source.EvidenceSha256 != selected.SourceFullRefProofSha256 ||
            source.SourceRepositoryId != selected.SourceRepositoryId ||
            !Sha(source.SourceEvidenceSha256) ||
            !Sha(source.SourceManifestRootSha256) ||
            source.PreviousCheckpointId is null ||
            !Sha(source.PreviousFullRefProofSha256) ||
            source.CanPush || source.CanRetry ||
            source.CanDispatch || source.MigrationComplete ||
            compared.OperationPlanId != plan.Id ||
            compared.ReadyForExecution ||
            !Sha(compared.ReconciliationSha256) ||
            reconciliationId == Guid.Empty || workpackId == Guid.Empty ||
            actorId == Guid.Empty ||
            selected.ExpectedSourceRefs is null ||
            selected.ExpectedTargetRefs is null ||
            selected.ExpectedSourceRefs.Count is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            selected.ExpectedTargetRefs.Count is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            source.Refs is null ||
            !source.Refs.SequenceEqual(selected.ExpectedSourceRefs) ||
            !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == selected.SourceRepositoryId &&
                x.TargetRepositoryName == selected.TargetRepositoryName) ||
            plan.Steps.Count(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == selected.SourceRepositoryId &&
                x.TargetRepositoryName == selected.TargetRepositoryName) != 1)
            throw Invalid();

        GitMirrorRefReconciliation recomputed;
        try
        {
            recomputed = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256,
                source.SourceEvidenceSha256, selected.TargetEvidenceSha256,
                selected.ExpectedSourceRefs, selected.ExpectedTargetRefs));
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw Invalid();
        }
        if (recomputed != compared &&
            (recomputed.ReconciliationSha256 != compared.ReconciliationSha256 ||
             !recomputed.Items.SequenceEqual(compared.Items)))
            throw Invalid();

        var ids = new object?[] {
            "f097c4c1-immutable-source-checkpoint-workpack-v1",
            plan.PreparationId.ToString("D"), plan.Id.ToString("D"),
            plan.SnapshotId.ToString("D"),
            selected.SourceSnapshotId.ToString("D"),
            selected.SourceCheckpointId.ToString("D"),
            selected.SourceRepositoryId, selected.TargetRepositoryName,
            selected.TargetObservationId.ToString("D"),
            plan.ManifestSha256, source.SourceEvidenceSha256,
            source.EvidenceSha256, source.SourceManifestRootSha256,
            selected.TargetEvidenceSha256,
            reconciliationId.ToString("D"), recomputed.ReconciliationSha256,
            workpackId.ToString("D"), actorId.ToString("D"),
            false, false, false, false
        };
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ids))))
            .ToLowerInvariant();
        return new(1, plan.PreparationId, plan.Id, plan.SnapshotId,
            selected.SourceSnapshotId, selected.SourceCheckpointId,
            selected.SourceRepositoryId, selected.TargetRepositoryName,
            selected.TargetObservationId, plan.ManifestSha256,
            source.SourceEvidenceSha256, source.EvidenceSha256,
            source.SourceManifestRootSha256, selected.TargetEvidenceSha256,
            reconciliationId, recomputed.ReconciliationSha256, workpackId,
            actorId, digest, false, false, false, false);
    }

    public static void Verify(
        GitIncrementalCheckpointWorkpackProof stored,
        IncrementalOperationPlan plan, GitIncrementalCheckpointAuditInputs selected,
        GitHubSourceCheckpointGitRefProof source, GitMirrorRefReconciliation compared)
    {
        if (stored is null || stored.EvidenceVersion != 1 ||
            stored.CanPush || stored.CanRetry || stored.CanDispatch ||
            stored.MigrationComplete ||
            stored != Seal(plan, selected, source, compared,
                stored.ReconciliationId, stored.WorkpackId, stored.RecordedBy))
            throw Invalid();
    }
}
