using System.Data;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// F-095 internal only, default disabled. The public HTTP layer has no
// endpoint for issuing this audit, accepting OIDs or allowing GitHub writes.
// Physical F-094 checks are performed BEFORE publication, then source/target
// proofs are re-read under one PostgreSQL source lock and atomically recorded.
public sealed class PostgresGitIncrementalAncestryLedger(
    AppDbContext db, TimeProvider clock,
    GitIncrementalAncestryLedgerOptions options,
    IIncrementalOperationPlanRepository plans,
    IGitHubSourceSyncCheckpointJournal checkpoints,
    IVerifiedSourceGitRefReader sourceRefs,
    ILocalGitIncrementalAncestryAuditor physical)
    : IGitIncrementalAncestryLedger
{
    private static GitIncrementalAncestryLedgerException Refused(string code) =>
        new(code);

    private void RequireEnabled()
    {
        if (!options.Enabled)
            throw Refused("GitAncestryLedgerDisabled");
    }

    private static bool Sha(string? value) =>
        value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<GitIncrementalAncestryLedgerEntry> AuditAndRecordAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetEvidenceSha256,
        Guid targetSandboxId, Guid actorId, CancellationToken ct)
    {
        RequireEnabled();
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            targetSandboxId == Guid.Empty || actorId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            !Sha(expectedTargetEvidenceSha256))
            throw Refused("GitAncestryLedgerRequestInvalid");

        // No client-supplied source OIDs, destination OIDs, filesystem paths,
        // URLs, tokens, refspecs, audit result or evidence fingerprints.
        // Every byte of expected refs comes from verified DB snapshots.
        var first = await ReadTrustedAsync(preparationId, planId,
            checkpointId, targetObservationId,
            sourceRepositoryId, expectedTargetEvidenceSha256,
            requireCurrent: true, ct);
        var request = new GitIncrementalAncestryRequest(
            first.Checkpoint.Proof.SnapshotId, targetSandboxId,
            checkpointId, first.Checkpoint.RefChangeAttestationSha256!,
            targetObservationId, first.Target.View.EvidenceSha256,
            first.Source.Refs,
            first.Target.Observation.AsMirrorRefs());

        // The runner is offline, deployment-owned and independently gated.
        // A broken/missing mirror aborts without inserting any ledger row.
        var audited = await physical.AuditAsync(request, ct);
        try
        {
            GitIncrementalAncestryLedgerIntegrity.VerifyBinding(
                first.Plan, first.Checkpoint, first.Source,
                first.Target.View, first.Target.Observation,
                audited, true);
        }
        catch (GitIncrementalAncestryLedgerException) { throw; }
        catch (Exception e) when (e is GitIncrementalAncestryException
            or IncrementalGitRefReconciliationException)
        { throw Refused("GitAncestryLedgerEvidenceInvalid"); }

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        // Same source-scoped fencing key used by F-029/F-031/F-090.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({first.Plan.SourceConnectionId.ToString()}), 732911)",
            ct);
        var trusted = await ReadTrustedAsync(preparationId, planId,
            checkpointId, targetObservationId, sourceRepositoryId,
            expectedTargetEvidenceSha256, requireCurrent: true, ct);

        GitIncrementalAncestryLedgerIntegrity.VerifyBinding(
            trusted.Plan, trusted.Checkpoint, trusted.Source,
            trusted.Target.View, trusted.Target.Observation,
            audited, true);

        var existing = await db.GitIncrementalAncestryLedgers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanId == planId &&
                x.CheckpointId == checkpointId &&
                x.TargetObservationId == targetObservationId &&
                x.SourceRepositoryId == sourceRepositoryId, ct);
        if (existing is not null)
        {
            if (existing.ReceiptSha256 != audited.EvidenceSha256)
                throw Refused("GitAncestryLedgerReplayConflict");
            var replay = await VerifiedAsync(existing, trusted, ct);
            await tx.CommitAsync(ct);
            return replay;
        }

        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            clock.GetUtcNow().ToUnixTimeMilliseconds());
        var record = new GitIncrementalAncestryLedgerRecord
        {
            Id = Guid.NewGuid(), PreparationId = preparationId,
            PlanId = planId, CheckpointId = checkpointId,
            TargetObservationId = targetObservationId,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryName = trusted.Target.View.TargetRepositoryName,
            OperationManifestSha256 = trusted.Plan.ManifestSha256,
            SourceEvidenceSha256 = trusted.Source.SourceEvidenceSha256,
            SourceCheckpointAttestationSha256 =
                trusted.Checkpoint.RefChangeAttestationSha256!,
            TargetEvidenceSha256 = trusted.Target.View.EvidenceSha256,
            ReceiptSha256 = audited.EvidenceSha256,
            ReceiptJson = JsonSerializer.Serialize(audited),
            RecordedBy = actorId, RecordedAtUtc = at,
            CanPush = false, CanRetry = false, CanDispatch = false,
            MigrationComplete = false
        };
        db.GitIncrementalAncestryLedgers.Add(record);
        db.GitIncrementalAncestryLedgerEvents.Add(
            new GitIncrementalAncestryLedgerEventRecord
            {
                LedgerId = record.Id, Type = "Recorded", ActorId = actorId,
                ReceiptSha256 = audited.EvidenceSha256,
                OccurredAtUtc = at
            });
        await db.SaveChangesAsync(ct);
        var saved = await VerifiedAsync(record, trusted, ct);
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<GitIncrementalAncestryLedgerEntry?> GetAsync(
        Guid preparationId, Guid planId, Guid ledgerId,
        CancellationToken ct)
    {
        RequireEnabled();
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            ledgerId == Guid.Empty)
            throw Refused("GitAncestryLedgerRequestInvalid");

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        var row = await db.GitIncrementalAncestryLedgers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == ledgerId &&
                x.PlanId == planId && x.PreparationId == preparationId, ct);
        if (row is null) return null;
        // Historic evidence stays readable when F-031 freshness expires or
        // later F-090 source checkpoints are captured. Never label as live.
        var trusted = await ReadTrustedAsync(preparationId, planId,
            row.CheckpointId, row.TargetObservationId,
            row.SourceRepositoryId, row.TargetEvidenceSha256,
            requireCurrent: false, ct);
        var entry = await VerifiedAsync(row, trusted, ct);
        await tx.CommitAsync(ct);
        return entry;
    }

    private async Task<(
        IncrementalOperationPlan Plan,
        GitHubSourceSyncCheckpointReceipt Checkpoint,
        VerifiedSourceGitRefs Source,
        (GitHubTargetRefEvidenceView View, GitHubRefObservation Observation) Target
        )> ReadTrustedAsync(
        Guid preparationId, Guid planId, Guid checkpointId,
        Guid targetObservationId, string sourceRepositoryId,
        string expectedTargetEvidenceSha256,
        bool requireCurrent, CancellationToken ct)
    {
        var plan = await plans.GetAsync(preparationId, planId, ct)
            ?? throw Refused("GitAncestryLedgerPlanNotFound");
        if ((requireCurrent && !plan.IsCurrent) ||
            plan.ReadyForExecution)
            throw Refused("GitAncestryLedgerPlanStale");
        var checkpoint = await checkpoints.GetVerifiedAsync(
            preparationId, planId, checkpointId, ct)
            ?? throw Refused("GitAncestryLedgerCheckpointNotFound");
        if (checkpoint.EvidenceVersion != 2 ||
            checkpoint.Proof.SnapshotId != plan.SnapshotId ||
            !checkpoint.Proof.IsBaselineSnapshot ||
            checkpoint.RefChangeAttestationSha256 is null)
            throw Refused("GitAncestryLedgerSourceEpochUnsupported");

        if (requireCurrent)
        {
            // Never publish a proof for a superseded F-090 epoch.
            var latest = await (from cp in db.GitHubSourceSyncCheckpoints.AsNoTracking()
                join sync in db.StagingSnapshotSyncs.AsNoTracking()
                    on cp.SnapshotId equals sync.SnapshotId
                where cp.PlanId == planId
                orderby sync.SourceCollectedAtUtc descending, cp.Id descending
                select cp.Id).FirstOrDefaultAsync(ct);
            if (latest != checkpointId)
                throw Refused("GitAncestryLedgerSourceEpochStale");
        }

        VerifiedSourceGitRefs source;
        try { source = await sourceRefs.ReadAsync(plan, sourceRepositoryId, ct); }
        catch (Exception e) when (e is IncrementalGitRefReconciliationException
            or DiscoveryRefManifestException
            or StagingSyncInputException)
        { throw Refused("GitAncestryLedgerSourceEvidenceUnavailable"); }

        (GitHubTargetRefEvidenceView View, GitHubRefObservation Observation)? target;
        try
        {
            target = await new PostgresGitHubTargetRefEvidenceStore(db, clock)
                .ReadVerifiedSnapshotAsync(preparationId, planId,
                    targetObservationId, ct);
        }
        catch (GitHubTargetRefEvidenceException)
        { throw Refused("GitAncestryLedgerTargetEvidenceCorrupt"); }
        if (target is null)
            throw Refused("GitAncestryLedgerTargetObservationNotFound");
        if (target.Value.View.EvidenceSha256 != expectedTargetEvidenceSha256 ||
            (requireCurrent && !target.Value.View.IsCurrent))
            throw Refused("GitAncestryLedgerTargetEvidenceStale");

        return (plan, checkpoint, source, target.Value);
    }

    private async Task<GitIncrementalAncestryLedgerEntry> VerifiedAsync(
        GitIncrementalAncestryLedgerRecord row,
        (IncrementalOperationPlan Plan,
         GitHubSourceSyncCheckpointReceipt Checkpoint,
         VerifiedSourceGitRefs Source,
         (GitHubTargetRefEvidenceView View, GitHubRefObservation Observation) Target
        ) trusted,
        CancellationToken ct)
    {
        GitIncrementalAncestryReceipt audited;
        try
        {
            audited = JsonSerializer.Deserialize<GitIncrementalAncestryReceipt>(
                row.ReceiptJson) ?? throw new JsonException();
        }
        catch (JsonException)
        { throw Refused("GitAncestryLedgerHistoryCorrupt"); }

        var entry = new GitIncrementalAncestryLedgerEntry(
            row.Id, row.PreparationId, row.PlanId,
            row.SourceRepositoryId, row.TargetRepositoryName,
            row.OperationManifestSha256, row.SourceEvidenceSha256, audited,
            row.RecordedBy, row.RecordedAtUtc,
            row.CanPush, row.CanRetry, row.CanDispatch, row.MigrationComplete);
        try
        {
            GitIncrementalAncestryLedgerIntegrity.VerifyEntry(
                entry, trusted.Plan, trusted.Checkpoint, trusted.Source,
                trusted.Target.View, trusted.Target.Observation);
        }
        catch (GitIncrementalAncestryLedgerException)
        { throw Refused("GitAncestryLedgerHistoryCorrupt"); }

        if (row.CheckpointId != audited.Request.SourceCheckpointId ||
            row.TargetObservationId != audited.Request.TargetObservationId ||
            row.ReceiptSha256 != audited.EvidenceSha256 ||
            row.SourceCheckpointAttestationSha256 !=
                audited.Request.SourceCheckpointAttestationSha256 ||
            row.TargetEvidenceSha256 !=
                audited.Request.TargetObservationSha256)
            throw Refused("GitAncestryLedgerHistoryCorrupt");

        var events = await db.GitIncrementalAncestryLedgerEvents.AsNoTracking()
            .Where(x => x.LedgerId == row.Id).Take(2).ToArrayAsync(ct);
        if (events.Length != 1 || events[0].Type != "Recorded" ||
            events[0].ActorId != row.RecordedBy ||
            events[0].ReceiptSha256 != row.ReceiptSha256 ||
            events[0].OccurredAtUtc != row.RecordedAtUtc)
            throw Refused("GitAncestryLedgerHistoryCorrupt");
        return entry;
    }
}
