using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-097c.4c.1: internal, OFF-by-default, source-locked later checkpoint
// F-032/F-038 write-blocked workpack. The separately trusted F-097b
// complete Git ref/OID receipt is essential: F-091 fingerprints are NOT
// Git object IDs. No bundle acquisition, F-095 audit or remote GitHub.
public sealed record GitIncrementalCheckpointWorkpackOptions(bool Enabled);

public sealed class PostgresGitIncrementalCheckpointWorkpackJournal(
    AppDbContext db, TimeProvider clock,
    GitIncrementalCheckpointWorkpackOptions options,
    PostgresGitIncrementalCheckpointAuditPreflight preflight,
    PostgresGitHubSourceCheckpointGitRefLedger sourceLedger,
    IIncrementalOperationPlanRepository plans)
{
    private static GitIncrementalCheckpointWorkpackException Refused(
        string code) => new(code);

    public async Task<GitIncrementalCheckpointWorkpackProof> CaptureAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetEvidenceSha256, Guid actorId,
        CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("CheckpointWorkpackDisabled");
        if (actorId == Guid.Empty)
            throw Refused("CheckpointWorkpackActorInvalid");

        // F-091 and F-097b readers use their own source proof transactions.
        // Read independently first, then after the source advisory lock
        // recheck the latest snapshot/target/plan/proof rows atomically.
        GitIncrementalCheckpointAuditInputs selected;
        GitHubSourceCheckpointGitRefProof? source;
        try
        {
            selected = await preflight.ReadAsync(
                preparationId, planId, checkpointId, targetObservationId,
                sourceRepositoryId, expectedTargetEvidenceSha256, ct);
            source = await sourceLedger.GetVerifiedAsync(
                preparationId, planId, checkpointId, sourceRepositoryId, ct);
        }
        catch (GitIncrementalCheckpointPreflightException)
        { throw Refused("CheckpointWorkpackSourceTargetUnverified"); }
        catch (GitHubSourceCheckpointGitRefLedgerException)
        { throw Refused("CheckpointWorkpackSourceUnverified"); }
        if (source is null ||
            source.EvidenceSha256 != selected.SourceFullRefProofSha256)
            throw Refused("CheckpointWorkpackSourceUnverified");

        var before = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("CheckpointWorkpackPlanNotFound");
        if (!before.IsCurrent || before.ReadyForExecution ||
            before.SnapshotId == selected.SourceSnapshotId)
            throw Refused("CheckpointWorkpackLaterEpochRequired");

        GitMirrorRefReconciliation comparison;
        try
        {
            comparison = GitMirrorRefReconciler.Compare(new(
                before.Id, before.ManifestSha256, source.SourceEvidenceSha256,
                selected.TargetEvidenceSha256, selected.ExpectedSourceRefs,
                selected.ExpectedTargetRefs));
        }
        catch (GitMirrorRefReconciliationException)
        { throw Refused("CheckpointWorkpackReconciliationInvalid"); }
        if (comparison.SourceRefCount is < 1 or >
                GitIncrementalAncestryIntegrity.MaxRefsPerSide ||
            comparison.TargetRefCount is < 1 or >
                GitIncrementalAncestryIntegrity.MaxRefsPerSide)
            throw Refused("CheckpointWorkpackRefLimit");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // F-013/F-029/F-031/F-090/F-097 use this source-first lock;
        // an out-of-date preflight must never authorize a persisted row.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({before.SourceConnectionId.ToString()}), 732911)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({planId.ToString()}), 732990)", ct);
        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("CheckpointWorkpackPlanNotFound");
        if (!plan.IsCurrent || plan.ReadyForExecution ||
            plan.SourceConnectionId != before.SourceConnectionId ||
            plan.ManifestSha256 != before.ManifestSha256 ||
            plan.SnapshotId != before.SnapshotId)
            throw Refused("CheckpointWorkpackPlanChanged");

        var latest = await (
            from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
            join sync in db.StagingSnapshotSyncs.AsNoTracking()
                on cp.SnapshotId equals sync.SnapshotId
            where cp.PlanId == planId
            orderby sync.SourceCollectedAtUtc descending, cp.Id descending
            select cp).FirstOrDefaultAsync(ct);
        if (latest is null || latest.Id != checkpointId ||
            latest.SnapshotId != selected.SourceSnapshotId ||
            latest.PreparationId != preparationId ||
            latest.SourceConnectionId != plan.SourceConnectionId ||
            latest.RefChangeAttestationSha256 !=
                selected.SourceCheckpointAttestationSha256 ||
            latest.CanPush || latest.CanRetry ||
            latest.CanDispatch || latest.MigrationComplete)
            throw Refused("CheckpointWorkpackSourceStale");

        var proofRow = await db.GitHubSourceCheckpointGitRefLedgers
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (proofRow is null ||
            proofRow.PreparationId != preparationId ||
            proofRow.PlanId != planId ||
            proofRow.SnapshotId != source.SnapshotId ||
            proofRow.SourceConnectionId != plan.SourceConnectionId ||
            proofRow.EvidenceSha256 != source.EvidenceSha256 ||
            proofRow.SourceManifestRootSha256 !=
                source.SourceManifestRootSha256 ||
            proofRow.RecordedBy != actorId ||
            proofRow.CanPush || proofRow.CanRetry ||
            proofRow.CanDispatch || proofRow.MigrationComplete)
            throw Refused("CheckpointWorkpackSourceChanged");

        var target = await db.GitHubTargetRefEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == targetObservationId &&
                x.PlanId == planId && x.SourceRepositoryId == sourceRepositoryId, ct);
        var latestTarget = await db.GitHubTargetRefEvidence.AsNoTracking()
            .Where(x => x.PlanId == planId &&
                x.SourceRepositoryId == sourceRepositoryId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => x.Id).FirstOrDefaultAsync(ct);
        if (target is null || latestTarget != targetObservationId ||
            target.EvidenceSha256 != selected.TargetEvidenceSha256 ||
            target.TargetRepositoryName != selected.TargetRepositoryName ||
            target.ManifestSha256 != plan.ManifestSha256 ||
            target.CreatedBy != actorId || target.ReadyForExecution)
            throw Refused("CheckpointWorkpackTargetStale");

        var row = await db.GitIncrementalCheckpointWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CheckpointId == checkpointId &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.TargetObservationId == targetObservationId, ct);
        if (row is not null)
        {
            if (row.RecordedBy != actorId ||
                row.FullRefProofSha256 != source.EvidenceSha256 ||
                row.PlanId != plan.Id ||
                row.SourceSnapshotId != source.SnapshotId ||
                row.CanPush || row.CanRetry ||
                row.CanDispatch || row.MigrationComplete)
                throw Refused("CheckpointWorkpackReplayConflict");
            GitIncrementalCheckpointWorkpackProof receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<
                    GitIncrementalCheckpointWorkpackProof>(row.ReceiptJson)
                    ?? throw Refused("CheckpointWorkpackReceiptCorrupt");
            }
            catch (JsonException)
            { throw Refused("CheckpointWorkpackReceiptCorrupt"); }
            GitIncrementalCheckpointWorkpackIntegrity.Verify(
                receipt, plan, selected, source, comparison);
            if (row.WorkpackId != receipt.WorkpackId ||
                row.ReconciliationId != receipt.ReconciliationId ||
                row.EvidenceSha256 != receipt.EvidenceSha256)
                throw Refused("CheckpointWorkpackReceiptCorrupt");
            await VerifyChildrenAsync(receipt, comparison, ct);
            await tx.CommitAsync(ct);
            return receipt;
        }

        // Do not claim an existing baseline reconciliation as a later
        // checkpoint, even if its source Git IDs coincidentally match.
        if (await db.IncrementalGitRefReconciliations.AsNoTracking()
            .AnyAsync(x => x.PlanId == plan.Id &&
                x.SourceRepositoryId == sourceRepositoryId &&
                x.TargetObservationId == targetObservationId &&
                x.SourceEvidenceSha256 == source.SourceEvidenceSha256, ct))
            throw Refused("CheckpointWorkpackReconciliationConflict");

        var recId = Guid.NewGuid();
        var workpackId = Guid.NewGuid();
        var sealedProof = GitIncrementalCheckpointWorkpackIntegrity.Seal(
            plan, selected, source, comparison, recId, workpackId, actorId);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());
        db.IncrementalGitRefReconciliations.Add(new()
        {
            Id = recId, PlanId = plan.Id,
            SnapshotId = selected.SourceSnapshotId,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryName = selected.TargetRepositoryName,
            TargetObservationId = targetObservationId,
            ManifestSha256 = plan.ManifestSha256,
            SourceEvidenceSha256 = source.SourceEvidenceSha256,
            SourceManifestRootSha256 = source.SourceManifestRootSha256,
            TargetEvidenceSha256 = selected.TargetEvidenceSha256,
            ReconciliationSha256 = comparison.ReconciliationSha256,
            CreatedAtUtc = at, CreatedBy = actorId,
            SourceRefCount = comparison.SourceRefCount,
            TargetRefCount = comparison.TargetRefCount,
            MatchingCount = comparison.MatchingCount,
            CreateCandidateCount = comparison.CreateCandidateCount,
            DivergedCount = comparison.DivergedCount,
            TargetOnlyCount = comparison.TargetOnlyCount,
            ItemsJson = JsonSerializer.Serialize(comparison.Items),
            ReadyForExecution = false
        });
        db.GitTransferWorkpacks.Add(new()
        {
            Id = workpackId, ReconciliationId = recId,
            PlanId = plan.Id,
            SourceConnectionId = plan.SourceConnectionId,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryName = selected.TargetRepositoryName,
            ManifestSha256 = plan.ManifestSha256,
            ReconciliationSha256 = comparison.ReconciliationSha256,
            TargetEvidenceSha256 = selected.TargetEvidenceSha256,
            CreatedBy = actorId,
            State = GitTransferWorkpackStates.Blocked,
            Phase = GitTransferWorkpackPhases.WriteBlocked,
            Attempts = 0, MaxAttempts = 5,
            AvailableAtUtc = at, CreatedAtUtc = at,
            UpdatedAtUtc = at, BlockedAtUtc = at,
            LastErrorCode = "DestinationWritesNotAuthorized",
            ReadyForExecution = false
        });
        db.GitTransferWorkpackEvents.Add(new()
        {
            WorkpackId = workpackId, Type = "WriteBlocked",
            Phase = GitTransferWorkpackPhases.WriteBlocked,
            Attempt = 0, OccurredAtUtc = at,
            ErrorCode = "DestinationWritesNotAuthorized"
        });
        db.GitIncrementalCheckpointWorkpacks.Add(new()
        {
            WorkpackId = workpackId, ReconciliationId = recId,
            PreparationId = preparationId, PlanId = planId,
            CheckpointId = checkpointId, SourceSnapshotId = source.SnapshotId,
            SourceRepositoryId = sourceRepositoryId,
            TargetObservationId = targetObservationId,
            FullRefProofSha256 = source.EvidenceSha256,
            ReceiptJson = JsonSerializer.Serialize(sealedProof),
            EvidenceSha256 = sealedProof.EvidenceSha256, RecordedBy = actorId,
            RecordedAtUtc = at, CanPush = false, CanRetry = false,
            CanDispatch = false, MigrationComplete = false
        });
        db.GitIncrementalCheckpointWorkpackEvents.Add(new()
        {
            WorkpackId = workpackId, Type = "Recorded",
            ActorId = actorId, EvidenceSha256 = sealedProof.EvidenceSha256,
            OccurredAtUtc = at
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sealedProof;
    }

    private async Task VerifyChildrenAsync(
        GitIncrementalCheckpointWorkpackProof proof,
        GitMirrorRefReconciliation comparison, CancellationToken ct)
    {
        var rec = await db.IncrementalGitRefReconciliations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == proof.ReconciliationId, ct)
            ?? throw Refused("CheckpointWorkpackChildMissing");
        var pack = await db.GitTransferWorkpacks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == proof.WorkpackId, ct)
            ?? throw Refused("CheckpointWorkpackChildMissing");
        var events = await db.GitIncrementalCheckpointWorkpackEvents
            .AsNoTracking().CountAsync(x => x.WorkpackId == proof.WorkpackId &&
                x.Type == "Recorded" &&
                x.EvidenceSha256 == proof.EvidenceSha256 &&
                x.ActorId == proof.RecordedBy, ct);
        GitMirrorRefComparison[] persistedItems;
        try
        {
            persistedItems = JsonSerializer.Deserialize<GitMirrorRefComparison[]>(
                rec.ItemsJson) ?? throw new JsonException();
        }
        catch (JsonException)
        { throw Refused("CheckpointWorkpackChildCorrupt"); }
        if (events != 1 ||
            rec.PlanId != proof.PlanId ||
            rec.SnapshotId != proof.SourceSnapshotId ||
            rec.SourceRepositoryId != proof.SourceRepositoryId ||
            rec.TargetRepositoryName != proof.TargetRepositoryName ||
            rec.TargetObservationId != proof.TargetObservationId ||
            rec.ManifestSha256 != proof.OperationManifestSha256 ||
            rec.SourceManifestRootSha256 != proof.SourceManifestRootSha256 ||
            rec.SourceEvidenceSha256 != proof.SourceEvidenceSha256 ||
            rec.TargetEvidenceSha256 != proof.TargetEvidenceSha256 ||
            rec.ReconciliationSha256 != proof.ReconciliationSha256 ||
            rec.CreatedBy != proof.RecordedBy || rec.ReadyForExecution ||
            rec.SourceRefCount != comparison.SourceRefCount ||
            rec.TargetRefCount != comparison.TargetRefCount ||
            rec.MatchingCount != comparison.MatchingCount ||
            rec.CreateCandidateCount != comparison.CreateCandidateCount ||
            rec.DivergedCount != comparison.DivergedCount ||
            rec.TargetOnlyCount != comparison.TargetOnlyCount ||
            !persistedItems.SequenceEqual(comparison.Items) ||
            pack.ReconciliationId != proof.ReconciliationId ||
            pack.PlanId != proof.PlanId ||
            pack.SourceRepositoryId != proof.SourceRepositoryId ||
            pack.TargetRepositoryName != proof.TargetRepositoryName ||
            pack.ReconciliationSha256 != proof.ReconciliationSha256 ||
            pack.TargetEvidenceSha256 != proof.TargetEvidenceSha256 ||
            pack.CreatedBy != proof.RecordedBy ||
            pack.State != GitTransferWorkpackStates.Blocked ||
            pack.Phase != GitTransferWorkpackPhases.WriteBlocked ||
            pack.Attempts != 0 || pack.ReadyForExecution ||
            pack.LastErrorCode != "DestinationWritesNotAuthorized")
            throw Refused("CheckpointWorkpackChildCorrupt");
    }
}
