using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubMigrator.Business;

// F-060: physically materialized LOCAL source mirrors are derived only
// from a persisted F-029 snapshot and the corresponding F-038 workpack.
// No GitHub remote URL, credential, refspec, or target write is accepted.
public sealed class GitSourceSnapshotMirrorException(string code)
    : Exception("Source snapshot Git mirror materialization refused")
{
    public string Code { get; } = code;
}

public sealed record GitSourceSnapshotMirrorProof(
    Guid SourceSnapshotId, Guid PhysicalMirrorId, Guid WorkpackId,
    string BundleSha256, string SourceEvidenceSha256,
    string SourceRefSetSha256, int VerifiedRefCount,
    string ObjectSetSha256, int VerifiedObjectCount,
    string EvidenceSha256, DateTimeOffset VerifiedAtUtc,
    bool RemotePushInvoked, bool RemoteWritesAuthorized,
    bool MayAutomaticallyRetry, bool MigrationComplete);

public interface IGitSourceSnapshotMirrorMaterializer
{
    Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
        GitHubTrustedRemoteReadPlan plan, CancellationToken ct);
}

public static class GitSourceSnapshotMirrorIntegrity
{
    // A different workpack for the same F-029 snapshot must get a
    // physically distinct bare mirror: plans can contain many repos.
    public static Guid PhysicalMirrorId(Guid snapshotId, Guid workpackId)
    {
        if (snapshotId == Guid.Empty || workpackId == Guid.Empty)
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorIdentityInvalid");
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(
            "f060-source-snapshot-workpack-v1\n" +
            snapshotId.ToString("D") + "\n" +
            workpackId.ToString("D")));
        return new Guid(sha.AsSpan(0, 16));
    }

    public static string SourceRefsSha(
        IReadOnlyList<GitMirrorRef> refs) =>
        GitImportRehearsalIntegrity.SealRefs(
            "f060-materialized-source-ref-set-v1", refs);

    public static void RequirePlan(GitHubTrustedRemoteReadPlan plan)
    {
        if (plan is null || plan.SourceBundle is null ||
            plan.SourceSnapshotId == Guid.Empty ||
            plan.SourceMirrorId == Guid.Empty ||
            plan.SourceBundle.WorkpackId == Guid.Empty ||
            plan.SourceMirrorId != PhysicalMirrorId(
                plan.SourceSnapshotId, plan.SourceBundle.WorkpackId) ||
            !plan.SourceBundle.IsCurrent ||
            plan.SourceBundle.ReadyForExecution ||
            plan.SourceBundle.PlanId == Guid.Empty ||
            plan.SourceBundle.ReconciliationId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(
                plan.SourceBundle.SourceEvidenceSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                plan.SourceBundle.ReconciliationSha256) ||
            !GitSourceBundleStorageCheck.ValidSha(
                plan.SourceBundle.BundleSha256) ||
            plan.SourceBundle.BundleFileName !=
                plan.SourceBundle.WorkpackId.ToString("N") + ".bundle" ||
            plan.SourceBundle.BundleBytes is < 1 or > 536_870_912 ||
            plan.SourceBundle.SourceEvidenceSha256 !=
                plan.SourceEvidenceSha256 ||
            plan.SourceRefs is null || plan.SourceRefs.Count == 0 ||
            plan.SourceRefs.Count != plan.SourceBundle.RefCount ||
            plan.SourceRefs.Any(x =>
                !x.Name.StartsWith("refs/heads/", StringComparison.Ordinal) &&
                !x.Name.StartsWith("refs/tags/", StringComparison.Ordinal)))
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorPlanNotBound");

        try
        {
            var canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                plan.SourceRefs);
            if (!canonical.SequenceEqual(plan.SourceRefs) ||
                !GitSourceBundleStorageCheck.ValidSha(
                    SourceRefsSha(canonical)))
                throw new GitSourceSnapshotMirrorException(
                    "SourceMirrorRefsInvalid");
        }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorRefsInvalid");
        }
    }

    public static GitSourceSnapshotMirrorProof Seal(
        GitHubTrustedRemoteReadPlan plan, string objectSetSha,
        int objectCount, DateTimeOffset when)
    {
        RequirePlan(plan);
        if (!GitSourceBundleStorageCheck.ValidSha(objectSetSha) ||
            objectCount is < 1 or > GitObjectClosureAuditIntegrity.MaxObjects)
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorObjectsInvalid");
        if (when == default)
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorTimeInvalid");
        var b = plan.SourceBundle!;
        var refSha = SourceRefsSha(plan.SourceRefs);
        var evidence = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
                "f060-verified-local-source-mirror-v1",
                plan.SourceSnapshotId.ToString("D"),
                plan.SourceMirrorId.ToString("D"),
                b.WorkpackId.ToString("D"),
                b.BundleSha256, b.SourceEvidenceSha256,
                refSha, b.RefCount, objectSetSha, objectCount,
                false, false, false, false
            }))));
        return new(plan.SourceSnapshotId, plan.SourceMirrorId,
            b.WorkpackId, b.BundleSha256, b.SourceEvidenceSha256,
            refSha, b.RefCount, objectSetSha, objectCount, evidence, when,
            false, false, false, false);
    }
}
