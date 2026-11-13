using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #123: a prepared SQL isolation intent is a crash-diagnostic fence,
// NOT permission to move a mirror, create an F-061 lease or import Git.
public sealed record GitIncrementalLaterCheckpointMirrorIsolationIntent(
    int EvidenceVersion, Guid PhysicalMirrorId, Guid WorkpackId,
    Guid CheckpointId, Guid SourceSnapshotId,
    Guid OriginalWorkerId, Guid OriginalLeaseToken, long OriginalGeneration,
    string RecoveryDecisionSha256, string InspectionSha256,
    string LeaseEventHistorySha256, string StagingEvidenceSha256,
    string ObjectSetSha256, int ObjectCount,
    string IsolationDirectoryName, DateTimeOffset PreparedAtUtc,
    string IntentSha256, bool CanReAdmit, bool CanMaterialize,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
{
    private const string Domain =
        "f097c4-f061v2-reviewed-source-isolation-intent-v1";

    private static GitSourceSnapshotMirrorException Refused() =>
        new("LaterCheckpointMirrorIsolationIntentInvalid");

    public static GitIncrementalLaterCheckpointMirrorIsolationIntent Seal(
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspected,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision reviewed,
        DateTimeOffset preparedAtUtc)
    {
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
            .VerifyHistoricalForIsolationIntent(reviewed, inspected);
        if (preparedAtUtc == default ||
            preparedAtUtc.Offset != TimeSpan.Zero ||
            preparedAtUtc < inspected.InspectedAtUtc ||
            preparedAtUtc - inspected.InspectedAtUtc >
                TimeSpan.FromMinutes(2) ||
            preparedAtUtc < reviewed.ReviewedAtUtc ||
            inspected.ObjectCount is null or < 1 or > 200000 ||
            !GitSourceBundleStorageCheck.ValidSha(inspected.ObjectSetSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                inspected.LeaseEventHistorySha256))
            throw Refused();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(
            preparedAtUtc.ToUnixTimeMilliseconds());
        var r = new GitIncrementalLaterCheckpointMirrorIsolationIntent(
            1, inspected.PhysicalMirrorId, inspected.WorkpackId,
            inspected.CheckpointId, inspected.SourceSnapshotId,
            inspected.WorkerId, inspected.LeaseToken,
            inspected.LeaseGeneration, reviewed.EvidenceSha256,
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                .ReviewCandidateSha256(inspected),
            inspected.LeaseEventHistorySha256,
            inspected.StagingEvidenceSha256,
            inspected.ObjectSetSha256!, inspected.ObjectCount.Value,
            GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                .IsolationName(reviewed),
            at, "", false, false, false, false, false, false);
        return r with { IntentSha256 = Digest(r) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointMirrorIsolationIntent intent,
        GitIncrementalLaterCheckpointMirrorRecoveryInspection inspected,
        GitIncrementalLaterCheckpointMirrorRecoveryDecision reviewed)
    {
        if (intent is null || intent.EvidenceVersion != 1 ||
            intent.CanReAdmit || intent.CanMaterialize ||
            intent.CanPush || intent.CanRetry ||
            intent.CanDispatch || intent.MigrationComplete ||
            intent != Seal(inspected, reviewed, intent.PreparedAtUtc))
            throw Refused();
    }

    private static string Digest(
        GitIncrementalLaterCheckpointMirrorIsolationIntent r)
    {
        object[] values = {
            Domain, r.EvidenceVersion,
            r.PhysicalMirrorId.ToString("D"), r.WorkpackId.ToString("D"),
            r.CheckpointId.ToString("D"), r.SourceSnapshotId.ToString("D"),
            r.OriginalWorkerId.ToString("D"),
            r.OriginalLeaseToken.ToString("D"), r.OriginalGeneration,
            r.RecoveryDecisionSha256, r.InspectionSha256,
            r.LeaseEventHistorySha256, r.StagingEvidenceSha256,
            r.ObjectSetSha256, r.ObjectCount, r.IsolationDirectoryName,
            r.PreparedAtUtc.ToUniversalTime().ToString("O"),
            false, false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    }
}
