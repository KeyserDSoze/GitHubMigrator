using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// #112: a non-authorizing source-only F-035v2 preparation, never an
// F-035 file or lease. Caller MUST independently load F-091/F-097b,
// F-031, F-032v2 and F-038v2 from PostgreSQL; self-sealed input is
// not proof of the source or permission to run the local acquirer.
public sealed record GitIncrementalLaterCheckpointBundlePlan(
    int EvidenceVersion, Guid PreparationId, Guid PlanId,
    Guid BaselineSnapshotId, Guid SourceSnapshotId, Guid CheckpointId,
    Guid TargetObservationId, string TargetReceiptSha256,
    string SourceFullRefProofSha256, string SourceManifestRootSha256,
    string WorkpackEvidenceSha256, GitSourceBundleRequest SourceOnlyRequest,
    string EvidenceSha256, bool CanAcquireBundle, bool CanPush,
    bool CanRetry, bool CanDispatch, bool MigrationComplete);

public static class GitIncrementalLaterCheckpointBundleIntegrity
{
    public const int MaxBundleRefs = 128;
    private const string Domain = "f097-f035v2-checkpoint-source-only-bundle-plan-v1";

    private static GitSourceBundleException Refused() =>
        new("LaterCheckpointBundleEvidenceInvalid");

    public static GitIncrementalLaterCheckpointBundlePlan Prepare(
        IncrementalOperationPlan plan,
        GitIncrementalCheckpointAuditInputs selection,
        GitHubSourceCheckpointGitRefProof source,
        GitMirrorRefReconciliation comparison,
        GitIncrementalLaterCheckpointWorkpackProof workpack)
    {
        if (plan is null || selection is null || source is null ||
            comparison is null || workpack is null)
            throw Refused();
        try
        {
            GitIncrementalLaterCheckpointWorkpackIntegrity.Verify(
                workpack, plan, selection, source, comparison);
        }
        catch (GitIncrementalCheckpointWorkpackException)
        { throw Refused(); }

        // Ref/OID arrays must come from F-097b, NOT the F-091 journal's
        // fingerprint hashes or caller-supplied Git command arguments.
        IReadOnlyList<GitMirrorRef> refs;
        try
        {
            refs = GitMirrorRefReconciler.CanonicalizeObservedRefs(source.Refs);
        }
        catch (GitMirrorRefReconciliationException)
        { throw Refused(); }
        if (refs.Count is < 1 or > MaxBundleRefs ||
            !refs.SequenceEqual(source.Refs) ||
            selection.GitObjectFormat != (refs[0].ObjectId.Length == 40
                ? "sha1" : "sha256") ||
            refs.Any(x => x.ObjectId.Length != refs[0].ObjectId.Length) ||
            plan.SourceConnectionId != selection.SourceConnectionId ||
            source.SourceConnectionId != selection.SourceConnectionId ||
            source.SnapshotId != selection.SourceSnapshotId ||
            source.SourceRepositoryId != selection.SourceRepositoryId ||
            workpack.SourceRepositoryId != selection.SourceRepositoryId ||
            workpack.TargetReceiptSha256 != selection.TargetEvidenceSha256 ||
            workpack.CheckpointTargetObservationId != selection.TargetObservationId ||
            workpack.ReconciliationSha256 != comparison.ReconciliationSha256 ||
            workpack.EvidenceVersion != 2 || workpack.State != "Blocked" ||
            workpack.Phase != "WriteBlocked" ||
            workpack.ReadyForExecution || workpack.CanPush ||
            workpack.CanRetry || workpack.CanDispatch ||
            workpack.MigrationComplete)
            throw Refused();

        var request = new GitSourceBundleRequest(
            workpack.WorkpackId, plan.SourceConnectionId,
            selection.SourceRepositoryId, comparison.ReconciliationSha256,
            source.SourceEvidenceSha256, refs.ToArray());

        var unsigned = new GitIncrementalLaterCheckpointBundlePlan(
            1, plan.PreparationId, plan.Id, plan.SnapshotId,
            selection.SourceSnapshotId, selection.SourceCheckpointId,
            selection.TargetObservationId, selection.TargetEvidenceSha256,
            source.EvidenceSha256, source.SourceManifestRootSha256,
            workpack.EvidenceSha256, request, "",
            false, false, false, false, false);
        return unsigned with { EvidenceSha256 = Digest(unsigned) };
    }

    public static void Verify(
        GitIncrementalLaterCheckpointBundlePlan saved,
        IncrementalOperationPlan plan,
        GitIncrementalCheckpointAuditInputs selection,
        GitHubSourceCheckpointGitRefProof source,
        GitMirrorRefReconciliation comparison,
        GitIncrementalLaterCheckpointWorkpackProof workpack)
    {
        if (saved is null || saved.EvidenceVersion != 1 ||
            saved.SourceOnlyRequest is null ||
            saved.CanAcquireBundle || saved.CanPush || saved.CanRetry ||
            saved.CanDispatch || saved.MigrationComplete)
            throw Refused();

        var expected = Prepare(plan, selection, source, comparison, workpack);
        var supplied = saved.SourceOnlyRequest;
        var original = expected.SourceOnlyRequest;
        if (saved with { SourceOnlyRequest = original } != expected ||
            supplied.WorkpackId != original.WorkpackId ||
            supplied.SourceConnectionId != original.SourceConnectionId ||
            supplied.SourceRepositoryId != original.SourceRepositoryId ||
            supplied.ReconciliationSha256 != original.ReconciliationSha256 ||
            supplied.SourceEvidenceSha256 != original.SourceEvidenceSha256 ||
            supplied.ExpectedRefs is null ||
            !supplied.ExpectedRefs.SequenceEqual(original.ExpectedRefs))
            throw Refused();
    }

    private static string Digest(GitIncrementalLaterCheckpointBundlePlan value)
    {
        var request = value.SourceOnlyRequest;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(new object?[] {
            Domain, value.EvidenceVersion,
            value.PreparationId.ToString("D"), value.PlanId.ToString("D"),
            value.BaselineSnapshotId.ToString("D"),
            value.SourceSnapshotId.ToString("D"),
            value.CheckpointId.ToString("D"),
            value.TargetObservationId.ToString("D"),
            value.TargetReceiptSha256, value.SourceFullRefProofSha256,
            value.SourceManifestRootSha256, value.WorkpackEvidenceSha256,
            request.WorkpackId.ToString("D"),
            request.SourceConnectionId.ToString("D"),
            request.SourceRepositoryId, request.ReconciliationSha256,
            request.SourceEvidenceSha256, request.ExpectedRefs.Count,
            false, false, false, false, false
        });
        foreach (var gitRef in request.ExpectedRefs)
            Append(new object?[] { gitRef.Name, gitRef.ObjectId });
        return Convert.ToHexStringLower(sha.GetHashAndReset());

        void Append(object?[] fields) => sha.AppendData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields) + "\n"));
    }
}
