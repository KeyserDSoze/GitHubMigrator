namespace GitHubMigrator.Business;

// #120. Only trusted server-side stages and F-035v2 historical receipts
// can reach the local Git importer; never accept user-supplied paths/refs.
public interface IGitIncrementalLaterCheckpointMirrorImporter
{
    Task<GitIncrementalLaterCheckpointMirrorOfflineAudit>
        MaterializeLaterCheckpointAsync(
            GitIncrementalLaterCheckpointMirrorStagingPlan stage,
            GitIncrementalLaterCheckpointBundleReceipt durable,
            CancellationToken ct);
}
