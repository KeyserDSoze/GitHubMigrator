using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #123: NON-AUTHORIZING evidence of a local-only UUID.git rename.
// This is NOT a SQL-backed recovery approval, a new generation, or
// permission to recreate the original UUID.git. The caller must bind
// a future immutable source-locked PostgreSQL isolation event.
public sealed record GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt(
    int EvidenceVersion, Guid PhysicalMirrorId, Guid WorkpackId,
    Guid SourceSnapshotId, Guid CheckpointId,
    Guid OriginalWorkerId, Guid OriginalLeaseToken, long OriginalGeneration,
    string StagingEvidenceSha256, string RecoveryDecisionSha256,
    string OrderedEventHistorySha256, string ObjectSetSha256,
    int ObjectCount, string IsolatedDirectoryName,
    DateTimeOffset IsolatedAtUtc, string EvidenceSha256,
    bool DurableIsolationRecorded, bool CanReAdmit,
    bool CanMaterialize, bool CanPush, bool CanRetry,
    bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
{
    private const string Domain = "f097c4-f061v2-orphan-isolation-local-v1";
    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorOrphanIsolationInvalid");

    public static GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt Seal(
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision,
        string quarantineDirectoryName,
        DateTimeOffset isolatedAtUtc)
    {
        if (decision.Decision ==
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .ApprovedForIsolationReview)
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .VerifyForPhysicalIsolation(decision, inspection);
        else
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                .Verify(decision, inspection);
        if (decision.Decision !=
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview ||
            inspection.PhysicalState !=
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Intact ||
            !GitSourceBundleStorageCheck.ValidSha(inspection.ObjectSetSha256) ||
            inspection.ObjectCount is null or < 1 or > 200000 ||
            quarantineDirectoryName != IsolationName(decision) ||
            isolatedAtUtc == default ||
            isolatedAtUtc.Offset != TimeSpan.Zero ||
            isolatedAtUtc < decision.ReviewedAtUtc)
            throw Refused();

        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            isolatedAtUtc.ToUnixTimeMilliseconds());
        var r = new GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt(
            1, inspection.PhysicalMirrorId, inspection.WorkpackId,
            inspection.SourceSnapshotId, inspection.CheckpointId,
            inspection.WorkerId, inspection.LeaseToken,
            inspection.LeaseGeneration, inspection.StagingEvidenceSha256,
            decision.EvidenceSha256, inspection.LeaseEventHistorySha256,
            inspection.ObjectSetSha256, inspection.ObjectCount.Value,
            quarantineDirectoryName, at, "",
            false, false, false, false, false, false, false);
        return r with { EvidenceSha256 = Digest(r) };
    }

    public static string IsolationName(
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision)
    {
        if (decision is null || decision.PhysicalMirrorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(decision.EvidenceSha256))
            throw Refused();
        // Deterministic name supports crash diagnosis without ever
        // overwriting or re-adopting a previous orphan.
        return decision.PhysicalMirrorId.ToString("N") + "." +
            decision.EvidenceSha256 + ".quarantine.git";
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt receipt,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspection,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision decision)
    {
        if (receipt is null || receipt.EvidenceVersion != 1 ||
            receipt.DurableIsolationRecorded || receipt.CanReAdmit ||
            receipt.CanMaterialize || receipt.CanPush ||
            receipt.CanRetry || receipt.CanDispatch ||
            receipt.MigrationComplete ||
            receipt != Seal(inspection, decision,
                receipt.IsolatedDirectoryName, receipt.IsolatedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorOrphanIsolationReceipt r)
    {
        object[] parts = {
            Domain, r.EvidenceVersion, r.PhysicalMirrorId.ToString("D"),
            r.WorkpackId.ToString("D"), r.SourceSnapshotId.ToString("D"),
            r.CheckpointId.ToString("D"), r.OriginalWorkerId.ToString("D"),
            r.OriginalLeaseToken.ToString("D"), r.OriginalGeneration,
            r.StagingEvidenceSha256, r.RecoveryDecisionSha256,
            r.OrderedEventHistorySha256, r.ObjectSetSha256,
            r.ObjectCount, r.IsolatedDirectoryName,
            r.IsolatedAtUtc.ToUniversalTime().ToString("O"),
            false, false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }
}
