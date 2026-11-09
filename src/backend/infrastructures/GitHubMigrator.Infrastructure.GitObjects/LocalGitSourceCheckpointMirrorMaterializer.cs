using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-097c.4a: off-by-default, LOCAL source-only adaptation to the
// already hardened F-060 bundle importer. Does not call F-059,
// contact GitHub, acquire an F-061 lease or create an F-095 receipt.
public sealed class LocalGitSourceCheckpointMirrorMaterializer(
    IGitSourceSnapshotMirrorMaterializer importer, bool enabled = false)
{
    public async Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
        IncrementalOperationPlan plan,
        GitIncrementalCheckpointAuditInputs checkpoint,
        GitHubSourceCheckpointGitRefProof fullRefs,
        GitSourceBundleView bundle, CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceCheckpointLocalMaterializationException(
                "SourceCheckpointLocalMaterializationDisabled");
        var workpack = GitSourceCheckpointLocalMaterializationIntegrity.Bind(
            plan, checkpoint, fullRefs, bundle);
        var receipt = await importer.MaterializeAsync(
            workpack.LocalOnlyPlan, ct);
        GitSourceSnapshotMirrorProof expected;
        try
        {
            expected = GitSourceSnapshotMirrorIntegrity.Seal(
                workpack.LocalOnlyPlan, receipt.ObjectSetSha256,
                receipt.VerifiedObjectCount, receipt.VerifiedAtUtc);
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw new GitSourceCheckpointLocalMaterializationException(
                "SourceCheckpointLocalMirrorProofInvalid");
        }
        if (receipt != expected ||
            receipt.SourceSnapshotId != workpack.SourceSnapshotId ||
            receipt.PhysicalMirrorId != workpack.PhysicalMirrorId ||
            receipt.WorkpackId != workpack.WorkpackId ||
            receipt.SourceRefSetSha256 !=
                GitSourceSnapshotMirrorIntegrity.SourceRefsSha(fullRefs.Refs) ||
            receipt.RemotePushInvoked || receipt.RemoteWritesAuthorized ||
            receipt.MayAutomaticallyRetry || receipt.MigrationComplete)
            throw new GitSourceCheckpointLocalMaterializationException(
                "SourceCheckpointLocalMirrorProofInvalid");
        return receipt;
    }
}
