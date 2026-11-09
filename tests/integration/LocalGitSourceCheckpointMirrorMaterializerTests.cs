using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-097c.4a: REAL local SHA-1/SHA-256 Git bare mirror + bundle tests.
// Inputs simulate already PostgreSQL-verified F-091/F-097/F-031 handles.
// No GitHub API, network, F-061 lease or F-095 receipt is used.
public sealed class LocalGitSourceCheckpointMirrorMaterializerTests
{
    private static (
        IncrementalOperationPlan Plan,
        GitIncrementalCheckpointAuditInputs Input,
        GitHubSourceCheckpointGitRefProof FullRef,
        GitSourceBundleView Bundle) Fixture(
        GitHubTrustedRemoteReadPlan original)
    {
        var baseline = Guid.NewGuid();
        var sourceConnection = Guid.NewGuid();
        var preparation = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var manifestSha = new string('d', 64);
        var now = DateTimeOffset.UtcNow;
        const string sourceRepo = "src-repo";
        const string targetRepo = "test-sandbox";
        var plan = new IncrementalOperationPlan(
            planId, preparation, baseline, sourceConnection,
            Guid.NewGuid(), original.Organization,
            new string('a', 64), new string('b', 64),
            new string('c', 64), manifestSha,
            now, Guid.NewGuid(), 1, 6, 1, 0, true, false, [],
            [new IncrementalOperationStep(1, null, sourceRepo,
                targetRepo, "ReviewTargetName", "Blocked", "Blocked", 0)]);

        GitHubSourceSyncCheckpointReceipt Checkpoint(
            Guid snapshot, GitHubSourceSyncCheckpointReceipt? prior)
        {
            var changed = prior is null ? 0 : 1;
            var proof = new GitHubSourceSyncCheckpointProof(
                preparation, planId, sourceConnection, snapshot, baseline,
                prior?.Id, prior?.EvidenceSha256, manifestSha,
                now.AddMinutes(-2), now.AddMinutes(-1),
                false, prior is null, 0, changed, 0, 0, changed, 0,
                [new GitHubSourceRefDelta(sourceRepo, targetRepo,
                    0, changed, 0, 0)]);
            var digest = GitHubSourceSyncCheckpointIntegrity.Digest(proof);
            var refManifest = new GitHubSourceRefChangeManifest(
                sourceConnection, snapshot, 0, changed, 0, 0,
                new string('e', 64));
            var id = Guid.NewGuid();
            return new GitHubSourceSyncCheckpointReceipt(
                id, proof, digest, Guid.NewGuid(), now,
                false, false, false, false,
                refManifest,
                GitHubSourceSyncCheckpointIntegrity.RefAttestationDigest(
                    id, digest, refManifest), 2);
        }

        VerifiedSourceGitRefs Refs(Guid snapshot) =>
            VerifiedSourceGitRefIntegrity.Seal(
                snapshot, sourceConnection, sourceRepo,
                new string('a', 64), original.SourceRefs);

        var initial = Checkpoint(baseline, null);
        var initialProof = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            initial, Refs(baseline));
        var later = Checkpoint(Guid.NewGuid(), initial);
        var full = GitHubSourceCheckpointGitRefProofIntegrity.Seal(
            later, Refs(later.Proof.SnapshotId), initialProof);
        var targetSha = new string('c', 64);
        var comparison = GitMirrorRefReconciler.Compare(new(
            plan.Id, plan.ManifestSha256,
            full.SourceEvidenceSha256, targetSha,
            full.Refs, full.Refs));
        var bundle = original.SourceBundle! with
        {
            PlanId = plan.Id,
            SourceEvidenceSha256 = full.SourceEvidenceSha256,
            ReconciliationSha256 = comparison.ReconciliationSha256
        };
        var input = new GitIncrementalCheckpointAuditInputs(
            1, plan.PreparationId, plan.Id, plan.SourceConnectionId,
            later.Proof.SnapshotId, later.Id,
            later.RefChangeAttestationSha256!, full.EvidenceSha256,
            Guid.NewGuid(), targetSha, sourceRepo, targetRepo,
            full.Refs[0].ObjectId.Length == 40 ? "sha1" : "sha256",
            full.Refs, full.Refs, false, false, false, false);
        return (plan, input, full, bundle);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Later_F097_epoch_imports_real_Git_history_and_recovers_without_rewrite(
        string format)
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var original = f.CreatePlan(format);
        var (plan, input, full, bundle) = Fixture(original);
        var workpack = GitSourceCheckpointLocalMaterializationIntegrity.Bind(
            plan, input, full, bundle);

        Assert.Equal(1, workpack.EvidenceVersion);
        Assert.NotEqual(plan.SnapshotId, workpack.SourceSnapshotId);
        Assert.Equal(input.SourceSnapshotId, workpack.LocalOnlyPlan.SourceSnapshotId);
        Assert.NotEqual(GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                plan.SnapshotId, bundle.WorkpackId),
            workpack.PhysicalMirrorId);
        Assert.Equal(full.EvidenceSha256, workpack.SourceFullRefProofSha256);
        Assert.Equal(full.SourceManifestRootSha256,
            workpack.SourceManifestRootSha256);
        Assert.False(workpack.CanPush || workpack.CanRetry ||
            workpack.CanDispatch || workpack.MigrationComplete);

        var disabled = new LocalGitSourceCheckpointMirrorMaterializer(
            f.Materializer());
        Assert.Equal("SourceCheckpointLocalMaterializationDisabled",
            (await Assert.ThrowsAsync<
                GitSourceCheckpointLocalMaterializationException>(() =>
                    disabled.MaterializeAsync(
                        plan, input, full, bundle, default))).Code);
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git"));

        var importer = new LocalGitSourceCheckpointMirrorMaterializer(
            f.Materializer(), enabled: true);
        var physical = await importer.MaterializeAsync(
            plan, input, full, bundle, default);
        Assert.Equal(workpack.PhysicalMirrorId, physical.PhysicalMirrorId);
        Assert.Equal(input.SourceSnapshotId, physical.SourceSnapshotId);
        Assert.Equal(bundle.BundleSha256, physical.BundleSha256);
        Assert.True(physical.VerifiedObjectCount >= 9);
        Assert.Equal(workpack.LocalOnlyPlan.SourceRefs,
            f.Refs(f.Physical(workpack.LocalOnlyPlan)));
        Assert.False(physical.RemotePushInvoked);
        Assert.False(physical.RemoteWritesAuthorized);
        Assert.False(physical.MayAutomaticallyRetry);
        Assert.False(physical.MigrationComplete);

        // A new worker/process recovers the original UUID.git without
        // replacing it or rewriting any previously verified Git object.
        var replay = await new LocalGitSourceCheckpointMirrorMaterializer(
            f.Materializer(), enabled: true).MaterializeAsync(
                plan, input, full, bundle, default);
        Assert.Equal(physical.EvidenceSha256, replay.EvidenceSha256);
        Assert.Equal(physical.ObjectSetSha256, replay.ObjectSetSha256);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.tmp"));

        // F-060 always rehashes the local bundle even after a replay:
        // a corrupted source archive can never silently validate again.
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(f.BundleRoot, bundle.BundleFileName));
        bytes[bytes.Length / 2] ^= 0x55;
        await File.WriteAllBytesAsync(
            Path.Combine(f.BundleRoot, bundle.BundleFileName), bytes);
        Assert.Equal("SourceMirrorBundleInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => importer.MaterializeAsync(
                    plan, input, full, bundle, default))).Code);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public void Later_F097_workpack_rejects_cross_epoch_ref_bundle_or_target_substitution(
        string format)
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var original = f.CreatePlan(format);
        var (plan, input, full, bundle) = Fixture(original);

        var invalid = new (
            IncrementalOperationPlan Plan,
            GitIncrementalCheckpointAuditInputs Input,
            GitHubSourceCheckpointGitRefProof Full,
            GitSourceBundleView Bundle)[]
        {
            (plan, input with { SourceSnapshotId = plan.SnapshotId }, full, bundle),
            (plan, input with { SourceCheckpointId = Guid.NewGuid() }, full, bundle),
            (plan, input with { SourceRepositoryId = "foreign" }, full, bundle),
            (plan, input with { TargetEvidenceSha256 = new string('f', 64) }, full, bundle),
            (plan, input with { GitObjectFormat = format == "sha1" ? "sha256" : "sha1" }, full, bundle),
            (plan, input with { CanPush = true }, full, bundle),
            (plan with { Steps = [] }, input, full, bundle),
            (plan with { ReadyForExecution = true }, input, full, bundle),
            (plan, input, full with { CanDispatch = true }, bundle),
            (plan, input, full with { SnapshotId = plan.SnapshotId }, bundle),
            (plan, input, full with { Refs =
                full.Refs.Select((r, i) => i == 0
                    ? r with { ObjectId = new string('f', r.ObjectId.Length) }
                    : r).ToArray() }, bundle),
            (plan, input, full, bundle with { PlanId = Guid.NewGuid() }),
            (plan, input, full, bundle with { BundleFileName = "../other.bundle" }),
            (plan, input, full, bundle with { SourceEvidenceSha256 = new string('f', 64) }),
            (plan, input, full, bundle with { ReconciliationSha256 = new string('f', 64) }),
            (plan, input, full, bundle with { RefCount = bundle.RefCount + 1 }),
            (plan, input, full, bundle with { ReadyForExecution = true })
        };
        foreach (var data in invalid)
            Assert.Equal("SourceCheckpointLocalWorkpackInvalid",
                Assert.Throws<GitSourceCheckpointLocalMaterializationException>(
                    () => GitSourceCheckpointLocalMaterializationIntegrity.Bind(
                        data.Plan, data.Input, data.Full, data.Bundle)).Code);
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }
}
