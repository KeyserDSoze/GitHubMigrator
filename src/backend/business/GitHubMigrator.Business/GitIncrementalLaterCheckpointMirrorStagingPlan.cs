using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #100: safe DOMAIN-ONLY F-060v2 staging description sourced from the
// independently verified F-035v2 physical bundle and F-097b OID chain.
// Intentionally NOT a GitHubTrustedRemoteReadPlan: legacy F-060 expects
// the original *current* F-029 baseline, which MUST remain stale for
// later epochs. This contract does not authorize F-061 or any filesystem
// activity and cannot be interpreted as a lease/capability.
public sealed record GitIncrementalLaterCheckpointMirrorStagingPlan(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid BaselineSnapshotId, Guid SourceSnapshotId, Guid CheckpointId,
    Guid TargetObservationId, Guid WorkpackId, Guid PhysicalMirrorId,
    Guid SourceConnectionId, string SourceRepositoryId,
    string TargetRepositoryName, string GitObjectFormat,
    string SourceFullRefProofSha256, string SourceManifestRootSha256,
    string TargetReceiptSha256, string ReconciliationSha256,
    string WorkpackEvidenceSha256, string BundlePlanSha256,
    string BundleEvidenceSha256, string BundleSha256, long BundleBytes,
    string SourceRefSetSha256, IReadOnlyList<GitMirrorRef> SourceRefs,
    string EvidenceSha256, long F061LeaseGeneration,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorStagingIntegrity
{
    private const string Domain = "f097c4-f060v2-later-checkpoint-staging-v1";
    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorStagingInvalid");

    public static GitIncrementalLaterCheckpointMirrorStagingPlan Prepare(
        IncrementalOperationPlan plan, GitIncrementalCheckpointAuditInputs selection,
        GitHubSourceCheckpointGitRefProof source,
        GitIncrementalLaterCheckpointBundlePlan prepared,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointWorkpackProof sealedWorkpack)
    {
        if (plan is null || selection is null || source is null ||
            prepared is null || durable is null ||
            sealedWorkpack is null ||
            source.Refs is null ||
            selection.ExpectedSourceRefs is null ||
            selection.ExpectedTargetRefs is null)
            throw Refused();
        try
        {
            GitIncrementalLaterCheckpointBundleReceiptIntegrity
                .VerifyHistorical(durable);
        }
        catch (GitSourceBundleException) { throw Refused(); }

        IReadOnlyList<GitMirrorRef> refs;
        GitMirrorRefReconciliation comparison;
        try
        {
            refs = GitMirrorRefReconciler.CanonicalizeObservedRefs(source.Refs);
            comparison = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
                selection.TargetEvidenceSha256, selection.ExpectedSourceRefs,
                selection.ExpectedTargetRefs));
        }
        catch (GitMirrorRefReconciliationException) { throw Refused(); }

        // Self-sealed SHA strings are not enough: bind the complete
        // immutable F-038v2 workpack and independently recompute the
        // original F-032v2 reconciliation, full F-035v2 bundle-plan
        // digest and all real F-097b source object IDs.
        try
        {
            GitIncrementalLaterCheckpointWorkpackIntegrity.Verify(
                sealedWorkpack, plan, selection, source, comparison);
            GitIncrementalLaterCheckpointBundleIntegrity.Verify(
                prepared, plan, selection, source, comparison, sealedWorkpack);
        }
        catch (Exception ex) when (ex is
            GitIncrementalCheckpointWorkpackException or
            GitSourceBundleException)
        { throw Refused(); }

        var request = prepared.SourceOnlyRequest;
        if (plan.IsCurrent || plan.ReadyForExecution ||
            plan.PreparationId == Guid.Empty || plan.Id == Guid.Empty ||
            plan.SourceConnectionId == Guid.Empty ||
            plan.SnapshotId == Guid.Empty ||
            selection.EvidenceVersion != 2 ||
            selection.PreparationId != plan.PreparationId ||
            selection.PlanId != plan.Id ||
            selection.SourceConnectionId != plan.SourceConnectionId ||
            selection.SourceSnapshotId == Guid.Empty ||
            selection.SourceSnapshotId == plan.SnapshotId ||
            selection.SourceCheckpointId == Guid.Empty ||
            selection.TargetObservationId == Guid.Empty ||
            selection.CanPush || selection.CanRetry ||
            selection.CanDispatch || selection.MigrationComplete ||
            source.EvidenceVersion != 1 ||
            source.CheckpointId != selection.SourceCheckpointId ||
            source.PreparationId != plan.PreparationId ||
            source.PlanId != plan.Id ||
            source.SourceConnectionId != plan.SourceConnectionId ||
            source.BaselineSnapshotId != plan.SnapshotId ||
            source.SnapshotId != selection.SourceSnapshotId ||
            source.PreviousCheckpointId is null ||
            source.SourceRepositoryId != selection.SourceRepositoryId ||
            source.EvidenceSha256 != selection.SourceFullRefProofSha256 ||
            source.CheckpointRefAttestationSha256 !=
                selection.SourceCheckpointAttestationSha256 ||
            source.CanPush || source.CanRetry ||
            source.CanDispatch || source.MigrationComplete ||
            refs.Count is < 1 or > 128 ||
            !refs.SequenceEqual(source.Refs) ||
            selection.ExpectedSourceRefs is null ||
            !refs.SequenceEqual(selection.ExpectedSourceRefs) ||
            selection.ExpectedTargetRefs is null ||
            selection.GitObjectFormat !=
                (refs[0].ObjectId.Length == 40 ? "sha1" : "sha256") ||
            refs.Any(x => x.ObjectId.Length != refs[0].ObjectId.Length) ||
            plan.Steps.Count(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == selection.SourceRepositoryId &&
                x.TargetRepositoryName == selection.TargetRepositoryName) != 1 ||
            comparison.ReadyForExecution ||
            prepared.EvidenceVersion != 1 ||
            prepared.PreparationId != plan.PreparationId ||
            prepared.PlanId != plan.Id ||
            prepared.BaselineSnapshotId != plan.SnapshotId ||
            prepared.WorkpackEvidenceSha256 != sealedWorkpack.EvidenceSha256 ||
            prepared.SourceSnapshotId != selection.SourceSnapshotId ||
            prepared.CheckpointId != selection.SourceCheckpointId ||
            prepared.TargetObservationId != selection.TargetObservationId ||
            prepared.TargetReceiptSha256 != selection.TargetEvidenceSha256 ||
            prepared.SourceFullRefProofSha256 != source.EvidenceSha256 ||
            prepared.SourceManifestRootSha256 != source.SourceManifestRootSha256 ||
            request is null ||
            request.WorkpackId == Guid.Empty ||
            request.SourceConnectionId != plan.SourceConnectionId ||
            request.SourceRepositoryId != source.SourceRepositoryId ||
            request.SourceEvidenceSha256 != source.SourceEvidenceSha256 ||
            request.ReconciliationSha256 != comparison.ReconciliationSha256 ||
            request.ExpectedRefs is null ||
            !request.ExpectedRefs.SequenceEqual(refs) ||
            !GitSourceBundleStorageCheck.ValidSha(prepared.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(prepared.WorkpackEvidenceSha256) ||
            prepared.CanAcquireBundle || prepared.CanPush ||
            prepared.CanRetry || prepared.CanDispatch ||
            prepared.MigrationComplete ||
            durable.EvidenceVersion != 1 ||
            durable.PreparationId != plan.PreparationId ||
            durable.PlanId != plan.Id ||
            durable.CheckpointId != selection.SourceCheckpointId ||
            durable.SourceSnapshotId != selection.SourceSnapshotId ||
            durable.TargetObservationId != selection.TargetObservationId ||
            durable.WorkpackId != request.WorkpackId ||
            durable.SourceRepositoryId != source.SourceRepositoryId ||
            durable.WorkpackEvidenceSha256 != prepared.WorkpackEvidenceSha256 ||
            durable.BundlePlanSha256 != prepared.EvidenceSha256 ||
            durable.FullRefProofSha256 != source.EvidenceSha256 ||
            durable.SourceEvidenceSha256 != source.SourceEvidenceSha256 ||
            durable.SourceManifestRootSha256 != source.SourceManifestRootSha256 ||
            durable.TargetReceiptSha256 != selection.TargetEvidenceSha256 ||
            durable.ReconciliationSha256 != comparison.ReconciliationSha256 ||
            durable.RefCount != refs.Count ||
            durable.BundleFileName != request.WorkpackId.ToString("N") + ".bundle" ||
            durable.ReadyForExecution || durable.CanPush ||
            durable.CanRetry || durable.CanDispatch || durable.MigrationComplete)
            throw Refused();

        var mirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
            selection.SourceSnapshotId, request.WorkpackId);
        var refSetSha = GitSourceSnapshotMirrorIntegrity.SourceRefsSha(refs);
        var candidate = new GitIncrementalLaterCheckpointMirrorStagingPlan(
            1, plan.PreparationId, plan.Id, plan.SnapshotId,
            selection.SourceSnapshotId, selection.SourceCheckpointId,
            selection.TargetObservationId, request.WorkpackId,
            mirrorId, plan.SourceConnectionId, source.SourceRepositoryId,
            selection.TargetRepositoryName, selection.GitObjectFormat,
            source.EvidenceSha256, source.SourceManifestRootSha256,
            selection.TargetEvidenceSha256, comparison.ReconciliationSha256,
            prepared.WorkpackEvidenceSha256, prepared.EvidenceSha256,
            durable.EvidenceSha256, durable.BundleSha256,
            durable.BundleBytes, refSetSha, refs.ToArray(), "",
            0, false, false, false, false, false);
        return candidate with { EvidenceSha256 = Digest(candidate) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorStagingPlan observed,
        IncrementalOperationPlan plan, GitIncrementalCheckpointAuditInputs selection,
        GitHubSourceCheckpointGitRefProof source,
        GitIncrementalLaterCheckpointBundlePlan prepared,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointWorkpackProof sealedWorkpack)
    {
        if (observed is null || observed.SourceRefs is null ||
            observed.F061LeaseGeneration != 0 ||
            observed.CanMaterialize || observed.CanPush ||
            observed.CanRetry || observed.CanDispatch ||
            observed.MigrationComplete)
            throw Refused();
        var expected = Prepare(plan, selection, source, prepared,
            durable, sealedWorkpack);
        if (observed with { SourceRefs = expected.SourceRefs } != expected ||
            !observed.SourceRefs.SequenceEqual(expected.SourceRefs))
            throw Refused();
    }

    private static string Digest(GitIncrementalLaterCheckpointMirrorStagingPlan p)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(object?[] fields) => h.AppendData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields) + "\n"));
        Add(new object?[] {
            Domain, p.EvidenceVersion, p.PreparationId.ToString("D"),
            p.PlanId.ToString("D"), p.BaselineSnapshotId.ToString("D"),
            p.SourceSnapshotId.ToString("D"), p.CheckpointId.ToString("D"),
            p.TargetObservationId.ToString("D"), p.WorkpackId.ToString("D"),
            p.PhysicalMirrorId.ToString("D"), p.SourceConnectionId.ToString("D"),
            p.SourceRepositoryId, p.TargetRepositoryName, p.GitObjectFormat,
            p.SourceFullRefProofSha256, p.SourceManifestRootSha256,
            p.TargetReceiptSha256, p.ReconciliationSha256,
            p.WorkpackEvidenceSha256, p.BundlePlanSha256,
            p.BundleEvidenceSha256, p.BundleSha256, p.BundleBytes,
            p.SourceRefSetSha256, p.SourceRefs.Count,
            p.F061LeaseGeneration, false, false, false, false, false
        });
        foreach (var x in p.SourceRefs)
            Add(new object?[] { x.Name, x.ObjectId });
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}
