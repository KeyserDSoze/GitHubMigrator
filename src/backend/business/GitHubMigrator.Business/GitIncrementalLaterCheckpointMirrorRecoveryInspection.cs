using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #121: OFFLINE, non-authorizing diagnostic of a quarantined F-061v2
// worker. A SHA-256 seal is NEVER a lease, reviewed recovery decision,
// permission to adopt UUID.git or proof of migration completion.
public sealed record GitIncrementalLaterCheckpointMirrorRecoveryInspection(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid CheckpointId, Guid SourceSnapshotId,
    Guid TargetObservationId, Guid WorkpackId, Guid PhysicalMirrorId,
    Guid WorkerId, Guid LeaseToken, long LeaseGeneration,
    string SourceRepositoryId, string StagingEvidenceSha256,
    string BundleEvidenceSha256, string BundleSha256,
    string FullRefProofSha256, string SourceRefSetSha256,
    string FailureCode, DateTimeOffset QuarantinedAtUtc,
    int LeaseEventCount, int RenewedEventCount,
    string LeaseEventHistorySha256,
    string PhysicalState, string? ObjectSetSha256, int? ObjectCount,
    DateTimeOffset InspectedAtUtc, string EvidenceSha256,
    bool DurableRecoveryApprovalPersisted,
    bool CanReAdmit, bool CanMaterialize, bool CanPush,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
{
    private const string Domain =
        "f097c4-f061v2-recovery-readonly-inspection-v1";
    public const string Intact = "Intact";
    public const string Uninspectable = "Uninspectable";
    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorRecoveryInspectionInvalid");

    public static GitIncrementalLaterCheckpointMirrorRecoveryInspection Seal(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        Guid workerId, Guid leaseToken, long generation,
        string failureCode, DateTimeOffset quarantinedAtUtc,
        int totalEvents, int renewedEvents, string eventHistorySha256,
        GitIncrementalLaterCheckpointMirrorOfflineAudit? physical,
        DateTimeOffset inspectedAtUtc)
    {
        try
        {
            GitIncrementalLaterCheckpointMirrorStagingIntegrity
                .VerifyOfflineCandidate(stage, durable);
            if (physical is not null)
                GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
                    .Verify(physical, stage, durable);
        }
        catch (GitSourceSnapshotMirrorException) { throw Refused(); }
        if (workerId == Guid.Empty || leaseToken == Guid.Empty ||
            generation is < 1 or > 2000 ||
            string.IsNullOrWhiteSpace(failureCode) ||
            failureCode.Length > 70 ||
            totalEvents is < 2 or > 1000 ||
            renewedEvents < 0 ||
            renewedEvents != totalEvents - 2 ||
            !GitSourceBundleStorageCheck.ValidSha(eventHistorySha256) ||
            quarantinedAtUtc == default ||
            quarantinedAtUtc.Offset != TimeSpan.Zero ||
            inspectedAtUtc == default ||
            inspectedAtUtc.Offset != TimeSpan.Zero ||
            inspectedAtUtc < quarantinedAtUtc ||
            (physical is not null &&
                physical.VerifiedAtUtc > inspectedAtUtc))
            throw Refused();

        var at = Milliseconds(inspectedAtUtc);
        var quarantine = Milliseconds(quarantinedAtUtc);
        var row = new GitIncrementalLaterCheckpointMirrorRecoveryInspection(
            1, stage.PreparationId, stage.PlanId, stage.CheckpointId,
            stage.SourceSnapshotId, stage.TargetObservationId,
            stage.WorkpackId, stage.PhysicalMirrorId, workerId,
            leaseToken, generation, stage.SourceRepositoryId,
            stage.EvidenceSha256, durable.EvidenceSha256,
            durable.BundleSha256, stage.SourceFullRefProofSha256,
            stage.SourceRefSetSha256, failureCode, quarantine,
            totalEvents, renewedEvents, eventHistorySha256,
            physical is null ? Uninspectable : Intact,
            physical?.ObjectSetSha256, physical?.ObjectCount, at,
            "", false, false, false, false, false, false, false);
        return row with { EvidenceSha256 = Digest(row) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorRecoveryInspection row,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorOfflineAudit? physical)
    {
        if (row is null || row.EvidenceVersion != 1 ||
            row.DurableRecoveryApprovalPersisted || row.CanReAdmit ||
            row.CanMaterialize || row.CanPush || row.CanRetry ||
            row.CanDispatch || row.MigrationComplete ||
            row != Seal(stage, durable, row.WorkerId, row.LeaseToken,
                row.LeaseGeneration, row.FailureCode,
                row.QuarantinedAtUtc, row.LeaseEventCount,
                row.RenewedEventCount, row.LeaseEventHistorySha256,
                physical, row.InspectedAtUtc))
            throw Refused();
    }

    private static DateTimeOffset Milliseconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(
            value.ToUnixTimeMilliseconds());

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorRecoveryInspection row)
    {
        var p = new object?[] {
            Domain, row.EvidenceVersion,
            row.PreparationId.ToString("D"), row.PlanId.ToString("D"),
            row.CheckpointId.ToString("D"),
            row.SourceSnapshotId.ToString("D"),
            row.TargetObservationId.ToString("D"),
            row.WorkpackId.ToString("D"),
            row.PhysicalMirrorId.ToString("D"),
            row.WorkerId.ToString("D"), row.LeaseToken.ToString("D"),
            row.LeaseGeneration, row.SourceRepositoryId,
            row.StagingEvidenceSha256, row.BundleEvidenceSha256,
            row.BundleSha256, row.FullRefProofSha256,
            row.SourceRefSetSha256, row.FailureCode,
            row.QuarantinedAtUtc.ToUniversalTime().ToString("O"),
            row.LeaseEventCount, row.RenewedEventCount,
            row.LeaseEventHistorySha256,
            row.PhysicalState, row.ObjectSetSha256, row.ObjectCount,
            row.InspectedAtUtc.ToUniversalTime().ToString("O"),
            false, false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p))));
    }
}
