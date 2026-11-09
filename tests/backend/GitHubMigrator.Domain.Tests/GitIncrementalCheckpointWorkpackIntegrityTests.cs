using GitHubMigrator.Business;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// F-097c.4c.1: deterministic proof-format tests only. These synthetic
// fixtures do NOT replace F-091/F-097b/F-014 PostgreSQL provenance.
public sealed class GitIncrementalCheckpointWorkpackIntegrityTests
{
    private static (
        IncrementalOperationPlan Plan,
        GitIncrementalCheckpointAuditInputs Selection,
        GitHubSourceCheckpointGitRefProof Source,
        GitMirrorRefReconciliation Reconciliation) Fixture(int length)
    {
        var preparationId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sourceConnectionId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        var laterSnapshotId = Guid.NewGuid();
        var checkpointId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var sha = new string('d', 64);
        var sourceRefs = new[] {
            new GitMirrorRef("refs/heads/main", new string('a', length)),
            new GitMirrorRef("refs/tags/v1", new string('b', length))
        };
        var targetRefs = new[] {
            new GitMirrorRef("refs/heads/main", new string('c', length))
        };
        var plan = new IncrementalOperationPlan(
            planId, preparationId, baselineId, sourceConnectionId,
            Guid.NewGuid(), "gh-org", new string('a', 64),
            new string('b', 64), new string('c', 64),
            sha, now, Guid.NewGuid(), 1, 6, 1, 0, true, false, [],
            [new IncrementalOperationStep(1, null, "repo-1", "sandbox",
                "ReviewTargetName", "Blocked", "Blocked", 0)]);
        var source = new GitHubSourceCheckpointGitRefProof(
            1, checkpointId, preparationId, planId, sourceConnectionId,
            laterSnapshotId, baselineId, "repo-1",
            new string('c', 64), new string('e', 64),
            new string('f', 64), new string('a', 64),
            Guid.NewGuid(), new string('b', 64),
            sourceRefs, new string('d', 64),
            false, false, false, false);
        var selection = new GitIncrementalCheckpointAuditInputs(
            1, preparationId, planId, sourceConnectionId,
            laterSnapshotId, checkpointId, source.CheckpointRefAttestationSha256,
            source.EvidenceSha256, targetId, new string('a', 64),
            "repo-1", "sandbox", length == 40 ? "sha1" : "sha256",
            sourceRefs, targetRefs, false, false, false, false);
        var comparison = GitMirrorRefReconciler.Compare(new(
            plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
            selection.TargetEvidenceSha256, sourceRefs, targetRefs));
        return (plan, selection, source, comparison);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Later_checkpoint_F031_v2_seal_is_strictly_non_executable(
        int oidLength)
    {
        var x = Fixture(oidLength);
        var plan = x.Plan with { IsCurrent = false };
        var selection = x.Selection with {
            EvidenceVersion = 2,
            TargetEvidenceSha256 = new string('f', 64)
        };
        var recId = Guid.NewGuid();
        var packId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var compared = GitMirrorRefReconciler.Compare(new(
            plan.Id, plan.ManifestSha256, x.Source.SourceEvidenceSha256,
            selection.TargetEvidenceSha256,
            selection.ExpectedSourceRefs, selection.ExpectedTargetRefs));
        var first = GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
            plan, selection, x.Source, compared, recId, packId, actor);
        Assert.Equal(2, first.EvidenceVersion);
        Assert.Equal("Blocked", first.State);
        Assert.Equal("WriteBlocked", first.Phase);
        Assert.Equal(selection.TargetEvidenceSha256, first.TargetReceiptSha256);
        Assert.False(first.ReadyForExecution || first.CanPush ||
            first.CanRetry || first.CanDispatch || first.MigrationComplete);
        Assert.Equal(first, GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
            plan, selection, x.Source, compared, recId, packId, actor));
        GitIncrementalLaterCheckpointWorkpackIntegrity.Verify(
            first, plan, selection, x.Source, compared);

        foreach (var invalid in new[] {
            first with { CanPush = true },
            first with { ReadyForExecution = true },
            first with { Phase = "Ready" },
            first with { TargetReceiptSha256 = new string('0', 64) },
            first with { SourceFullRefProofSha256 = new string('0', 64) },
            first with { CheckpointId = Guid.NewGuid() },
            first with { EvidenceSha256 = new string('0', 64) }
        })
            Assert.Equal("LaterCheckpointWorkpackEvidenceInvalid",
                Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                    GitIncrementalLaterCheckpointWorkpackIntegrity.Verify(
                        invalid, plan, selection, x.Source, compared)).Code);

        foreach (var invalidSelection in new[] {
            selection with { EvidenceVersion = 1 },
            selection with { TargetObservationId = Guid.NewGuid(),
                TargetEvidenceSha256 = new string('z', 64) },
            selection with { CanDispatch = true },
            selection with { SourceSnapshotId = plan.SnapshotId }
        })
            Assert.Equal("LaterCheckpointWorkpackEvidenceInvalid",
                Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                    GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
                        plan, invalidSelection, x.Source, compared,
                        recId, packId, actor)).Code);
        Assert.Equal("LaterCheckpointWorkpackEvidenceInvalid",
            Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
                    plan with { IsCurrent = true }, selection, x.Source,
                    compared, recId, packId, actor)).Code);
        Assert.Equal("LaterCheckpointWorkpackEvidenceInvalid",
            Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
                    plan, selection, x.Source, compared,
                    recId, packId, Guid.Empty)).Code);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Witness_is_versioned_deterministic_and_never_grants_push(int oidLength)
    {
        var x = Fixture(oidLength);
        var reconciliationId = Guid.NewGuid();
        var workpackId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var first = GitIncrementalCheckpointWorkpackIntegrity.Seal(
            x.Plan, x.Selection, x.Source, x.Reconciliation,
            reconciliationId, workpackId, actor);
        var second = GitIncrementalCheckpointWorkpackIntegrity.Seal(
            x.Plan, x.Selection, x.Source, x.Reconciliation,
            reconciliationId, workpackId, actor);
        Assert.Equal(first, second);
        Assert.Equal(1, first.EvidenceVersion);
        Assert.NotEqual(x.Plan.SnapshotId, first.SourceSnapshotId);
        Assert.Equal(x.Source.SourceManifestRootSha256,
            first.SourceManifestRootSha256);
        Assert.Equal(x.Source.EvidenceSha256, first.FullRefProofSha256);
        Assert.Equal(64, first.EvidenceSha256.Length);
        Assert.False(first.CanPush || first.CanRetry ||
            first.CanDispatch || first.MigrationComplete);
        GitIncrementalCheckpointWorkpackIntegrity.Verify(
            first, x.Plan, x.Selection, x.Source, x.Reconciliation);

        var other = GitIncrementalCheckpointWorkpackIntegrity.Seal(
            x.Plan, x.Selection, x.Source, x.Reconciliation,
            reconciliationId, Guid.NewGuid(), actor);
        Assert.NotEqual(first.EvidenceSha256, other.EvidenceSha256);
        Assert.Equal("CheckpointWorkpackEvidenceInvalid",
            Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                GitIncrementalCheckpointWorkpackIntegrity.Verify(
                    first with { CanPush = true }, x.Plan,
                    x.Selection, x.Source, x.Reconciliation)).Code);
        Assert.Equal("CheckpointWorkpackEvidenceInvalid",
            Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                GitIncrementalCheckpointWorkpackIntegrity.Verify(
                    first with { SourceManifestRootSha256 = new string('f', 64) },
                    x.Plan, x.Selection, x.Source, x.Reconciliation)).Code);
        foreach (var source in new[] {
            x.Source with { SnapshotId = x.Plan.SnapshotId },
            x.Source with { EvidenceSha256 = new string('a', 64) },
            x.Source with { CanDispatch = true }
        })
            Assert.Equal("CheckpointWorkpackEvidenceInvalid",
                Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                    GitIncrementalCheckpointWorkpackIntegrity.Seal(
                        x.Plan, x.Selection, source, x.Reconciliation,
                        reconciliationId, workpackId, actor)).Code);
        Assert.Equal("CheckpointWorkpackEvidenceInvalid",
            Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                GitIncrementalCheckpointWorkpackIntegrity.Seal(
                    x.Plan, x.Selection with { SourceSnapshotId = x.Plan.SnapshotId },
                    x.Source, x.Reconciliation, reconciliationId,
                    workpackId, actor)).Code);
    }
}
