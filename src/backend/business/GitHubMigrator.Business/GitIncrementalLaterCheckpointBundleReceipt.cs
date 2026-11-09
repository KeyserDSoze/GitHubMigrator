using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #112: domain-separated immutable F-035v2 receipt candidate.
// It is NOT authorization or a durable DB receipt until a source-locked,
// lease-fenced writer independently rehashes the file and INSERTs it
// with one immutable audit event. Never reuse baseline F-035 SHA format.
public sealed record GitIncrementalLaterCheckpointBundleReceipt(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid CheckpointId, Guid SourceSnapshotId, Guid TargetObservationId,
    Guid WorkpackId, string SourceRepositoryId,
    string WorkpackEvidenceSha256, string BundlePlanSha256,
    string FullRefProofSha256, string SourceEvidenceSha256,
    string SourceManifestRootSha256, string TargetReceiptSha256,
    string ReconciliationSha256, string BundleFileName,
    string BundleSha256, long BundleBytes, int RefCount,
    DateTimeOffset AcquiredAtUtc, DateTimeOffset RecordedAtUtc,
    Guid RecordedBy, string EvidenceSha256, bool ReadyForExecution,
    bool CanPush, bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointBundleReceiptIntegrity
{
    private const string Domain = "f097-f035v2-physical-local-bundle-receipt-v1";

    private static GitSourceBundleException Refused() =>
        new("LaterCheckpointBundleReceiptInvalid");

    public static GitIncrementalLaterCheckpointBundleReceipt Seal(
        GitIncrementalLaterCheckpointBundlePlan plan,
        GitIncrementalLaterCheckpointLocalBundleResult checkedFile,
        Guid actorId, DateTimeOffset recordedAtUtc)
    {
        if (plan is null || checkedFile is null ||
            plan.EvidenceVersion != 1 ||
            plan.PreparationId == Guid.Empty ||
            plan.PlanId == Guid.Empty ||
            plan.CheckpointId == Guid.Empty ||
            plan.TargetObservationId == Guid.Empty ||
            plan.SourceSnapshotId == Guid.Empty ||
            plan.BaselineSnapshotId == plan.SourceSnapshotId ||
            actorId == Guid.Empty ||
            plan.SourceOnlyRequest is not {
                WorkpackId: var workpackId,
                ExpectedRefs: { Count: >= 1 and <= 128 } refs
            } ||
            workpackId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(plan.EvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.WorkpackEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.SourceFullRefProofSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.SourceManifestRootSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(plan.TargetReceiptSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                plan.SourceOnlyRequest.ReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                plan.SourceOnlyRequest.SourceEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(checkedFile.BundleSha256) ||
            checkedFile.WorkpackId != workpackId ||
            checkedFile.CheckpointId != plan.CheckpointId ||
            checkedFile.SourceSnapshotId != plan.SourceSnapshotId ||
            checkedFile.ReadPlanSha256 != plan.EvidenceSha256 ||
            checkedFile.SourceFullRefProofSha256 != plan.SourceFullRefProofSha256 ||
            checkedFile.TargetReceiptSha256 != plan.TargetReceiptSha256 ||
            checkedFile.BundleFileName != workpackId.ToString("N") + ".bundle" ||
            checkedFile.BundleBytes is < 1 or > 536_870_912 ||
            checkedFile.RefCount != refs.Count ||
            checkedFile.DurableEvidencePersisted ||
            checkedFile.CanPush || checkedFile.CanRetry ||
            checkedFile.CanDispatch || checkedFile.MigrationComplete ||
            plan.CanAcquireBundle || plan.CanPush || plan.CanRetry ||
            plan.CanDispatch || plan.MigrationComplete ||
            checkedFile.AcquiredAtUtc == default ||
            checkedFile.RecheckedAtUtc < checkedFile.AcquiredAtUtc ||
            recordedAtUtc < checkedFile.RecheckedAtUtc ||
            checkedFile.AcquiredAtUtc.Offset != TimeSpan.Zero ||
            recordedAtUtc.Offset != TimeSpan.Zero ||
            plan.SourceOnlyRequest.SourceRepositoryId is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(plan.SourceOnlyRequest.SourceRepositoryId))
            throw Refused();

        IReadOnlyList<GitMirrorRef> canonical;
        try
        {
            canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(refs);
        }
        catch (GitMirrorRefReconciliationException)
        { throw Refused(); }
        if (!canonical.SequenceEqual(refs) ||
            canonical.Any(x => x.ObjectId.Length != canonical[0].ObjectId.Length))
            throw Refused();

        var receipt = new GitIncrementalLaterCheckpointBundleReceipt(
            1, plan.PreparationId, plan.PlanId,
            plan.CheckpointId, plan.SourceSnapshotId, plan.TargetObservationId,
            workpackId, plan.SourceOnlyRequest.SourceRepositoryId,
            plan.WorkpackEvidenceSha256, plan.EvidenceSha256,
            plan.SourceFullRefProofSha256,
            plan.SourceOnlyRequest.SourceEvidenceSha256,
            plan.SourceManifestRootSha256, plan.TargetReceiptSha256,
            plan.SourceOnlyRequest.ReconciliationSha256,
            checkedFile.BundleFileName, checkedFile.BundleSha256,
            checkedFile.BundleBytes, checkedFile.RefCount,
            checkedFile.AcquiredAtUtc, recordedAtUtc, actorId, "",
            false, false, false, false, false);
        return receipt with { EvidenceSha256 = Digest(receipt) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointBundleReceipt receipt,
        GitIncrementalLaterCheckpointBundlePlan plan,
        GitIncrementalLaterCheckpointLocalBundleResult checkedFile)
    {
        if (receipt is null || receipt.EvidenceVersion != 1 ||
            receipt.ReadyForExecution || receipt.CanPush ||
            receipt.CanRetry || receipt.CanDispatch || receipt.MigrationComplete ||
            receipt != Seal(plan, checkedFile, receipt.RecordedBy,
                receipt.RecordedAtUtc))
            throw Refused();
    }

    private static string Digest(GitIncrementalLaterCheckpointBundleReceipt p)
    {
        var fields = new object?[] {
            Domain, p.EvidenceVersion, p.PreparationId.ToString("D"),
            p.PlanId.ToString("D"), p.CheckpointId.ToString("D"),
            p.SourceSnapshotId.ToString("D"), p.TargetObservationId.ToString("D"),
            p.WorkpackId.ToString("D"), p.SourceRepositoryId,
            p.WorkpackEvidenceSha256, p.BundlePlanSha256,
            p.FullRefProofSha256, p.SourceEvidenceSha256,
            p.SourceManifestRootSha256, p.TargetReceiptSha256,
            p.ReconciliationSha256, p.BundleFileName, p.BundleSha256,
            p.BundleBytes, p.RefCount,
            p.AcquiredAtUtc.ToUniversalTime().ToString("O"),
            p.RecordedAtUtc.ToUniversalTime().ToString("O"),
            p.RecordedBy.ToString("D"),
            false, false, false, false, false
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))));
    }
}
