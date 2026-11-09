using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #114: an OFFLINE, READ-ONLY observation of an already existing
// UUID.git. The digest is NOT an F-061 Verified event, lease, authorization,
// persisted source proof, F-095 ancestry result or migration completion.
public sealed record GitIncrementalLaterCheckpointMirrorOfflineAudit(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid SourceSnapshotId, Guid CheckpointId, Guid TargetObservationId,
    Guid WorkpackId, Guid PhysicalMirrorId, string StagingEvidenceSha256,
    string FullRefProofSha256, string BundleEvidenceSha256,
    string BundleSha256, string SourceRefSetSha256,
    string GitObjectFormat, string ObjectSetSha256, int ObjectCount,
    DateTimeOffset VerifiedAtUtc, string EvidenceSha256,
    bool DurableEvidencePersisted, long F061LeaseGeneration,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
{
    private const string Domain = "f097c4-f060v2-offline-physical-inspection-v1";

    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorOfflineAuditInvalid");

    public static GitIncrementalLaterCheckpointMirrorOfflineAudit Seal(
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        string objectSetSha256, int objectCount, DateTimeOffset verifiedAtUtc)
    {
        try
        {
            GitIncrementalLaterCheckpointMirrorStagingIntegrity
                .VerifyOfflineCandidate(stage, durable);
        }
        catch (GitSourceSnapshotMirrorException) { throw Refused(); }

        if (!GitSourceBundleStorageCheck.ValidSha(objectSetSha256) ||
            objectCount is < 1 or > GitObjectClosureAuditIntegrity.MaxObjects ||
            verifiedAtUtc == default ||
            verifiedAtUtc.Offset != TimeSpan.Zero)
            throw Refused();

        var audit = new GitIncrementalLaterCheckpointMirrorOfflineAudit(
            1, stage.PreparationId, stage.PlanId,
            stage.SourceSnapshotId, stage.CheckpointId,
            stage.TargetObservationId, stage.WorkpackId,
            stage.PhysicalMirrorId, stage.EvidenceSha256,
            stage.SourceFullRefProofSha256, stage.BundleEvidenceSha256,
            stage.BundleSha256, stage.SourceRefSetSha256,
            stage.GitObjectFormat, objectSetSha256, objectCount,
            verifiedAtUtc, "", false, 0, false, false, false, false, false);
        return audit with { EvidenceSha256 = Digest(audit) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorOfflineAudit audit,
        GitIncrementalLaterCheckpointMirrorStagingPlan stage,
        GitIncrementalLaterCheckpointBundleReceipt durable)
    {
        if (audit is null || audit.EvidenceVersion != 1 ||
            audit.DurableEvidencePersisted ||
            audit.F061LeaseGeneration != 0 || audit.CanMaterialize ||
            audit.CanPush || audit.CanRetry || audit.CanDispatch ||
            audit.MigrationComplete || audit != Seal(stage, durable,
                audit.ObjectSetSha256, audit.ObjectCount, audit.VerifiedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorOfflineAudit audit)
    {
        var parts = new object[] {
            Domain, audit.EvidenceVersion,
            audit.PreparationId.ToString("D"), audit.PlanId.ToString("D"),
            audit.SourceSnapshotId.ToString("D"),
            audit.CheckpointId.ToString("D"),
            audit.TargetObservationId.ToString("D"),
            audit.WorkpackId.ToString("D"),
            audit.PhysicalMirrorId.ToString("D"),
            audit.StagingEvidenceSha256, audit.FullRefProofSha256,
            audit.BundleEvidenceSha256, audit.BundleSha256,
            audit.SourceRefSetSha256, audit.GitObjectFormat,
            audit.ObjectSetSha256, audit.ObjectCount,
            audit.VerifiedAtUtc.ToUniversalTime().ToString("O"),
            false, 0L, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }
}
