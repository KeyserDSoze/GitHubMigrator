namespace GitHubMigrator.Business;

// F-097c.4a: a non-executable, local-only F-060 bundle workpack
// for a GENUINELY later F-091/F-097 source snapshot. All arguments
// must originate in independently verified PostgreSQL records (F-097c.2,
// F-097b, F-029 and F-035). Not a remote GitHub read plan or permit.
public sealed record GitSourceCheckpointLocalMaterializationWorkpack(
    int EvidenceVersion, Guid SourceCheckpointId,
    string SourceFullRefProofSha256, string SourceManifestRootSha256,
    Guid SourceSnapshotId, Guid PhysicalMirrorId, Guid WorkpackId,
    GitHubTrustedRemoteReadPlan LocalOnlyPlan,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public sealed class GitSourceCheckpointLocalMaterializationException(string code)
    : Exception("Later checkpoint source Git materialization refused")
{
    public string Code { get; } = code;
}

public static class GitSourceCheckpointLocalMaterializationIntegrity
{
    private static GitSourceCheckpointLocalMaterializationException Invalid() =>
        new("SourceCheckpointLocalWorkpackInvalid");

    private static bool Sha(string? sha) =>
        sha is { Length: 64 } &&
        sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // This is a pure *mapping* of already verified F-097/F-031/F-035
    // evidence into the existing safe F-060 local bundle importer.
    // It MUST NOT be exposed as an HTTP endpoint or passed to F-059.
    public static GitSourceCheckpointLocalMaterializationWorkpack Bind(
        IncrementalOperationPlan plan,
        GitIncrementalCheckpointAuditInputs checkpoint,
        GitHubSourceCheckpointGitRefProof fullRefProof,
        GitSourceBundleView bundle)
    {
        if (plan is null || checkpoint is null ||
            fullRefProof is null || bundle is null ||
            checkpoint.EvidenceVersion != 1 ||
            fullRefProof.EvidenceVersion != 1 ||
            !plan.IsCurrent || plan.ReadyForExecution ||
            plan.Id == Guid.Empty || plan.PreparationId == Guid.Empty ||
            plan.SourceConnectionId == Guid.Empty ||
            plan.SnapshotId == Guid.Empty ||
            checkpoint.PreparationId != plan.PreparationId ||
            checkpoint.PlanId != plan.Id ||
            checkpoint.SourceConnectionId != plan.SourceConnectionId ||
            checkpoint.SourceSnapshotId == Guid.Empty ||
            checkpoint.SourceSnapshotId == plan.SnapshotId ||
            checkpoint.SourceCheckpointId == Guid.Empty ||
            !Sha(checkpoint.SourceCheckpointAttestationSha256) ||
            !Sha(checkpoint.SourceFullRefProofSha256) ||
            !Sha(checkpoint.TargetEvidenceSha256) ||
            checkpoint.CanPush || checkpoint.CanRetry ||
            checkpoint.CanDispatch || checkpoint.MigrationComplete ||
            checkpoint.GitObjectFormat is not ("sha1" or "sha256") ||
            fullRefProof.CheckpointId != checkpoint.SourceCheckpointId ||
            fullRefProof.EvidenceSha256 !=
                checkpoint.SourceFullRefProofSha256 ||
            fullRefProof.CheckpointRefAttestationSha256 !=
                checkpoint.SourceCheckpointAttestationSha256 ||
            fullRefProof.PreparationId != plan.PreparationId ||
            fullRefProof.PlanId != plan.Id ||
            fullRefProof.SourceConnectionId != plan.SourceConnectionId ||
            fullRefProof.BaselineSnapshotId != plan.SnapshotId ||
            fullRefProof.SnapshotId != checkpoint.SourceSnapshotId ||
            fullRefProof.PreviousCheckpointId is null ||
            !Sha(fullRefProof.PreviousFullRefProofSha256) ||
            !Sha(fullRefProof.SourceEvidenceSha256) ||
            !Sha(fullRefProof.SourceManifestRootSha256) ||
            fullRefProof.SourceRepositoryId != checkpoint.SourceRepositoryId ||
            fullRefProof.CanPush || fullRefProof.CanRetry ||
            fullRefProof.CanDispatch || fullRefProof.MigrationComplete ||
            bundle.WorkpackId == Guid.Empty ||
            bundle.PlanId != plan.Id ||
            bundle.ReconciliationId == Guid.Empty ||
            bundle.SourceRepositoryId != checkpoint.SourceRepositoryId ||
            bundle.SourceEvidenceSha256 != fullRefProof.SourceEvidenceSha256 ||
            bundle.RefCount is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            bundle.RefCount != fullRefProof.Refs?.Count ||
            !Sha(bundle.ReconciliationSha256) ||
            !Sha(bundle.BundleSha256) ||
            bundle.BundleBytes is < 1 or > 536_870_912 ||
            bundle.AcquiredAtUtc == default ||
            bundle.BundleFileName != bundle.WorkpackId.ToString("N") + ".bundle" ||
            !bundle.IsCurrent || bundle.ReadyForExecution ||
            checkpoint.ExpectedSourceRefs is null ||
            checkpoint.ExpectedTargetRefs is null ||
            fullRefProof.Refs is null ||
            !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == checkpoint.SourceRepositoryId &&
                x.TargetRepositoryName == checkpoint.TargetRepositoryName) ||
            plan.Steps.Count(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == checkpoint.SourceRepositoryId &&
                x.TargetRepositoryName == checkpoint.TargetRepositoryName) != 1)
            throw Invalid();

        try
        {
            var source = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                checkpoint.ExpectedSourceRefs);
            var target = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                checkpoint.ExpectedTargetRefs);
            if (source.Count is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
                target.Count is < 1 or > GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
                !source.SequenceEqual(checkpoint.ExpectedSourceRefs) ||
                !target.SequenceEqual(checkpoint.ExpectedTargetRefs) ||
                !source.SequenceEqual(fullRefProof.Refs) ||
                source.Any(x => x.ObjectId.Length !=
                    (checkpoint.GitObjectFormat == "sha1" ? 40 : 64)) ||
                target.Any(x => x.ObjectId.Length !=
                    (checkpoint.GitObjectFormat == "sha1" ? 40 : 64)))
                throw Invalid();

            var compared = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256,
                fullRefProof.SourceEvidenceSha256,
                checkpoint.TargetEvidenceSha256, source, target));
            if (compared.ReconciliationSha256 != bundle.ReconciliationSha256 ||
                compared.ReadyForExecution)
                throw Invalid();

            // This existing F-060 importer accepts ONLY source-side
            // UUID.bundle artifacts. No remote GitHub transport is used.
            var mirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                checkpoint.SourceSnapshotId, bundle.WorkpackId);
            var local = new GitHubTrustedRemoteReadPlan(
                checkpoint.SourceSnapshotId,
                plan.Organization, checkpoint.TargetRepositoryName,
                fullRefProof.SourceEvidenceSha256, source.ToArray(),
                checkpoint.TargetEvidenceSha256,
                compared.ReconciliationSha256,
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(source),
                fullRefProof.EvidenceSha256)
            {
                SourceMirrorId = mirrorId,
                SourceBundle = bundle
            };
            GitSourceSnapshotMirrorIntegrity.RequirePlan(local);
            return new(1, checkpoint.SourceCheckpointId,
                fullRefProof.EvidenceSha256,
                fullRefProof.SourceManifestRootSha256,
                checkpoint.SourceSnapshotId, mirrorId,
                bundle.WorkpackId, local,
                false, false, false, false);
        }
        catch (Exception ex) when (ex is
            GitMirrorRefReconciliationException or
            GitSourceSnapshotMirrorException)
        {
            throw Invalid();
        }
    }
}
