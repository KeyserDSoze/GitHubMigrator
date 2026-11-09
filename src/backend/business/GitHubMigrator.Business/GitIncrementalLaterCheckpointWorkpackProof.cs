using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #104: separate F-032v2/F-038v2 sealed, permanently WriteBlocked proof.
// V1 workpack hashes and the legacy target F-031 ID are NEVER reused.
// This is not a capability token: a PostgreSQL writer must independently
// verify latest F-091/F-097b and reviewed checkpoint F-031 under source lock.
public sealed record GitIncrementalLaterCheckpointWorkpackProof(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid BaselineSnapshotId, Guid SourceSnapshotId, Guid CheckpointId,
    string SourceRepositoryId, string TargetRepositoryName,
    Guid CheckpointTargetObservationId, string OperationManifestSha256,
    string SourceEvidenceSha256, string SourceFullRefProofSha256,
    string SourceManifestRootSha256, string TargetReceiptSha256,
    Guid ReconciliationId, string ReconciliationSha256,
    Guid WorkpackId, Guid RecordedBy, string State, string Phase,
    string EvidenceSha256, bool ReadyForExecution, bool CanPush,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointWorkpackIntegrity
{
    private static GitIncrementalCheckpointWorkpackException Invalid() =>
        new("LaterCheckpointWorkpackEvidenceInvalid");

    private static bool Sha(string? sha) => sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static GitIncrementalLaterCheckpointWorkpackProof Seal(
        IncrementalOperationPlan plan, GitIncrementalCheckpointAuditInputs selected,
        GitHubSourceCheckpointGitRefProof source,
        GitMirrorRefReconciliation comparison,
        Guid reconciliationId, Guid workpackId, Guid actorId)
    {
        if (plan is null || selected is null || source is null ||
            comparison is null ||
            selected.EvidenceVersion != 2 ||
            plan.Id == Guid.Empty || plan.PreparationId == Guid.Empty ||
            plan.SourceConnectionId == Guid.Empty || plan.SnapshotId == Guid.Empty ||
            plan.IsCurrent || plan.ReadyForExecution ||
            plan.Id != selected.PlanId ||
            plan.PreparationId != selected.PreparationId ||
            plan.SourceConnectionId != selected.SourceConnectionId ||
            selected.SourceSnapshotId == Guid.Empty ||
            selected.SourceSnapshotId == plan.SnapshotId ||
            selected.SourceCheckpointId == Guid.Empty ||
            selected.TargetObservationId == Guid.Empty ||
            !Sha(selected.TargetEvidenceSha256) ||
            !Sha(selected.SourceFullRefProofSha256) ||
            !Sha(selected.SourceCheckpointAttestationSha256) ||
            selected.CanPush || selected.CanRetry ||
            selected.CanDispatch || selected.MigrationComplete ||
            source.EvidenceVersion != 1 ||
            source.PreparationId != plan.PreparationId ||
            source.PlanId != plan.Id ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            source.CheckpointId != selected.SourceCheckpointId ||
            source.SnapshotId != selected.SourceSnapshotId ||
            source.BaselineSnapshotId != plan.SnapshotId ||
            source.PreviousCheckpointId is null ||
            !Sha(source.PreviousFullRefProofSha256) ||
            source.CheckpointRefAttestationSha256 !=
                selected.SourceCheckpointAttestationSha256 ||
            source.EvidenceSha256 != selected.SourceFullRefProofSha256 ||
            source.SourceRepositoryId != selected.SourceRepositoryId ||
            !Sha(source.SourceEvidenceSha256) ||
            !Sha(source.SourceManifestRootSha256) ||
            source.CanPush || source.CanRetry ||
            source.CanDispatch || source.MigrationComplete ||
            comparison.OperationPlanId != plan.Id ||
            comparison.ReadyForExecution ||
            !Sha(comparison.ReconciliationSha256) ||
            reconciliationId == Guid.Empty || workpackId == Guid.Empty ||
            actorId == Guid.Empty ||
            selected.ExpectedSourceRefs is not { Count: >= 1 } ||
            selected.ExpectedTargetRefs is not { Count: >= 1 } ||
            selected.ExpectedSourceRefs.Count > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            selected.ExpectedTargetRefs.Count > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            source.Refs is null ||
            !source.Refs.SequenceEqual(selected.ExpectedSourceRefs) ||
            plan.Steps.Count(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == source.SourceRepositoryId &&
                x.TargetRepositoryName == selected.TargetRepositoryName) != 1)
            throw Invalid();

        GitMirrorRefReconciliation recomputed;
        try
        {
            recomputed = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
                selected.TargetEvidenceSha256,
                selected.ExpectedSourceRefs, selected.ExpectedTargetRefs));
        }
        catch (GitMirrorRefReconciliationException) { throw Invalid(); }
        if (recomputed.ReconciliationSha256 != comparison.ReconciliationSha256 ||
            !recomputed.Items.SequenceEqual(comparison.Items) ||
            recomputed.SourceRefCount != comparison.SourceRefCount ||
            recomputed.TargetRefCount != comparison.TargetRefCount ||
            recomputed.CreateCandidateCount != comparison.CreateCandidateCount ||
            recomputed.DivergedCount != comparison.DivergedCount)
            throw Invalid();

        var evidence = new GitIncrementalLaterCheckpointWorkpackProof(
            2, plan.PreparationId, plan.Id, plan.SnapshotId,
            selected.SourceSnapshotId, selected.SourceCheckpointId,
            selected.SourceRepositoryId, selected.TargetRepositoryName,
            selected.TargetObservationId, plan.ManifestSha256,
            source.SourceEvidenceSha256, source.EvidenceSha256,
            source.SourceManifestRootSha256, selected.TargetEvidenceSha256,
            reconciliationId, recomputed.ReconciliationSha256,
            workpackId, actorId, "Blocked", "WriteBlocked",
            "", false, false, false, false, false);
        return evidence with { EvidenceSha256 = Digest(evidence) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointWorkpackProof proof,
        IncrementalOperationPlan plan, GitIncrementalCheckpointAuditInputs selected,
        GitHubSourceCheckpointGitRefProof source,
        GitMirrorRefReconciliation comparison)
    {
        if (proof is null || proof.EvidenceVersion != 2 ||
            proof.State != "Blocked" || proof.Phase != "WriteBlocked" ||
            proof.ReadyForExecution || proof.CanPush || proof.CanRetry ||
            proof.CanDispatch || proof.MigrationComplete ||
            proof != Seal(plan, selected, source, comparison,
                proof.ReconciliationId, proof.WorkpackId, proof.RecordedBy))
            throw Invalid();
    }

    private static string Digest(GitIncrementalLaterCheckpointWorkpackProof p)
    {
        var fields = new object?[] {
            "f097c4c1-checkpoint-target-f031-v2-writeblocked",
            p.EvidenceVersion, p.PreparationId.ToString("D"), p.PlanId.ToString("D"),
            p.BaselineSnapshotId.ToString("D"), p.SourceSnapshotId.ToString("D"),
            p.CheckpointId.ToString("D"), p.SourceRepositoryId, p.TargetRepositoryName,
            p.CheckpointTargetObservationId.ToString("D"),
            p.OperationManifestSha256, p.SourceEvidenceSha256,
            p.SourceFullRefProofSha256, p.SourceManifestRootSha256,
            p.TargetReceiptSha256, p.ReconciliationId.ToString("D"),
            p.ReconciliationSha256, p.WorkpackId.ToString("D"),
            p.RecordedBy.ToString("D"), p.State, p.Phase,
            p.ReadyForExecution, p.CanPush, p.CanRetry,
            p.CanDispatch, p.MigrationComplete
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))));
    }
}
