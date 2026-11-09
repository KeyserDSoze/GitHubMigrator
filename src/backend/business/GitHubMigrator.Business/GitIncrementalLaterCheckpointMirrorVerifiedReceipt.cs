using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #119: physically re-audited F-061v2 source mirror evidence.
// Separate from self-sealed F-060v2 offline/auditable SHA witnesses:
// only a source-locked PostgreSQL lease writer can make this durable.
public interface IGitIncrementalLaterCheckpointMirrorPhysicalAuditor
{
    Task<GitIncrementalLaterCheckpointMirrorOfflineAudit> AuditAsync(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        CancellationToken ct);
}

public sealed record GitIncrementalLaterCheckpointMirrorVerifiedReceipt(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid SourceSnapshotId, Guid CheckpointId, Guid TargetObservationId,
    Guid WorkpackId, Guid PhysicalMirrorId,
    Guid WorkerId, Guid LeaseToken, long LeaseGeneration,
    string StagingEvidenceSha256, string FullRefProofSha256,
    string BundleEvidenceSha256, string BundleSha256,
    string SourceRefSetSha256, string GitObjectFormat,
    string ObjectSetSha256, int ObjectCount,
    DateTimeOffset PhysicalVerifiedAtUtc,
    DateTimeOffset RecordedAtUtc, string EvidenceSha256,
    bool DurableEvidencePersisted, bool CanMaterialize,
    bool CanPush, bool CanRetry, bool CanDispatch,
    bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorVerifiedIntegrity
{
    private const string Domain = "f097c4-f061v2-physical-verified-v1";
    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorVerifiedInvalid");

    public static GitIncrementalLaterCheckpointMirrorVerifiedReceipt Seal(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorOfflineAudit physical,
        Guid workerId, Guid leaseToken, long generation,
        DateTimeOffset recordedAtUtc)
    {
        try
        {
            GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity.Verify(
                physical, stage, durable);
        }
        catch (GitSourceSnapshotMirrorException) { throw Refused(); }
        if (workerId == Guid.Empty || leaseToken == Guid.Empty ||
            generation is < 1 or > 2000 ||
            recordedAtUtc == default ||
            recordedAtUtc.Offset != TimeSpan.Zero ||
            recordedAtUtc < physical.VerifiedAtUtc ||
            !GitSourceBundleStorageCheck.ValidSha(physical.ObjectSetSha256))
            throw Refused();

        // PostgreSQL timestamps are stored with microsecond precision;
        // normalize to millisecond before sealing for stable DB replay.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(
            recordedAtUtc.ToUnixTimeMilliseconds());
        var measuredAt = DateTimeOffset.FromUnixTimeMilliseconds(
            physical.VerifiedAtUtc.ToUnixTimeMilliseconds());
        if (measuredAt > now)
            throw Refused();
        var record = new GitIncrementalLaterCheckpointMirrorVerifiedReceipt(
            1, stage.PreparationId, stage.PlanId, stage.SourceSnapshotId,
            stage.CheckpointId, stage.TargetObservationId, stage.WorkpackId,
            stage.PhysicalMirrorId, workerId, leaseToken, generation,
            stage.EvidenceSha256, stage.SourceFullRefProofSha256,
            durable.EvidenceSha256, durable.BundleSha256,
            stage.SourceRefSetSha256, stage.GitObjectFormat,
            physical.ObjectSetSha256, physical.ObjectCount,
            measuredAt, now, "", true,
            false, false, false, false, false);
        return record with { EvidenceSha256 = Digest(record) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorVerifiedReceipt receipt,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorOfflineAudit physical)
    {
        if (receipt is null || receipt.EvidenceVersion != 1 ||
            !receipt.DurableEvidencePersisted || receipt.CanMaterialize ||
            receipt.CanPush || receipt.CanRetry || receipt.CanDispatch ||
            receipt.MigrationComplete ||
            receipt != Seal(stage, durable, physical, receipt.WorkerId,
                receipt.LeaseToken, receipt.LeaseGeneration,
                receipt.RecordedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorVerifiedReceipt r)
    {
        var parts = new object[] {
            Domain, r.EvidenceVersion,
            r.PreparationId.ToString("D"), r.PlanId.ToString("D"),
            r.SourceSnapshotId.ToString("D"),
            r.CheckpointId.ToString("D"),
            r.TargetObservationId.ToString("D"),
            r.WorkpackId.ToString("D"),
            r.PhysicalMirrorId.ToString("D"),
            r.WorkerId.ToString("D"), r.LeaseToken.ToString("D"),
            r.LeaseGeneration, r.StagingEvidenceSha256,
            r.FullRefProofSha256, r.BundleEvidenceSha256,
            r.BundleSha256, r.SourceRefSetSha256, r.GitObjectFormat,
            r.ObjectSetSha256, r.ObjectCount,
            r.PhysicalVerifiedAtUtc.ToUniversalTime().ToString("O"),
            r.RecordedAtUtc.ToUniversalTime().ToString("O"),
            true, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }
}
