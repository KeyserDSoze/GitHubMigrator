namespace GitHubMigrator.Business;

// F-097c.4b: opt-in local-only bridge from a later verified F-097c.2
// source/target snapshot into the existing F-061 PostgreSQL lease journal.
// No new write authority, remote transport, F-059 runner, F-095 receipt,
// or source-bundle acquisition. The caller MUST independently retrieve
// and verify F-097b/F-031/F-035 under the source scope; #100 owns the
// future source-locked admission of newly created workpacks.
public sealed class DurableGitSourceCheckpointMirrorMaterializer(
    IGitSourceSnapshotMirrorMaterializer physical,
    IGitSourceMirrorLifecycleJournal lifecycle,
    bool enabled = false)
{
    public async Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
        IncrementalOperationPlan plan,
        GitIncrementalCheckpointAuditInputs checkpoint,
        GitHubSourceCheckpointGitRefProof fullRefs,
        GitSourceBundleView bundle, CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceCheckpointLocalMaterializationException(
                "SourceCheckpointDurableMirrorDisabled");

        // Reconstruct the complete bounded source ref/OID set, F-029
        // repository mapping, F-031 target comparison and F-035 bundle
        // binding before acquiring an F-061 generation.
        var workpack = GitSourceCheckpointLocalMaterializationIntegrity.Bind(
            plan, checkpoint, fullRefs, bundle);

        // The hardened F-061 wrapper acquires the distributed PostgreSQL
        // lease, heartbeats, performs a real local F-060 fsck, checks a
        // fresh verification timestamp AFTER lease admission, commits
        // Verified, or quarantines a corrupt mirror/bundle. Never bypass
        // generation, proof conflict, or lease expiry enforcement.
        var durable = new DurableGitSourceSnapshotMirrorMaterializer(
            physical, lifecycle);
        var receipt = await durable.MaterializeAsync(
            workpack.LocalOnlyPlan, ct);

        // F-061 also checks this receipt before committing. Keep an
        // explicit F-097 source identity/false-authority check at this
        // adapter boundary; never return a source from another epoch.
        if (receipt.SourceSnapshotId != workpack.SourceSnapshotId ||
            receipt.PhysicalMirrorId != workpack.PhysicalMirrorId ||
            receipt.WorkpackId != workpack.WorkpackId ||
            receipt.BundleSha256 != bundle.BundleSha256 ||
            receipt.SourceEvidenceSha256 != fullRefs.SourceEvidenceSha256 ||
            receipt.SourceRefSetSha256 !=
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(
                    workpack.LocalOnlyPlan.SourceRefs) ||
            receipt.RemotePushInvoked || receipt.RemoteWritesAuthorized ||
            receipt.MayAutomaticallyRetry || receipt.MigrationComplete)
            throw new GitSourceCheckpointLocalMaterializationException(
                "SourceCheckpointDurableMirrorProofInvalid");
        return receipt;
    }
}
