using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// #123: OFF by default and deliberately NOT registered in API DI.
// This is only a local physical primitive. A separate future
// source-locked PostgreSQL coordinator MUST authenticate/recheck the
// approved #122 decision, record the rename and fence any new F-061
// generation. The caller can NEVER make this receipt authoritative.
public sealed class LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
    string bundleRoot, string mirrorRoot,
    bool enabled = false, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Read-only crash-diagnosis primitive. This method never restores
    // UUID.git, creates a new lease or returns authority. The only
    // filesystem path is derived from the reviewed decision SHA.
    public async Task<(int Count, string ObjectSetSha256)> AuditIsolatedAsync(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationDisabled");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Verify(
            decision, inspection);
        if (decision.Decision !=
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview ||
            inspection.PhysicalState !=
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Intact ||
            inspection.PhysicalMirrorId != stage.PhysicalMirrorId ||
            inspection.WorkpackId != stage.WorkpackId ||
            inspection.SourceSnapshotId != stage.SourceSnapshotId ||
            inspection.CheckpointId != stage.CheckpointId ||
            inspection.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            inspection.BundleSha256 != durable.BundleSha256 ||
            inspection.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            inspection.FullRefProofSha256 != stage.SourceFullRefProofSha256 ||
            inspection.SourceRefSetSha256 != stage.SourceRefSetSha256 ||
            !GitSourceBundleStorageCheck.ValidSha(inspection.ObjectSetSha256) ||
            inspection.ObjectCount is null or < 1)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationScopeInvalid");
        var root = LocalGitSourceSnapshotMirrorMaterializer
            .CheckMirrorRoot(mirrorRoot);
        var store = new LocalGitBundleArtifactStore(bundleRoot);
        if (string.Equals(root, store.Root, StringComparison.Ordinal))
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationRootsOverlap");
        await LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.WaitAsync(ct);
        try
        {
            await using var rootLock =
                await LocalGitSourceSnapshotMirrorMaterializer
                    .AcquireRootLockAsync(root, ct);
            var original = Path.Combine(root,
                stage.PhysicalMirrorId.ToString("N") + ".git");
            if (Directory.Exists(original) || File.Exists(original) ||
                new DirectoryInfo(original).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationOriginalPresent");
            var before = await store.InspectExistingAsync(stage.WorkpackId, ct);
            if (before.Sha256 != durable.BundleSha256 ||
                before.Bytes != durable.BundleBytes)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationBundleChanged");
            (int Count, string Digest) checkedGraph;
            try
            {
                checkedGraph =
                    await LocalGitObjectClosureAuditor.VerifyQuarantinedOneAsync(
                        root, stage.PhysicalMirrorId,
                        decision.EvidenceSha256, stage.SourceRefs, ct);
            }
            catch (GitObjectClosureAuditException)
            {
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationClosureInvalid");
            }
            var after = await store.InspectExistingAsync(stage.WorkpackId, ct);
            if (before != after ||
                checkedGraph.Count != inspection.ObjectCount ||
                checkedGraph.Digest != inspection.ObjectSetSha256)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationPhysicalChanged");
            return (checkedGraph.Count, checkedGraph.Digest);
        }
        catch (GitSourceBundleException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationBundleInvalid");
        }
        finally
        {
            LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.Release();
        }
    }

    public async Task<GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt>
        IsolateAsync(
            GitIncrementalLaterCheckpointMirrorStagingPlan stage,
            GitIncrementalLaterCheckpointBundleReceipt durable,
            GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
            GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
            CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationDisabled");

        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Verify(
            decision, inspection);
        if (decision.Decision !=
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview ||
            inspection.PhysicalState !=
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                    .Intact ||
            inspection.PhysicalMirrorId != stage.PhysicalMirrorId ||
            inspection.SourceSnapshotId != stage.SourceSnapshotId ||
            inspection.CheckpointId != stage.CheckpointId ||
            inspection.WorkpackId != stage.WorkpackId ||
            inspection.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            inspection.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            inspection.BundleSha256 != durable.BundleSha256 ||
            inspection.FullRefProofSha256 != stage.SourceFullRefProofSha256 ||
            inspection.SourceRefSetSha256 != stage.SourceRefSetSha256 ||
            !GitSourceBundleStorageCheck.ValidSha(
                inspection.ObjectSetSha256) ||
            inspection.ObjectCount is null or < 1 ||
            inspection.CanReAdmit || inspection.CanMaterialize ||
            inspection.CanPush || inspection.CanRetry ||
            inspection.CanDispatch || inspection.MigrationComplete)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationScopeInvalid");

        var root = LocalGitSourceSnapshotMirrorMaterializer
            .CheckMirrorRoot(mirrorRoot);
        var checkedBundleRoot = new LocalGitBundleArtifactStore(bundleRoot).Root;
        if (string.Equals(root, checkedBundleRoot, StringComparison.Ordinal))
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationRootsOverlap");
        var basename = stage.PhysicalMirrorId.ToString("N") + ".git";
        var isolatedName =
            GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                .IsolationName(decision);
        var original = Path.Combine(root, basename);
        var isolated = Path.Combine(root, isolatedName);

        // Share exactly the F-060 importer process+POSIX volume lock.
        // No other importer in this application or POSIX process may
        // recreate/remove the physical UUID.git while we inspect+move.
        await LocalGitSourceSnapshotMirrorMaterializer
            .ProcessGate.WaitAsync(ct);
        try
        {
            await using var volumeLock =
                await LocalGitSourceSnapshotMirrorMaterializer
                    .AcquireRootLockAsync(root, ct);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(isolated) || File.Exists(isolated) ||
                new DirectoryInfo(isolated).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationAlreadyPresent");
            if (!Directory.Exists(original) || File.Exists(original) ||
                new DirectoryInfo(original).LinkTarget is not null ||
                (File.GetAttributes(original) &
                    FileAttributes.ReparsePoint) != 0)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationSourceInvalid");

            // Rehash UUID.bundle and independently double-fsck every
            // source branch/tag/object closure WHILE the same POSIX
            // lock protects the final path. The old inspection's SHA
            // is not accepted as physical proof without this reread.
            var auditor =
                new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
                    bundleRoot, mirrorRoot, enabled: true, clock: _clock);
            var now = await auditor.AuditAsync(stage, durable, ct);
            if (now.ObjectSetSha256 != inspection.ObjectSetSha256 ||
                now.ObjectCount != inspection.ObjectCount ||
                now.PhysicalMirrorId != inspection.PhysicalMirrorId)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationPhysicalChanged");

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(isolated) || File.Exists(isolated))
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationAlreadyPresent");
            try
            {
                // Same-volume atomic rename. NO deletion, no foreign
                // path, no overwrite and no Git invocation after move.
                Directory.Move(original, isolated);
            }
            catch (IOException)
            {
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationMoveFailed");
            }
            if (Directory.Exists(original) || !Directory.Exists(isolated) ||
                new DirectoryInfo(isolated).LinkTarget is not null)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOrphanIsolationMoveFailed");
            var receipt =
                GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                    .Seal(inspection, decision, isolatedName,
                        _clock.GetUtcNow());
            GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity.Verify(
                receipt, inspection, decision);
            return receipt;
        }
        catch (GitSourceBundleException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationBundleInvalid");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOrphanIsolationLocalIoFailure");
        }
        finally
        {
            LocalGitSourceSnapshotMirrorMaterializer.ProcessGate.Release();
        }
    }
}
