using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #112: internal, default-OFF READ-ONLY source-only F-035v2
// preparation. This is not a file-acquisition lease or permission.
// The future physical writer MUST revalidate everything while holding
// the source lock across its own fenced, bounded filesystem operation.
public sealed record GitIncrementalLaterCheckpointBundleReadOptions(bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointBundleReadPreflight(
    AppDbContext db,
    GitIncrementalLaterCheckpointBundleReadOptions options,
    PostgresGitIncrementalLaterCheckpointWorkpackHistory historical,
    PostgresGitIncrementalLaterCheckpointAuditPreflight live,
    PostgresGitHubSourceCheckpointGitRefLedger fullRefs,
    IIncrementalOperationPlanRepository plans)
    : IGitIncrementalLaterCheckpointBundleReadPreflight
{
    private static GitSourceBundleException Refused(string code) => new(code);

    public async Task<GitIncrementalLaterCheckpointBundlePlan> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
        string expectedTargetReceiptSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointBundleReadDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            workpackId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            expectedTargetReceiptSha256 is not { Length: 64 } ||
            expectedTargetReceiptSha256.Any(c => c is not
                (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Refused("LaterCheckpointBundleReadInvalidScope");

        GitIncrementalLaterCheckpointHistory? savedHistory;
        GitIncrementalCheckpointAuditInputs gate;
        GitHubSourceCheckpointGitRefProof? source;
        IncrementalOperationPlan? plan;
        try
        {
            savedHistory = await historical.ReadAsync(preparationId, planId,
                checkpointId, workpackId, targetObservationId,
                sourceRepositoryId, expectedTargetReceiptSha256, ct);
            if (savedHistory is null)
                throw Refused("LaterCheckpointBundleReadNotFound");

            // The verified historical F-031/F-038 receipt alone is NEVER
            // sufficient to acquire a bundle after revocation or expiry.
            gate = await live.ReadAsync(preparationId, planId, checkpointId,
                targetObservationId, sourceRepositoryId,
                expectedTargetReceiptSha256, ct);
            source = await fullRefs.GetVerifiedAsync(preparationId, planId,
                checkpointId, sourceRepositoryId, ct);
            plan = await plans.GetAsync(preparationId, planId, ct);
        }
        catch (Exception ex) when (ex is
            GitIncrementalLaterCheckpointHistoryException or
            GitIncrementalCheckpointPreflightException or
            GitHubSourceCheckpointGitRefLedgerException)
        { throw Refused("LaterCheckpointBundleReadProvenanceUnverified"); }

        if (plan is null || source is null || savedHistory is null ||
            plan.IsCurrent || plan.ReadyForExecution ||
            gate.EvidenceVersion != 2 ||
            gate.SourceFullRefProofSha256 != source.EvidenceSha256 ||
            gate.SourceSnapshotId != source.SnapshotId ||
            gate.SourceCheckpointId != checkpointId ||
            gate.TargetObservationId != targetObservationId ||
            gate.TargetEvidenceSha256 != savedHistory.TargetReceiptSha256 ||
            gate.SourceRepositoryId != sourceRepositoryId ||
            savedHistory.SourceFullRefProofSha256 != source.EvidenceSha256 ||
            savedHistory.CheckpointId != checkpointId ||
            savedHistory.WorkpackId != workpackId)
            throw Refused("LaterCheckpointBundleReadContextChanged");

        var row = await db.GitLaterCheckpointWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == workpackId &&
                x.PreparationId == preparationId && x.PlanId == planId &&
                x.CheckpointId == checkpointId &&
                x.TargetObservationId == targetObservationId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (row is null || row.EvidenceSha256 != savedHistory.EvidenceSha256 ||
            row.TargetReceiptSha256 != expectedTargetReceiptSha256 ||
            row.FullRefProofSha256 != source.EvidenceSha256 ||
            row.ReadyForExecution || row.CanPush || row.CanRetry ||
            row.CanDispatch || row.MigrationComplete || row.Attempts != 0 ||
            row.State != "Blocked" || row.Phase != "WriteBlocked")
            throw Refused("LaterCheckpointBundleReadWorkpackChanged");

        GitIncrementalLaterCheckpointWorkpackProof proof;
        GitMirrorRefReconciliation compared;
        try
        {
            proof = JsonSerializer.Deserialize<
                GitIncrementalLaterCheckpointWorkpackProof>(row.ReceiptJson)
                ?? throw new JsonException();
            compared = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
                gate.TargetEvidenceSha256,
                gate.ExpectedSourceRefs, gate.ExpectedTargetRefs));
            GitIncrementalLaterCheckpointWorkpackIntegrity.Verify(
                proof, plan, gate, source, compared);
        }
        catch (Exception ex) when (ex is JsonException or
            GitMirrorRefReconciliationException or
            GitIncrementalCheckpointWorkpackException)
        { throw Refused("LaterCheckpointBundleReadWorkpackInvalid"); }

        // No filesystem/network side effects. The result contains exact
        // immutable source refs/OIDs for a future trusted local acquirer
        // but is NOT an executable authority.
        var prepared = GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
            plan, gate, source, compared, proof);
        if (prepared.CanAcquireBundle || prepared.CanPush ||
            prepared.CanRetry || prepared.CanDispatch ||
            prepared.MigrationComplete)
            throw Refused("LaterCheckpointBundleReadAuthorityInvalid");
        return prepared;
    }
}
