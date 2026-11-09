using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #113: exact, read-only F-035v2 replay after a committed bundle. Unlike
// a NEW capture, historical read survives F-027 approval revocation but
// NEVER grants live admission, retry, F-061 or remote GitHub permissions.
public sealed class PostgresGitIncrementalLaterCheckpointBundleHistory(
    AppDbContext db,
    PostgresGitIncrementalLaterCheckpointWorkpackHistory provenance,
    IGitSourceBundleArtifactVerifier fileVerifier)
{
    private static GitSourceBundleException Refused(string code) => new(code);

    public async Task<GitIncrementalLaterCheckpointBundleReceipt?> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
        string targetReceiptSha256, Guid recordedBy, CancellationToken ct)
    {
        var row = await db.GitLaterCheckpointBundleEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == workpackId &&
                x.PreparationId == preparationId && x.PlanId == planId &&
                x.CheckpointId == checkpointId &&
                x.TargetObservationId == targetObservationId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (row is null) return null;
        if (recordedBy == Guid.Empty || row.RecordedBy != recordedBy ||
            row.TargetReceiptSha256 != targetReceiptSha256)
            throw Refused("LaterCheckpointBundleReplayScopeChanged");

        var hist = await provenance.ReadAsync(preparationId, planId,
            checkpointId, workpackId, targetObservationId,
            sourceRepositoryId, targetReceiptSha256, ct);
        if (hist is null)
            throw Refused("LaterCheckpointBundleReplayProvenanceMissing");

        var proof = new GitIncrementalLaterCheckpointBundleReceipt(
            1, row.PreparationId, row.PlanId, row.CheckpointId,
            row.SourceSnapshotId, row.TargetObservationId,
            row.WorkpackId, row.SourceRepositoryId,
            row.WorkpackEvidenceSha256, row.BundlePlanSha256,
            row.FullRefProofSha256, row.SourceEvidenceSha256,
            row.SourceManifestRootSha256, row.TargetReceiptSha256,
            row.ReconciliationSha256, row.BundleFileName,
            row.BundleSha256, row.BundleBytes, row.RefCount,
            row.AcquiredAtUtc, row.RecordedAtUtc, row.RecordedBy,
            row.EvidenceSha256, row.ReadyForExecution, row.CanPush,
            row.CanRetry, row.CanDispatch, row.MigrationComplete);
        GitIncrementalLaterCheckpointBundleReceiptIntegrity
            .VerifyHistorical(proof);

        var events = await db.GitLaterCheckpointBundleEvents.AsNoTracking()
            .Where(x => x.WorkpackId == workpackId).Take(2).ToArrayAsync(ct);
        var lease = await db.GitLaterCheckpointBundleLeases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkpackId == workpackId, ct);
        var recorded = await db.GitLaterCheckpointBundleLeaseEvents.AsNoTracking()
            .Where(x => x.WorkpackId == workpackId && x.Type == "Recorded")
            .Take(2).ToArrayAsync(ct);
        if (hist.EvidenceVersion != 2 ||
            hist.SourceSnapshotId != row.SourceSnapshotId ||
            hist.EvidenceSha256 != row.WorkpackEvidenceSha256 ||
            hist.SourceFullRefProofSha256 != row.FullRefProofSha256 ||
            hist.TargetReceiptSha256 != row.TargetReceiptSha256 ||
            hist.ReconciliationSha256 != row.ReconciliationSha256 ||
            hist.SourceRefCount != row.RefCount ||
            hist.ReadyForExecution || hist.CanPush || hist.CanRetry ||
            hist.CanDispatch || hist.MigrationComplete ||
            events.Length != 1 || events[0].Type != "Recorded" ||
            events[0].ActorId != recordedBy ||
            events[0].EvidenceSha256 != row.EvidenceSha256 ||
            events[0].OccurredAtUtc != row.RecordedAtUtc ||
            lease is null || lease.State != "Recorded" ||
            lease.WorkpackId != workpackId ||
            lease.PreparationId != preparationId ||
            lease.PlanId != planId ||
            lease.CheckpointId != checkpointId ||
            lease.SourceSnapshotId != row.SourceSnapshotId ||
            lease.SourceRepositoryId != sourceRepositoryId ||
            lease.TargetObservationId != targetObservationId ||
            lease.BundlePlanSha256 != row.BundlePlanSha256 ||
            lease.WorkpackEvidenceSha256 != row.WorkpackEvidenceSha256 ||
            lease.FullRefProofSha256 != row.FullRefProofSha256 ||
            lease.TargetReceiptSha256 != targetReceiptSha256 ||
            lease.ActorId != recordedBy ||
            lease.LeaseUntilUtc is not null ||
            lease.DurableEvidencePersisted || lease.CanPush ||
            lease.CanRetry || lease.CanDispatch || lease.MigrationComplete ||
            recorded.Length != 1 ||
            recorded[0].Generation != lease.Generation ||
            recorded[0].ActorId != recordedBy ||
            recorded[0].BundlePlanSha256 != row.BundlePlanSha256 ||
            recorded[0].OccurredAtUtc != row.RecordedAtUtc)
            throw Refused("LaterCheckpointBundleReplayIntegrityInvalid");

        // A sealed database SHA is not evidence that a local file still
        // exists unchanged. Rehash it on EVERY replay using the trusted
        // source-only artifact store; never return a success on an orphan
        // file, symlink or changed byte.
        var checkedFile = await fileVerifier.VerifyAsync(new GitSourceBundleView(
            workpackId, planId, Guid.Empty, sourceRepositoryId,
            row.SourceEvidenceSha256, row.ReconciliationSha256,
            row.BundleFileName, row.BundleSha256,
            row.BundleBytes, row.RefCount,
            row.AcquiredAtUtc, false, false), ct);
        if (checkedFile.WorkpackId != workpackId ||
            checkedFile.BundleSha256 != row.BundleSha256 ||
            checkedFile.BundleBytes != row.BundleBytes ||
            checkedFile.IsCurrent || checkedFile.ReadyForExecution)
            throw Refused("LaterCheckpointBundleReplayFileChanged");
        return proof;
    }
}
