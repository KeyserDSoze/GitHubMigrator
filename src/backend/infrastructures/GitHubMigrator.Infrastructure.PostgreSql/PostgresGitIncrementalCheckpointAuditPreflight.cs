using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097c.2: a bounded source/target evidence preflight only.
// No local Git paths, remote URLs, HTTP endpoints, sender, credentials,
// lease grants, F-094 physical audit or F-095 INSERT are available here.
// For future admission the F-095 writer MUST recheck everything after a
// source-scoped lock, including physical F-060/F-061 provenance.
public sealed class PostgresGitIncrementalCheckpointAuditPreflight(
    AppDbContext db, TimeProvider clock,
    GitIncrementalCheckpointPreflightOptions options,
    IIncrementalOperationPlanRepository plans,
    IGitHubSourceSyncCheckpointJournal checkpoints,
    PostgresGitHubSourceCheckpointGitRefLedger fullRefLedger)
{
    private static GitIncrementalCheckpointPreflightException Refused(
        string code) => new(code);

    private static bool Sha(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<GitIncrementalCheckpointAuditInputs> ReadAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetEvidenceSha256, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("GitCheckpointAuditPreflightDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !Sha(expectedTargetEvidenceSha256))
            throw Refused("GitCheckpointAuditPreflightInvalidScope");

        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("GitCheckpointAuditPreflightPlanNotFound");
        if (!plan.IsCurrent || plan.ReadyForExecution ||
            !plan.Steps.Any(x => x.Kind == "ReviewTargetName" &&
                x.SourceRepositoryId == sourceRepositoryId))
            throw Refused("GitCheckpointAuditPreflightPlanNotCurrent");

        var checkpoint = await checkpoints.GetVerifiedAsync(
            preparationId, planId, checkpointId, ct)
            ?? throw Refused("GitCheckpointAuditPreflightCheckpointNotFound");
        if (checkpoint.EvidenceVersion != 2 ||
            checkpoint.Proof.SnapshotId == Guid.Empty ||
            checkpoint.Proof.BaselineSnapshotId != plan.SnapshotId ||
            checkpoint.Proof.SourceConnectionId != plan.SourceConnectionId ||
            checkpoint.Proof.ManifestSha256 != plan.ManifestSha256)
            throw Refused("GitCheckpointAuditPreflightCheckpointInvalid");

        // F-097b independently replays the F-091 predecessor chain and
        // checks every sealed F-014 full-ref/OID source manifest.
        GitHubSourceCheckpointGitRefProof? full;
        try
        {
            full = await fullRefLedger.GetVerifiedAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
        }
        catch (GitHubSourceCheckpointGitRefLedgerException)
        { throw Refused("GitCheckpointAuditPreflightSourceCorrupt"); }
        if (full is null)
            throw Refused("GitCheckpointAuditPreflightSourceProofMissing");

        (GitHubTargetRefEvidenceView View, GitHubRefObservation Observation)? target;
        try
        {
            target = await new PostgresGitHubTargetRefEvidenceStore(db, clock)
                .ReadVerifiedSnapshotAsync(
                    preparationId, planId, targetObservationId, ct);
        }
        catch (GitHubTargetRefEvidenceException)
        { throw Refused("GitCheckpointAuditPreflightTargetCorrupt"); }
        if (target is null)
            throw Refused("GitCheckpointAuditPreflightTargetNotFound");
        if (target.Value.View.EvidenceSha256 != expectedTargetEvidenceSha256 ||
            !target.Value.View.IsCurrent)
            throw Refused("GitCheckpointAuditPreflightTargetStale");

        // No superseded F-090 epoch may be used to prepare a new audit.
        // This method only reads: future F-095 writes MUST repeat these
        // checks under the source-scoped advisory lock after physical fsck.
        var latest = await (from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join sync in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals sync.SnapshotId
            where cp.PlanId == planId
            orderby sync.SourceCollectedAtUtc descending, cp.Id descending
            select cp.Id).FirstOrDefaultAsync(ct);
        if (latest != checkpointId)
            throw Refused("GitCheckpointAuditPreflightSourceStale");

        // Recheck plan selection after independently loading the source
        // and target receipts. No writer can consume this as an authority.
        var finalPlan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("GitCheckpointAuditPreflightPlanNotFound");
        if (finalPlan.ManifestSha256 != plan.ManifestSha256 ||
            finalPlan.SnapshotId != plan.SnapshotId ||
            !finalPlan.IsCurrent || finalPlan.ReadyForExecution)
            throw Refused("GitCheckpointAuditPreflightPlanNotCurrent");
        try
        {
            return GitIncrementalCheckpointAuditPreflightIntegrity.Seal(
                finalPlan, checkpoint, full, target.Value.View,
                target.Value.Observation);
        }
        catch (GitIncrementalCheckpointPreflightException)
        { throw Refused("GitCheckpointAuditPreflightEvidenceInvalid"); }
    }
}
