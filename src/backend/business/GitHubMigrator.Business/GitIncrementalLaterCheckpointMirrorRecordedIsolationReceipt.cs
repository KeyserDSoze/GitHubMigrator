using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #124: a durable, audited PHYSICAL QUARANTINE outcome, never a
// re-admission/migration/source-lease capability. This is a different
// kind of evidence from the non-authorizing PREPARED SQL intent.
public sealed record GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt(
    int EvidenceVersion, Guid PhysicalMirrorId, Guid WorkpackId,
    Guid OriginalWorkerId, Guid OriginalLeaseToken,
    long OriginalGeneration, string RecoveryDecisionSha256,
    string PreparedIntentSha256, string OfflineReceiptSha256,
    string IsolationDirectoryName, string ObjectSetSha256,
    int ObjectCount, string BundleSha256, long BundleBytes,
    bool ReconciledExistingQuarantine, DateTimeOffset RecordedAtUtc,
    string EvidenceSha256, bool DurableIsolationRecorded,
    bool CanReAdmit, bool CanMaterialize, bool CanPush,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity
{
    private const string Domain =
        "f097c4-f061v2-reviewed-physical-isolation-sql-v1";
    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorRecordedIsolationInvalid");

    public static GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt
        Seal(
            GitIncrementalLaterCheckpointMirrorRecoveryInspection expected,
            GitIncrementalLaterCheckpointMirrorRecoveryDecision reviewed,
            GitIncrementalLaterCheckpointMirrorIsolationIntent intent,
            GitIncrementalLaterCheckpointBundleReceipt durable,
            GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof physical,
            DateTimeOffset recordedAtUtc)
    {
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
            .VerifyForPhysicalIsolation(reviewed, expected);
        GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
            .Verify(intent, expected, reviewed);
        if (physical is null || recordedAtUtc == default ||
            recordedAtUtc.Offset != TimeSpan.Zero ||
            recordedAtUtc < intent.PreparedAtUtc ||
            physical.VerifiedAtUtc == default ||
            physical.VerifiedAtUtc.Offset != TimeSpan.Zero ||
            physical.VerifiedAtUtc < intent.PreparedAtUtc ||
            physical.VerifiedAtUtc > recordedAtUtc.AddMinutes(2) ||
            recordedAtUtc - physical.VerifiedAtUtc > TimeSpan.FromMinutes(5) ||
            physical.PhysicalMirrorId != intent.PhysicalMirrorId ||
            physical.WorkpackId != intent.WorkpackId ||
            physical.IsolationDirectoryName != intent.IsolationDirectoryName ||
            physical.ObjectSetSha256 != intent.ObjectSetSha256 ||
            physical.ObjectCount != intent.ObjectCount ||
            physical.BundleSha256 != durable.BundleSha256 ||
            physical.BundleBytes != durable.BundleBytes)
            throw Refused();
        var when = DateTimeOffset.FromUnixTimeMilliseconds(
            recordedAtUtc.ToUnixTimeMilliseconds());
        var offline =
            GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity.Seal(
                expected, reviewed, intent.IsolationDirectoryName, when);
        var result = new
            GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt(
                1, intent.PhysicalMirrorId, intent.WorkpackId,
                intent.OriginalWorkerId, intent.OriginalLeaseToken,
                intent.OriginalGeneration, reviewed.EvidenceSha256,
                intent.IntentSha256, offline.EvidenceSha256,
                intent.IsolationDirectoryName, intent.ObjectSetSha256,
                intent.ObjectCount, durable.BundleSha256, durable.BundleBytes,
                physical.RecoveredExistingQuarantine, when, "",
                true, false, false, false, false, false, false);
        return result with { EvidenceSha256 = Digest(result) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt receipt,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection expected,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision reviewed,
        GitIncrementalLaterCheckpointMirrorIsolationIntent intent,
        GitIncrementalLaterCheckpointBundleReceipt durable,
        GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof physical)
    {
        if (receipt is null || receipt.EvidenceVersion != 1 ||
            !receipt.DurableIsolationRecorded || receipt.CanReAdmit ||
            receipt.CanMaterialize || receipt.CanPush ||
            receipt.CanRetry || receipt.CanDispatch ||
            receipt.MigrationComplete ||
            receipt != Seal(expected, reviewed, intent, durable, physical,
                receipt.RecordedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorRecordedIsolationReceipt r)
    {
        object[] fields = {
            Domain, r.EvidenceVersion, r.PhysicalMirrorId.ToString("D"),
            r.WorkpackId.ToString("D"), r.OriginalWorkerId.ToString("D"),
            r.OriginalLeaseToken.ToString("D"), r.OriginalGeneration,
            r.RecoveryDecisionSha256, r.PreparedIntentSha256,
            r.OfflineReceiptSha256, r.IsolationDirectoryName,
            r.ObjectSetSha256, r.ObjectCount,
            r.BundleSha256, r.BundleBytes,
            r.ReconciledExistingQuarantine,
            r.RecordedAtUtc.ToUniversalTime().ToString("O"),
            true, false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))));
    }
}
