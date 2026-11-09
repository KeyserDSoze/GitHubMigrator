using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// #114: inspect an ALREADY EXISTING physical later-checkpoint mirror.
// No Git import, F-061 lease, database write, network transport or API.
// The caller must first use the live, source-locked F-091/F-097b/F-031
// PostgreSQL preflight; matching a checksum alone is not provenance.
public sealed class LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
    string bundleRoot, string mirrorRoot, bool enabled = false,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<GitIncrementalLaterCheckpointMirrorOfflineAudit> AuditAsync(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOfflineAuditDisabled");

        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(stage, durable);

        try
        {
            // The bundle store accepts a UUID, NOT a caller path, and
            // rejects wrong SHA/length, symlinks and unsafe root ancestry.
            var bundles = new LocalGitBundleArtifactStore(bundleRoot);
            var safeBundleRoot = bundles.Root;
            if (string.IsNullOrWhiteSpace(mirrorRoot) ||
                !Path.IsPathFullyQualified(mirrorRoot) ||
                Path.GetFullPath(safeBundleRoot) == Path.GetFullPath(mirrorRoot))
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOfflineRootsInvalid");

            var before = await bundles.InspectExistingAsync(
                stage.WorkpackId, ct);
            RequireBundle(before, stage);

            // F-056 performs strict self-contained bare-Git fsck,
            // exact source ref/OID comparison and bounded full reachable
            // object closure. It rejects corrupt or non-closed mirrors.
            var first = await LocalGitObjectClosureAuditor.VerifyOneAsync(
                mirrorRoot, stage.PhysicalMirrorId, stage.SourceRefs, ct);
            var second = await LocalGitObjectClosureAuditor.VerifyOneAsync(
                mirrorRoot, stage.PhysicalMirrorId, stage.SourceRefs, ct);
            if (first != second)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOfflineClosureChanged");

            var after = await bundles.InspectExistingAsync(
                stage.WorkpackId, ct);
            RequireBundle(after, stage);
            if (before != after)
                throw new GitSourceSnapshotMirrorException(
                    "LaterCheckpointMirrorOfflineBundleChanged");

            var receipt = GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
                .Seal(stage, durable, second.Digest, second.Count,
                    _clock.GetUtcNow());
            GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
                .Verify(receipt, stage, durable);
            return receipt;
        }
        catch (GitSourceBundleException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOfflineBundleInvalid");
        }
        catch (GitObjectClosureAuditException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOfflineClosureInvalid");
        }
        catch (IOException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOfflineIoFailure");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOfflineIoFailure");
        }
    }

    private static void RequireBundle(
        (string Sha256, long Bytes) measured,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage)
    {
        if (measured.Sha256 != stage.BundleSha256 ||
            measured.Bytes != stage.BundleBytes)
            throw new GitSourceSnapshotMirrorException(
                "LaterCheckpointMirrorOfflineBundleChanged");
    }
}
