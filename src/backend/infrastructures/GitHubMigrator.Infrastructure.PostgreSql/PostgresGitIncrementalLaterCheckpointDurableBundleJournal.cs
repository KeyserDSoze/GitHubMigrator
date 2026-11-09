using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #113 internal ONLY, three independently disabled switches:
// bundle-read + local Git rehearsal + durable F-035v2 commit. Never
// exposed by HTTP, workers, remote transports or import admission.
public sealed record GitIncrementalLaterCheckpointDurableBundleOptions(bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointDurableBundleJournal(
    GitIncrementalLaterCheckpointDurableBundleOptions options,
    PostgresGitIncrementalLaterCheckpointBundleLeaseStore leaseStore,
    PostgresGitIncrementalLaterCheckpointBundleHistory history,
    GitIncrementalLaterCheckpointLocalBundleAcquisition local,
    IGitSourceBundleArtifactVerifier fileVerifier)
{
    public async Task<GitIncrementalLaterCheckpointBundleReceipt> CaptureAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, Guid actorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw new GitSourceBundleException(
                "LaterCheckpointDurableBundleDisabled");
        // Restart/idempotence checks the exact, immutable stored receipt
        // and on-disk bytes BEFORE requesting a new lease. This historical
        // replay never asserts current approval or re-runs Git.
        var previouslyRecorded = await history.ReadAsync(preparationId,
            planId, checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256, actorId, ct);
        if (previouslyRecorded is not null)
            return previouslyRecorded;

        var lease = await leaseStore.ClaimAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId, sourceRepositoryId,
            expectedTargetReceiptSha256, actorId,
            PostgresGitIncrementalLaterCheckpointBundleLeaseStore.MaxLease, ct);
        try
        {
            // Never run git/fsck/bundle creation inside an SQL transaction.
            var result = await local.RehearseAsync(preparationId, planId,
                checkpointId, targetObservationId, workpackId,
                sourceRepositoryId, expectedTargetReceiptSha256, ct);
            return await leaseStore.CommitAsync(lease, result,
                preparationId, planId, checkpointId, targetObservationId,
                sourceRepositoryId, expectedTargetReceiptSha256,
                fileVerifier, ct);
        }
        catch
        {
            // A failed or cancelled export may leave UUID.bundle on disk;
            // it is NOT F-035v2 evidence. Only the same live lease may
            // release itself. A crashed or expired lease remains quarantined
            // until explicit audited recovery (never silently adopted).
            try
            {
                await leaseStore.ReleaseAsync(workpackId, lease.LeaseToken,
                    lease.Generation, actorId, lease.BundlePlanSha256,
                    CancellationToken.None);
            }
            catch (Exception) { /* Preserve the original failure. */ }
            throw;
        }
    }
}
