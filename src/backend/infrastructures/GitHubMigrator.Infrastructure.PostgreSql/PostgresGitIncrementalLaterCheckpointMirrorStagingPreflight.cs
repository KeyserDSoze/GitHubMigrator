using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #100: a separate, default-OFF, non-authorizing F-060v2 read
// preflight for a genuine later F-091/F-097b source snapshot.
// Unlike legacy F-060 it MUST NOT require F-029 IsCurrent, synthesize
// "current" baseline F-035 records or create an F-061 lease.
// It does not spawn git or write to either filesystem or GitHub.
public sealed record GitIncrementalLaterCheckpointMirrorStagingOptions(
    bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorStagingOptions options,
    IGitIncrementalLaterCheckpointBundleReadPreflight liveBundles,
    PostgresGitIncrementalLaterCheckpointBundleHistory durableHistory,
    PostgresGitIncrementalLaterCheckpointAuditPreflight liveAudit,
    PostgresGitHubSourceCheckpointGitRefLedger sourceLedger,
    IIncrementalOperationPlanRepository plans)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    public async Task<GitIncrementalLaterCheckpointMirrorStagingPlan> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, Guid recordedBy,
        CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorStagingDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            workpackId == Guid.Empty || recordedBy == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !GitSourceBundleStorageCheck.ValidSha(
                expectedTargetReceiptSha256))
            throw Refused("LaterCheckpointMirrorStagingInvalidScope");

        GitIncrementalLaterCheckpointBundlePlan bundlePlan;
        GitIncrementalLaterCheckpointBundleReceipt durableReceipt;
        GitIncrementalCheckpointAuditInputs selected;
        GitHubSourceCheckpointGitRefProof? source;
        IncrementalOperationPlan? plan;
        try
        {
            // Live source-first review/target + F-038v2 must still agree
            // before reading an historical physical F-035v2 receipt.
            bundlePlan = await liveBundles.ReadAsync(preparationId,
                planId, checkpointId, targetObservationId, workpackId,
                sourceRepositoryId, expectedTargetReceiptSha256, ct);
            durableReceipt = await durableHistory.ReadAsync(preparationId,
                planId, checkpointId, targetObservationId, workpackId,
                sourceRepositoryId, expectedTargetReceiptSha256,
                recordedBy, ct) ??
                throw Refused("LaterCheckpointMirrorStagingBundleMissing");
            selected = await liveAudit.ReadAsync(preparationId, planId,
                checkpointId, targetObservationId, sourceRepositoryId,
                expectedTargetReceiptSha256, ct);
            source = await sourceLedger.GetVerifiedAsync(preparationId,
                planId, checkpointId, sourceRepositoryId, ct);
            plan = await plans.GetAsync(preparationId, planId, ct);
        }
        catch (Exception ex) when (ex is GitSourceBundleException or
            GitIncrementalLaterCheckpointHistoryException or
            GitIncrementalCheckpointPreflightException or
            GitHubSourceCheckpointGitRefLedgerException)
        { throw Refused("LaterCheckpointMirrorStagingProvenanceUnverified"); }

        if (source is null || plan is null ||
            selected.EvidenceVersion != 2 ||
            selected.SourceSnapshotId != source.SnapshotId ||
            selected.SourceFullRefProofSha256 != source.EvidenceSha256 ||
            selected.TargetObservationId != targetObservationId ||
            selected.TargetEvidenceSha256 != expectedTargetReceiptSha256 ||
            bundlePlan.SourceSnapshotId != source.SnapshotId ||
            bundlePlan.SourceFullRefProofSha256 != source.EvidenceSha256 ||
            bundlePlan.TargetReceiptSha256 != expectedTargetReceiptSha256 ||
            durableReceipt.WorkpackId != workpackId ||
            durableReceipt.WorkpackEvidenceSha256 !=
                bundlePlan.WorkpackEvidenceSha256 ||
            durableReceipt.BundlePlanSha256 != bundlePlan.EvidenceSha256 ||
            durableReceipt.FullRefProofSha256 != source.EvidenceSha256 ||
            durableReceipt.RecordedBy != recordedBy)
            throw Refused("LaterCheckpointMirrorStagingContextChanged");

        var row = await db.GitLaterCheckpointWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == workpackId &&
                x.PreparationId == preparationId && x.PlanId == planId &&
                x.CheckpointId == checkpointId &&
                x.TargetObservationId == targetObservationId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (row is null || row.EvidenceSha256 !=
            durableReceipt.WorkpackEvidenceSha256 ||
            row.State != "Blocked" || row.Phase != "WriteBlocked" ||
            row.ReadyForExecution || row.CanPush || row.CanRetry ||
            row.CanDispatch || row.MigrationComplete)
            throw Refused("LaterCheckpointMirrorStagingWorkpackMissing");

        GitIncrementalLaterCheckpointWorkpackProof sealedPack;
        GitIncrementalLaterCheckpointMirrorStagingPlan stage;
        try
        {
            sealedPack = JsonSerializer.Deserialize<
                GitIncrementalLaterCheckpointWorkpackProof>(row.ReceiptJson)
                ?? throw new JsonException();
            stage = GitIncrementalLaterCheckpointMirrorStagingIntegrity.Prepare(
                plan, selected, source, bundlePlan,
                durableReceipt, sealedPack);
        }
        catch (Exception ex) when (ex is JsonException or
            GitSourceSnapshotMirrorException)
        { throw Refused("LaterCheckpointMirrorStagingIntegrityInvalid"); }

        // Reverify the live gate AFTER reading and rehashing the saved
        // binary, but DO NOT acquire an F-061 lease or authorize a Git
        // subprocess: the later durable importer must recheck under its
        // own source-first lease-fenced PostgreSQL transaction.
        GitIncrementalLaterCheckpointBundlePlan final;
        try
        {
            final = await liveBundles.ReadAsync(preparationId,
                planId, checkpointId, targetObservationId, workpackId,
                sourceRepositoryId, expectedTargetReceiptSha256, ct);
        }
        catch (GitSourceBundleException)
        { throw Refused("LaterCheckpointMirrorStagingApprovalChanged"); }

        if (final.EvidenceSha256 != bundlePlan.EvidenceSha256 ||
            final.WorkpackEvidenceSha256 != bundlePlan.WorkpackEvidenceSha256 ||
            final.SourceSnapshotId != stage.SourceSnapshotId ||
            !final.SourceOnlyRequest.ExpectedRefs.SequenceEqual(stage.SourceRefs) ||
            stage.F061LeaseGeneration != 0 ||
            stage.CanMaterialize || stage.CanPush || stage.CanRetry ||
            stage.CanDispatch || stage.MigrationComplete)
            throw Refused("LaterCheckpointMirrorStagingApprovalChanged");
        return stage;
    }
}
