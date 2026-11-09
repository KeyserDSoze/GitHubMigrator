namespace GitHubMigrator.Business;

// #112: internal local-only, default-OFF source export rehearsal.
// NOT a persisted F-035v2 receipt, worker instruction, lease or GitHub
// permission. Even a successful rehearsal must NEVER go to F-061/F-095
// without a new source-locked PostgreSQL commit and file hash recheck.
public sealed record GitIncrementalLaterCheckpointLocalBundleOptions(bool Enabled);

public sealed record GitIncrementalLaterCheckpointLocalBundleResult(
    Guid WorkpackId, Guid CheckpointId, Guid SourceSnapshotId,
    string ReadPlanSha256, string SourceFullRefProofSha256,
    string TargetReceiptSha256, string BundleFileName,
    string BundleSha256, long BundleBytes, int RefCount,
    DateTimeOffset AcquiredAtUtc, DateTimeOffset RecheckedAtUtc,
    bool DurableEvidencePersisted, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public sealed class GitIncrementalLaterCheckpointLocalBundleAcquisition(
    GitIncrementalLaterCheckpointLocalBundleOptions options,
    IGitIncrementalLaterCheckpointBundleReadPreflight preflight,
    IGitSourceBundleAcquirer acquirer,
    IGitSourceBundleArtifactVerifier verifier,
    TimeProvider clock)
{
    private static GitSourceBundleException Refused(string code) => new(code);

    public async Task<GitIncrementalLaterCheckpointLocalBundleResult> RehearseAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointLocalBundleDisabled");

        var before = await preflight.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256, ct);
        VerifyReadPlan(before, preparationId, planId, checkpointId,
            targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256);

        // This existing local-only acquirer independently checks ALL
        // mirror refs/OIDs, strict git fsck, bundle verify/list-heads, size,
        // quota, symlinks and atomic UUID.bundle promotion. Never pass a
        // request path, GitHub URL or user-provided ref list.
        var artifact = await acquirer.AcquireAsync(before.SourceOnlyRequest, ct);

        // The provider/approval/snapshot may change while Git checks and
        // exports. A second independent live PostgreSQL preflight MUST
        // still agree; an orphan file after refusal is not DB evidence.
        var after = await preflight.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256, ct);
        VerifyReadPlan(after, preparationId, planId, checkpointId,
            targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256);
        var original = before.SourceOnlyRequest;
        var current = after.SourceOnlyRequest;
        if (before.EvidenceSha256 != after.EvidenceSha256 ||
            before.WorkpackEvidenceSha256 != after.WorkpackEvidenceSha256 ||
            before.SourceFullRefProofSha256 != after.SourceFullRefProofSha256 ||
            before.SourceManifestRootSha256 != after.SourceManifestRootSha256 ||
            original.SourceConnectionId != current.SourceConnectionId ||
            original.SourceRepositoryId != current.SourceRepositoryId ||
            original.SourceEvidenceSha256 != current.SourceEvidenceSha256 ||
            original.ReconciliationSha256 != current.ReconciliationSha256 ||
            original.WorkpackId != current.WorkpackId ||
            !original.ExpectedRefs.SequenceEqual(current.ExpectedRefs))
            throw Refused("LaterCheckpointLocalBundleContextChanged");

        if (artifact is null || artifact.WorkpackId != workpackId ||
            artifact.SourceEvidenceSha256 != original.SourceEvidenceSha256 ||
            artifact.BundleFileName != workpackId.ToString("N") + ".bundle" ||
            !GitSourceBundleStorageCheck.ValidSha(artifact.BundleSha256) ||
            artifact.BundleBytes is < 1 or > 536_870_912 ||
            artifact.RefCount != original.ExpectedRefs.Count)
            throw Refused("LaterCheckpointLocalBundleArtifactInvalid");

        // Rehash the ACTUAL generated file, not only the adapter's
        // in-memory digest. No source/target GitHub transport is possible.
        var verified = await verifier.VerifyAsync(new GitSourceBundleView(
            workpackId, planId, Guid.Empty, sourceRepositoryId,
            artifact.SourceEvidenceSha256,
            original.ReconciliationSha256, artifact.BundleFileName,
            artifact.BundleSha256, artifact.BundleBytes, artifact.RefCount,
            artifact.AcquiredAtUtc, false, false), ct);
        if (verified.WorkpackId != workpackId ||
            verified.BundleSha256 != artifact.BundleSha256 ||
            verified.BundleBytes != artifact.BundleBytes ||
            verified.IsCurrent || verified.ReadyForExecution)
            throw Refused("LaterCheckpointLocalBundleFileUnverified");

        return new(workpackId, checkpointId, before.SourceSnapshotId,
            before.EvidenceSha256, before.SourceFullRefProofSha256,
            before.TargetReceiptSha256, artifact.BundleFileName,
            artifact.BundleSha256, artifact.BundleBytes, artifact.RefCount,
            artifact.AcquiredAtUtc, clock.GetUtcNow(), false,
            false, false, false, false);
    }

    private static void VerifyReadPlan(
        GitIncrementalLaterCheckpointBundlePlan? plan,
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId,
        string sourceRepositoryId, string expectedTargetReceiptSha256)
    {
        if (plan is null || plan.EvidenceVersion != 1 ||
            plan.PreparationId != preparationId ||
            plan.PlanId != planId ||
            plan.CheckpointId != checkpointId ||
            plan.TargetObservationId != targetObservationId ||
            plan.SourceSnapshotId == Guid.Empty ||
            plan.SourceSnapshotId == plan.BaselineSnapshotId ||
            plan.WorkpackEvidenceSha256 is not { Length: 64 } ||
            !GitSourceBundleStorageCheck.ValidSha(plan.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.SourceFullRefProofSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.SourceManifestRootSha256) ||
            plan.TargetReceiptSha256 != expectedTargetReceiptSha256 ||
            plan.SourceOnlyRequest is null ||
            plan.SourceOnlyRequest.WorkpackId != workpackId ||
            plan.SourceOnlyRequest.SourceConnectionId == Guid.Empty ||
            plan.SourceOnlyRequest.SourceRepositoryId != sourceRepositoryId ||
            !GitSourceBundleStorageCheck.ValidSha(
                plan.SourceOnlyRequest.SourceEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                plan.SourceOnlyRequest.ReconciliationSha256) ||
            plan.SourceOnlyRequest.ExpectedRefs is not { Count: >= 1 and <= 128 } ||
            plan.CanAcquireBundle || plan.CanPush || plan.CanRetry ||
            plan.CanDispatch || plan.MigrationComplete)
            throw Refused("LaterCheckpointLocalBundlePlanInvalid");

        try
        {
            var refs = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                plan.SourceOnlyRequest.ExpectedRefs);
            if (!refs.SequenceEqual(plan.SourceOnlyRequest.ExpectedRefs) ||
                refs.Any(x => x.ObjectId.Length != refs[0].ObjectId.Length))
                throw Refused("LaterCheckpointLocalBundlePlanInvalid");
        }
        catch (GitMirrorRefReconciliationException)
        { throw Refused("LaterCheckpointLocalBundlePlanInvalid"); }
    }
}
