using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

// #121: read-only, separately disabled-by-default assessment of a
// quarantined later source mirror. The inspection grants no recovered
// generation, lease, file import, release or migration authority.
// Physical Git auditing runs OUTSIDE the short SQL source-first locks.
public sealed record GitIncrementalLaterCheckpointMirrorRecoveryInspectionOptions(
    bool Enabled);

public sealed class PostgresGitIncrementalLaterCheckpointMirrorRecoveryInspector(
    AppDbContext db,
    GitIncrementalLaterCheckpointMirrorRecoveryInspectionOptions options,
    PostgresGitIncrementalLaterCheckpointMirrorStagingPreflight staging,
    PostgresGitIncrementalLaterCheckpointBundleHistory history,
    PostgresGitIncrementalLaterCheckpointMirrorAdmissionPreflight admission,
    PostgresGitIncrementalLaterCheckpointMirrorLeaseStore leaseStore,
    IGitIncrementalLaterCheckpointMirrorPhysicalAuditor physical)
{
    private static GitSourceSnapshotMirrorException Refused(string code) =>
        new(code);

    private sealed class EventRow
    {
        public long Id { get; set; }
        public long Generation { get; set; }
        public Guid WorkerId { get; set; }
        public string Type { get; set; } = "";
        public string StagingEvidenceSha256 { get; set; } = "";
        public DateTimeOffset OccurredAtUtc { get; set; }
    }

    private sealed record DiagnosticSnapshot(
        Guid WorkerId, Guid LeaseToken, long Generation,
        string FailureCode, DateTimeOffset QuarantinedAtUtc,
        int EventCount, int RenewedCount, string HistoryDigest,
        DateTimeOffset InspectedAtUtc);

    public async Task<GitIncrementalLaterCheckpointMirrorRecoveryInspection>
        ReadAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId,
            string sourceRepositoryId, string expectedTargetReceiptSha256,
            Guid originalBundleActorId, CancellationToken ct)
    {
        if (!options.Enabled)
            throw Refused("LaterCheckpointMirrorRecoveryInspectionDisabled");
        if (preparationId == Guid.Empty || planId == Guid.Empty ||
            checkpointId == Guid.Empty || targetObservationId == Guid.Empty ||
            workpackId == Guid.Empty || originalBundleActorId == Guid.Empty ||
            sourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(sourceRepositoryId) ||
            !GitSourceBundleStorageCheck.ValidSha(expectedTargetReceiptSha256))
            throw Refused("LaterCheckpointMirrorRecoveryInspectionScopeInvalid");

        // The *live* F-091/F-097b source and F-031 review, current
        // F-038v2 workpack, and actual on-disk UUID.bundle F-035v2
        // SHA/length are independently checked by the prior readers.
        var before = await staging.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        var durable = await history.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct)
            ?? throw Refused("LaterCheckpointMirrorRecoveryBundleMissing");
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(before, durable);
        var proof = await admission.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        RequireStage(proof, before, durable);
        var first = await ReadLockedAsync(proof, originalBundleActorId, ct);

        // The real local auditor opens trusted server-owned roots and
        // rechecks UUID.bundle SHA before/after TWO bare-Git fsck/full
        // reachable graph/ref/OID passes. No SQL transaction or GitHub
        // provider call is active here. Absence/corruption is a *failed*
        // physical read, never consent to adopt or overwrite UUID.git.
        GitIncrementalLaterCheckpointMirrorOfflineAudit? observed = null;
        try
        {
            observed = await physical.AuditAsync(before, durable, ct);
            GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity.Verify(
                observed, before, durable);
        }
        catch (GitSourceSnapshotMirrorException ex) when (
            ex.Code is "LaterCheckpointMirrorOfflineClosureInvalid" or
                       "LaterCheckpointMirrorOfflineBundleInvalid" or
                       "LaterCheckpointMirrorOfflineBundleChanged" or
                       "LaterCheckpointMirrorOfflineIoFailure")
        {
            // This is diagnostics only: mark the mirror uninspectable.
            // Neither a missing UUID.git nor a corrupt object can be
            // elevated to F-061 "Verified" or grant recovery authority.
        }

        // The source snapshot and target may have been superseded during
        // the filesystem pass. Independently re-read current proof, then
        // re-lock SQL and reject a changed generation/audit chain.
        var after = await staging.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        var afterDurable = await history.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct)
            ?? throw Refused("LaterCheckpointMirrorRecoveryBundleChanged");
        var afterProof = await admission.ReadAsync(preparationId, planId,
            checkpointId, targetObservationId, workpackId,
            sourceRepositoryId, expectedTargetReceiptSha256,
            originalBundleActorId, ct);
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(after, afterDurable);
        RequireStage(afterProof, after, afterDurable);
        if (before with { SourceRefs = after.SourceRefs } != after ||
            !before.SourceRefs.SequenceEqual(after.SourceRefs) ||
            durable != afterDurable)
            throw Refused("LaterCheckpointMirrorRecoveryProvenanceChanged");

        var second = await ReadLockedAsync(afterProof,
            originalBundleActorId, ct);
        if (first with { InspectedAtUtc = second.InspectedAtUtc } != second)
            throw Refused("LaterCheckpointMirrorRecoveryAuditChanged");

        var result =
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Seal(
                after, afterDurable, second.WorkerId,
                second.LeaseToken, second.Generation, second.FailureCode,
                second.QuarantinedAtUtc, second.EventCount,
                second.RenewedCount, observed, second.InspectedAtUtc);
        GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Verify(
            result, after, afterDurable, observed);
        return result;
    }

    private async Task<DiagnosticSnapshot> ReadLockedAsync(
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate proof,
        Guid bundleActorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await leaseStore.LockAsync(proof.SourceConnectionId, proof.PlanId, ct);
        await leaseStore.RequireLiveLockedAsync(proof, bundleActorId, ct);
        var now = await leaseStore.DatabaseNowAsync(ct);
        var row = await leaseStore.ReadLeaseAsync(
            proof.PhysicalMirrorId, ct)
            ?? throw Refused("LaterCheckpointMirrorRecoveryLeaseMissing");
        PostgresGitIncrementalLaterCheckpointMirrorLeaseStore
            .RequireSameScope(row, proof, bundleActorId);
        if (row.State != "RecoveryRequired" ||
            row.LeaseUntilUtc is not null ||
            row.FailureCode is not { Length: >= 1 and <= 70 } ||
            row.Generation is < 1 or > 2000 ||
            row.LeaseToken == Guid.Empty || row.WorkerId == Guid.Empty ||
            row.AcquiredAtUtc > now || row.UpdatedAtUtc > now ||
            row.HasVerifiedLocalObjects ||
            row.CanMaterialize || row.CanPush || row.CanRetry ||
            row.CanDispatch || row.MigrationComplete)
            throw Refused("LaterCheckpointMirrorRecoveryNotQuarantined");
        var verifiedCount = await db.Database.SqlQuery<long>($"""
            SELECT count(*)::bigint AS "Value"
            FROM git_later_checkpoint_mirror_verified_evidence
            WHERE mirror_id = {row.MirrorId}
                OR workpack_id = {row.WorkpackId}
            """).SingleAsync(ct);
        if (verifiedCount != 0)
            throw Refused("LaterCheckpointMirrorRecoveryAlreadyVerified");

        var events = await db.Database.SqlQuery<EventRow>($"""
            SELECT id AS "Id", generation AS "Generation",
                worker_id AS "WorkerId", type AS "Type",
                staging_evidence_sha256 AS "StagingEvidenceSha256",
                occurred_at_utc AS "OccurredAtUtc"
            FROM git_later_checkpoint_mirror_lease_events
            WHERE mirror_id = {row.MirrorId}
            ORDER BY id ASC LIMIT 1001
            """).ToArrayAsync(ct);
        if (events.Length is < 2 or > 1000 ||
            events[0].Type != "Acquired" ||
            events[^1].Type != "Quarantined" ||
            events[0].OccurredAtUtc < row.AcquiredAtUtc ||
            events[^1].OccurredAtUtc > row.UpdatedAtUtc ||
            events[^1].OccurredAtUtc > now ||
            events.Where(e => e.Type == "Quarantined").Count() != 1 ||
            events.Where(e => e.Type == "Acquired").Count() != 1 ||
            events.Any(e => e.Generation != row.Generation ||
                e.WorkerId != row.WorkerId ||
                e.StagingEvidenceSha256 != proof.StagingEvidenceSha256 ||
                e.OccurredAtUtc < row.AcquiredAtUtc ||
                e.OccurredAtUtc > now) ||
            events.Skip(1).Take(events.Length - 2)
                .Any(e => e.Type != "Renewed") ||
            events.Zip(events.Skip(1), (a,b) =>
                a.Id >= b.Id || a.OccurredAtUtc > b.OccurredAtUtc)
                .Any(invalid => invalid))
            throw Refused("LaterCheckpointMirrorRecoveryEventChainInvalid");
        var renewedCount = events.Length - 2;
        var eventParts = events.Select(e => new object[] {
            e.Id, e.Generation, e.WorkerId.ToString("D"), e.Type,
            e.StagingEvidenceSha256,
            e.OccurredAtUtc.ToUniversalTime().ToString("O")
        }).ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(eventParts))));
        var result = new DiagnosticSnapshot(
            row.WorkerId, row.LeaseToken, row.Generation,
            row.FailureCode, events[^1].OccurredAtUtc,
            events.Length, renewedCount, digest, now);
        await tx.CommitAsync(ct);
        return result;
    }

    private static void RequireStage(
        GitIncrementalLaterCheckpointMirrorAdmissionCandidate proof,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable)
    {
        if (proof.PhysicalMirrorId != stage.PhysicalMirrorId ||
            proof.PreparationId != stage.PreparationId ||
            proof.PlanId != stage.PlanId ||
            proof.SourceSnapshotId != stage.SourceSnapshotId ||
            proof.CheckpointId != stage.CheckpointId ||
            proof.TargetObservationId != stage.TargetObservationId ||
            proof.WorkpackId != stage.WorkpackId ||
            proof.StagingEvidenceSha256 != stage.EvidenceSha256 ||
            proof.SourceFullRefProofSha256 !=
                stage.SourceFullRefProofSha256 ||
            proof.SourceRefSetSha256 != stage.SourceRefSetSha256 ||
            proof.BundleSha256 != durable.BundleSha256 ||
            proof.BundleEvidenceSha256 != durable.EvidenceSha256 ||
            proof.DurableLeasePersisted || proof.F061LeaseGeneration != 0 ||
            proof.CanMaterialize || proof.CanPush || proof.CanRetry ||
            proof.CanDispatch || proof.MigrationComplete)
            throw Refused("LaterCheckpointMirrorRecoveryScopeChanged");
    }
}
