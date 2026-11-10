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
    public void F035v2_offline_bundle_plan_binds_later_full_ref_OIDs_and_never_authorizes_acquisition(
        int oidLength)
    {
        var fixture = Fixture(oidLength);
        var plan = fixture.Plan with { IsCurrent = false };
        var selected = fixture.Selection with {
            EvidenceVersion = 2, TargetEvidenceSha256 = new string('f', 64)
        };
        var compared = GitMirrorRefReconciler.Compare(new(
            plan.Id, plan.ManifestSha256, fixture.Source.SourceEvidenceSha256,
            selected.TargetEvidenceSha256, selected.ExpectedSourceRefs,
            selected.ExpectedTargetRefs));
        var workpack = GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
            plan, selected, fixture.Source, compared,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var read = GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
            plan, selected, fixture.Source, compared, workpack);
        var again = GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
            plan, selected, fixture.Source, compared, workpack);
        Assert.Equal(read.EvidenceSha256, again.EvidenceSha256);
        Assert.Equal(read.SourceOnlyRequest.WorkpackId, workpack.WorkpackId);
        Assert.Equal(read.SourceOnlyRequest.SourceConnectionId,
            fixture.Source.SourceConnectionId);
        Assert.Equal(read.SourceOnlyRequest.SourceRepositoryId,
            fixture.Source.SourceRepositoryId);
        Assert.Equal(read.SourceOnlyRequest.SourceEvidenceSha256,
            fixture.Source.SourceEvidenceSha256);
        Assert.Equal(read.SourceOnlyRequest.ReconciliationSha256,
            workpack.ReconciliationSha256);
        Assert.Equal(fixture.Source.Refs, read.SourceOnlyRequest.ExpectedRefs);
        Assert.Equal(workpack.EvidenceSha256, read.WorkpackEvidenceSha256);
        Assert.Equal(workpack.TargetReceiptSha256, read.TargetReceiptSha256);
        Assert.Equal(fixture.Source.SourceManifestRootSha256,
            read.SourceManifestRootSha256);
        Assert.False(read.CanAcquireBundle || read.CanPush ||
            read.CanRetry || read.CanDispatch || read.MigrationComplete);
        Assert.Equal(64, read.EvidenceSha256.Length);
        GitIncrementalLaterCheckpointBundleIntegrity.Verify(
            read, plan, selected, fixture.Source, compared, workpack);

        foreach (var forged in new[] {
            read with { CanAcquireBundle = true },
            read with { CanPush = true },
            read with { EvidenceSha256 = new string('0', 64) },
            read with { SourceSnapshotId = plan.SnapshotId },
            read with { TargetObservationId = Guid.NewGuid() },
            read with { TargetReceiptSha256 = new string('0', 64) },
            read with { WorkpackEvidenceSha256 = new string('0', 64) },
            read with { SourceOnlyRequest = read.SourceOnlyRequest with {
                WorkpackId = Guid.NewGuid() } },
            read with { SourceOnlyRequest = read.SourceOnlyRequest with {
                ExpectedRefs = new[] {
                    new GitMirrorRef("refs/heads/main", new string('0', oidLength)),
                    fixture.Source.Refs[1]
                } } }
        })
            Assert.Equal("LaterCheckpointBundleEvidenceInvalid",
                Assert.Throws<GitSourceBundleException>(() =>
                    GitIncrementalLaterCheckpointBundleIntegrity.Verify(
                        forged, plan, selected, fixture.Source,
                        compared, workpack)).Code);

        Assert.Equal("LaterCheckpointBundleEvidenceInvalid",
            Assert.Throws<GitSourceBundleException>(() =>
                GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
                    plan with { IsCurrent = true }, selected,
                    fixture.Source, compared, workpack)).Code);
        Assert.Equal("LaterCheckpointBundleEvidenceInvalid",
            Assert.Throws<GitSourceBundleException>(() =>
                GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
                    plan, selected with { GitObjectFormat =
                        oidLength == 40 ? "sha256" : "sha1" },
                    fixture.Source, compared, workpack)).Code);
        Assert.Equal("LaterCheckpointBundleEvidenceInvalid",
            Assert.Throws<GitSourceBundleException>(() =>
                GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
                    plan, selected with { SourceRepositoryId = "foreign" },
                    fixture.Source, compared, workpack)).Code);
        Assert.Equal("LaterCheckpointBundleEvidenceInvalid",
            Assert.Throws<GitSourceBundleException>(() =>
                GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
                    plan, selected with { TargetObservationId = Guid.NewGuid() },
                    fixture.Source, compared, workpack)).Code);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void F060v2_staging_binds_durable_checkpoint_bundle_without_legacy_F029_currency(
        int oidLength)
    {
        var x = Fixture(oidLength);
        var plan = x.Plan with { IsCurrent = false };
        var selection = x.Selection with {
            EvidenceVersion = 2, TargetEvidenceSha256 = new string('f', 64)
        };
        var compared = GitMirrorRefReconciler.Compare(new(
            plan.Id, plan.ManifestSha256, x.Source.SourceEvidenceSha256,
            selection.TargetEvidenceSha256,
            selection.ExpectedSourceRefs, selection.ExpectedTargetRefs));
        var pack = GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
            plan, selection, x.Source, compared,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var prepared = GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
            plan, selection, x.Source, compared, pack);
        var now = DateTimeOffset.UtcNow;
        var file = new GitIncrementalLaterCheckpointLocalBundleResult(
            pack.WorkpackId, selection.SourceCheckpointId,
            selection.SourceSnapshotId, prepared.EvidenceSha256,
            x.Source.EvidenceSha256, selection.TargetEvidenceSha256,
            pack.WorkpackId.ToString("N") + ".bundle",
            new string('a', 64), 8192, x.Source.Refs.Count,
            now.AddSeconds(-2), now.AddSeconds(-1), false,
            false, false, false, false);
        var receipt = GitIncrementalLaterCheckpointBundleReceiptIntegrity.Seal(
            prepared, file, pack.RecordedBy, now);
        var staged = GitIncrementalLaterCheckpointMirrorStagingIntegrity.Prepare(
            plan, selection, x.Source, prepared, receipt, pack);
        Assert.Equal(1, staged.EvidenceVersion);
        Assert.Equal(0, staged.F061LeaseGeneration);
        Assert.Equal(selection.SourceSnapshotId, staged.SourceSnapshotId);
        Assert.NotEqual(plan.SnapshotId, staged.SourceSnapshotId);
        Assert.Equal(GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
            selection.SourceSnapshotId, pack.WorkpackId),
            staged.PhysicalMirrorId);
        Assert.NotEqual(GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
            plan.SnapshotId, pack.WorkpackId), staged.PhysicalMirrorId);
        Assert.Equal(receipt.EvidenceSha256, staged.BundleEvidenceSha256);
        Assert.Equal(receipt.BundleSha256, staged.BundleSha256);
        Assert.Equal(receipt.BundleBytes, staged.BundleBytes);
        Assert.Equal(pack.EvidenceSha256, staged.WorkpackEvidenceSha256);
        Assert.Equal(selection.TargetEvidenceSha256, staged.TargetReceiptSha256);
        Assert.Equal(x.Source.Refs, staged.SourceRefs);
        Assert.Equal(GitSourceSnapshotMirrorIntegrity.SourceRefsSha(x.Source.Refs),
            staged.SourceRefSetSha256);
        Assert.Equal(oidLength == 40 ? "sha1" : "sha256",
            staged.GitObjectFormat);
        Assert.False(staged.CanMaterialize || staged.CanPush ||
            staged.CanRetry || staged.CanDispatch ||
            staged.MigrationComplete);
        Assert.Equal(staged.EvidenceSha256,
            GitIncrementalLaterCheckpointMirrorStagingIntegrity.Prepare(
                plan, selection, x.Source, prepared, receipt, pack).EvidenceSha256);
        GitIncrementalLaterCheckpointMirrorStagingIntegrity.Verify(
            staged, plan, selection, x.Source, prepared, receipt, pack);

        // #114: the independent offline shape check does NOT replace the
        // live source-locked F-091/F-097b/F-031 provenance preflight.
        GitIncrementalLaterCheckpointMirrorStagingIntegrity
            .VerifyOfflineCandidate(staged, receipt);
        // #117 only seals a short-lived, non-authorizing read candidate;
        // no caller-generated hash is ever a PostgreSQL F-061 lease.
        var reviewedAt = DateTimeOffset.UtcNow;
        var admit = GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Seal(
            staged, receipt, reviewedAt);
        Assert.Equal(staged.EvidenceSha256, admit.StagingEvidenceSha256);
        Assert.Equal(staged.PhysicalMirrorId, admit.PhysicalMirrorId);
        Assert.Equal(selection.SourceSnapshotId, admit.SourceSnapshotId);
        Assert.Equal(receipt.BundleSha256, admit.BundleSha256);
        Assert.False(admit.DurableLeasePersisted || admit.CanMaterialize ||
            admit.CanPush || admit.CanRetry || admit.CanDispatch ||
            admit.MigrationComplete);
        Assert.Equal(0, admit.F061LeaseGeneration);
        GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Verify(
            admit, staged, receipt);
        foreach (var tampered in new[] {
            admit with { SourceSnapshotId = plan.SnapshotId },
            admit with { PhysicalMirrorId = Guid.NewGuid() },
            admit with { BundleSha256 = new string('0', 64) },
            admit with { StagingEvidenceSha256 = new string('0', 64) },
            admit with { SourceRefSetSha256 = new string('f', 64) },
            admit with { F061LeaseGeneration = 1 },
            admit with { DurableLeasePersisted = true },
            admit with { CanMaterialize = true },
            admit with { CanPush = true },
            admit with { EvidenceSha256 = new string('0', 64) }
        })
            Assert.Equal("LaterCheckpointMirrorAdmissionInvalid",
                Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                    GitIncrementalLaterCheckpointMirrorAdmissionIntegrity
                        .Verify(tampered, staged, receipt)).Code);
        Assert.Equal("LaterCheckpointMirrorAdmissionInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorAdmissionIntegrity.Seal(
                    staged with { SourceSnapshotId = plan.SnapshotId },
                    receipt, reviewedAt)).Code);

        var physical = GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
            .Seal(staged, receipt, new string('c', 64), 25, now);
        Assert.Equal(1, physical.EvidenceVersion);
        Assert.Equal(staged.EvidenceSha256, physical.StagingEvidenceSha256);
        Assert.Equal(staged.PhysicalMirrorId, physical.PhysicalMirrorId);
        Assert.Equal(staged.SourceSnapshotId, physical.SourceSnapshotId);
        Assert.False(physical.DurableEvidencePersisted || physical.CanMaterialize ||
            physical.CanPush || physical.CanRetry || physical.CanDispatch ||
            physical.MigrationComplete);
        Assert.Equal(0, physical.F061LeaseGeneration);
        GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity.Verify(
            physical, staged, receipt);
        foreach (var changed in new[] {
            physical with { SourceSnapshotId = plan.SnapshotId },
            physical with { BundleSha256 = new string('0', 64) },
            physical with { ObjectSetSha256 = new string('0', 64) },
            physical with { ObjectCount = 26 },
            physical with { F061LeaseGeneration = 1 },
            physical with { DurableEvidencePersisted = true },
            physical with { CanPush = true },
            physical with { EvidenceSha256 = new string('0', 64) }
        })
            Assert.Equal("LaterCheckpointMirrorOfflineAuditInvalid",
                Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                    GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity
                        .Verify(changed, staged, receipt)).Code);

        // #119: the durable Verified receipt is domain-separated from
        // self-sealed offline/SQL read witnesses. The PostgreSQL journal
        // must independently run actual Git fsck before using this seal.
        var verifiedWorker = Guid.NewGuid();
        var verifiedLease = Guid.NewGuid();
        var verified = GitIncrementalLaterCheckpointMirrorVerifiedIntegrity.Seal(
            staged, receipt, physical, verifiedWorker,
            verifiedLease, 1, now.AddSeconds(1));
        Assert.True(verified.DurableEvidencePersisted);
        Assert.Equal(staged.PhysicalMirrorId, verified.PhysicalMirrorId);
        Assert.Equal(staged.SourceSnapshotId, verified.SourceSnapshotId);
        Assert.Equal(physical.ObjectSetSha256, verified.ObjectSetSha256);
        Assert.Equal(physical.ObjectCount, verified.ObjectCount);
        Assert.Equal(verifiedWorker, verified.WorkerId);
        Assert.Equal(verifiedLease, verified.LeaseToken);
        Assert.Equal(1, verified.LeaseGeneration);
        Assert.False(verified.CanMaterialize || verified.CanPush ||
            verified.CanRetry || verified.CanDispatch ||
            verified.MigrationComplete);
        GitIncrementalLaterCheckpointMirrorVerifiedIntegrity.Verify(
            verified, staged, receipt, physical);
        foreach (var alteredProof in new[] {
            verified with { WorkerId = Guid.NewGuid() },
            verified with { LeaseToken = Guid.NewGuid() },
            verified with { LeaseGeneration = 2 },
            verified with { ObjectSetSha256 = new string('0',64) },
            verified with { ObjectCount = verified.ObjectCount + 1 },
            verified with { FullRefProofSha256 = new string('f',64) },
            verified with { SourceSnapshotId = plan.SnapshotId },
            verified with { BundleSha256 = new string('0',64) },
            verified with { DurableEvidencePersisted = false },
            verified with { CanMaterialize = true },
            verified with { CanPush = true },
            verified with { EvidenceSha256 = new string('f',64) }
        })
            Assert.Equal("LaterCheckpointMirrorVerifiedInvalid",
                Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                    GitIncrementalLaterCheckpointMirrorVerifiedIntegrity.Verify(
                        alteredProof, staged, receipt, physical)).Code);
        Assert.Equal("LaterCheckpointMirrorVerifiedInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorVerifiedIntegrity.Seal(
                    staged, receipt, physical, verifiedWorker,
                    verifiedLease, 0, now.AddSeconds(1))).Code);

        // #121 recovery diagnostics are sealed but NEVER authorize an
        // orphan release, re-admission, import, GitHub send or F-095.
        // They distinguish a fresh healthy physical Git audit from a
        // missing/corrupt physical mirror without manufacturing Verified.
        var quarantinedAt = now.AddSeconds(3);
        var inspectedAt = now.AddSeconds(5);
        var missing = GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
            .Seal(staged, receipt, verifiedWorker, verifiedLease, 1,
                "ImportWorkerFailed", quarantinedAt, 4, 2,
                new string('d', 64), null, inspectedAt);
        Assert.Equal(
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                .Uninspectable, missing.PhysicalState);
        Assert.Null(missing.ObjectSetSha256);
        Assert.Null(missing.ObjectCount);
        Assert.False(missing.DurableRecoveryApprovalPersisted ||
            missing.CanReAdmit || missing.CanMaterialize ||
            missing.CanPush || missing.CanRetry || missing.CanDispatch ||
            missing.MigrationComplete);
        GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Verify(
            missing, staged, receipt, null);
        var intact = GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
            .Seal(staged, receipt, verifiedWorker, verifiedLease, 1,
                "ImportWorkerFailed", quarantinedAt, 4, 2,
                new string('d', 64), physical, inspectedAt);
        Assert.Equal(
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Intact,
            intact.PhysicalState);
        Assert.Equal(physical.ObjectSetSha256, intact.ObjectSetSha256);
        Assert.Equal(physical.ObjectCount, intact.ObjectCount);
        Assert.NotEqual(missing.EvidenceSha256, intact.EvidenceSha256);
        GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Verify(
            intact, staged, receipt, physical);
        foreach (var changedInspection in new[] {
            intact with { WorkerId = Guid.NewGuid() },
            intact with { LeaseToken = Guid.NewGuid() },
            intact with { LeaseGeneration = 2 },
            intact with { FailureCode = "OtherFailure" },
            intact with { QuarantinedAtUtc = now.AddSeconds(4) },
            intact with { BundleSha256 = new string('0', 64) },
            intact with { SourceSnapshotId = plan.SnapshotId },
            intact with { LeaseEventCount = 5 },
            intact with { RenewedEventCount = 3 },
            intact with { LeaseEventHistorySha256 = new string('0', 64) },
            intact with { ObjectSetSha256 = new string('0', 64) },
            intact with { PhysicalState =
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                    .Uninspectable },
            intact with { CanReAdmit = true },
            intact with { CanPush = true },
            intact with { DurableRecoveryApprovalPersisted = true },
            intact with { EvidenceSha256 = new string('f', 64) }
        })
            Assert.Equal("LaterCheckpointMirrorRecoveryInspectionInvalid",
                Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                    GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                        .Verify(changedInspection, staged, receipt, physical)).Code);
        Assert.Equal("LaterCheckpointMirrorRecoveryInspectionInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                    .Seal(staged, receipt, verifiedWorker, verifiedLease, 1,
                        "ImportWorkerFailed", quarantinedAt, 4, 0,
                        new string('d', 64), null, inspectedAt)).Code);

        // #122: the actual F-031 human reviewer may record ONLY
        // a non-authorizing reviewed isolation decision. An intact
        // source mirror by itself is NOT permission to re-admit.
        var independentReviewerId = Guid.NewGuid();
        while (independentReviewerId == verifiedWorker ||
               independentReviewerId == pack.RecordedBy)
            independentReviewerId = Guid.NewGuid();
        var reviewed = GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
            .Seal(intact, pack.RecordedBy, independentReviewerId,
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview, inspectedAt.AddSeconds(1));
        Assert.Equal(staged.PhysicalMirrorId, reviewed.PhysicalMirrorId);
        Assert.Equal(verifiedWorker, reviewed.OriginalWorkerId);
        Assert.Equal(verifiedLease, reviewed.OriginalLeaseToken);
        Assert.Equal(intact.LeaseEventHistorySha256,
            reviewed.LeaseEventHistorySha256);
        Assert.False(reviewed.CanReAdmit || reviewed.CanMaterialize ||
            reviewed.CanPush || reviewed.CanRetry ||
            reviewed.CanDispatch || reviewed.MigrationComplete);
        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Verify(
            reviewed, intact);
        var rejected = GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
            .Seal(missing, pack.RecordedBy, independentReviewerId,
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Rejected,
                inspectedAt.AddSeconds(1));
        Assert.Equal(
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Rejected,
            rejected.Decision);
        foreach (var falsified in new[] {
            reviewed with { OriginalWorkerId = Guid.NewGuid() },
            reviewed with { OriginalLeaseGeneration = 2 },
            reviewed with { OriginalLeaseToken = Guid.NewGuid() },
            reviewed with { ReviewerId = verifiedWorker },
            reviewed with { OriginalBundleActorId = Guid.NewGuid() },
            reviewed with { Decision =
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Rejected },
            reviewed with { InspectionEvidenceSha256 = new string('0', 64) },
            reviewed with { LeaseEventHistorySha256 = new string('0', 64) },
            reviewed with { CanReAdmit = true },
            reviewed with { CanPush = true },
            reviewed with { EvidenceSha256 = new string('0', 64) }
        })
            Assert.Equal("LaterCheckpointMirrorRecoveryDecisionInvalid",
                Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                    GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                        .Verify(falsified, intact)).Code);
        Assert.Equal("LaterCheckpointMirrorRecoveryDecisionInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .Seal(missing, pack.RecordedBy, independentReviewerId,
                        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                            .ApprovedForIsolationReview,
                        inspectedAt.AddSeconds(1))).Code);
        Assert.Equal("LaterCheckpointMirrorRecoveryDecisionInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .Seal(intact, pack.RecordedBy, pack.RecordedBy,
                        GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                            .Rejected, inspectedAt.AddSeconds(1))).Code);

        foreach (var altered in new[] {
            staged with { CanMaterialize = true },
            staged with { CanPush = true },
            staged with { F061LeaseGeneration = 1 },
            staged with { PhysicalMirrorId = Guid.NewGuid() },
            staged with { SourceSnapshotId = plan.SnapshotId },
            staged with { BundleEvidenceSha256 = new string('0', 64) },
            staged with { TargetObservationId = Guid.NewGuid() },
            staged with { EvidenceSha256 = new string('0', 64) },
            staged with { SourceRefs = [
                x.Source.Refs[0] with { ObjectId = new string('0', oidLength) },
                x.Source.Refs[1]
            ] }
        })
            Assert.Equal("LaterCheckpointMirrorStagingInvalid",
                Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                    GitIncrementalLaterCheckpointMirrorStagingIntegrity.Verify(
                        altered, plan, selection, x.Source,
                        prepared, receipt, pack)).Code);

        Assert.Equal("LaterCheckpointMirrorStagingInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorStagingIntegrity.Prepare(
                    plan with { IsCurrent = true }, selection,
                    x.Source, prepared, receipt, pack)).Code);
        Assert.Equal("LaterCheckpointMirrorStagingInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorStagingIntegrity.Prepare(
                    plan, selection, x.Source, prepared,
                    receipt with { BundleSha256 = new string('f', 64) }, pack)).Code);
        Assert.Equal("LaterCheckpointMirrorStagingInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorStagingIntegrity.Prepare(
                    plan, selection with { TargetObservationId = Guid.NewGuid() },
                    x.Source, prepared, receipt, pack)).Code);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void F035v2_preparation_is_bounded_to_128_canonical_Git_refs(
        int oidLength)
    {
        var fixture = Fixture(oidLength);
        var plan = fixture.Plan with { IsCurrent = false };
        foreach (var count in new[] { 128, 129 })
        {
            var refs = Enumerable.Range(0, count).Select(i =>
                new GitMirrorRef($"refs/heads/branch-{i:D3}",
                    new string('a', oidLength))).ToArray();
            var source = fixture.Source with { Refs = refs };
            var selected = fixture.Selection with {
                EvidenceVersion = 2,
                TargetEvidenceSha256 = new string('f', 64),
                ExpectedSourceRefs = refs
            };
            var compared = GitMirrorRefReconciler.Compare(new(
                plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
                selected.TargetEvidenceSha256,
                selected.ExpectedSourceRefs, selected.ExpectedTargetRefs));
            if (count == 128)
            {
                var workpack = GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
                    plan, selected, source, compared,
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
                var planForBundle = GitIncrementalLaterCheckpointBundleIntegrity
                    .Prepare(plan, selected, source, compared, workpack);
                Assert.Equal(128, planForBundle.SourceOnlyRequest.ExpectedRefs.Count);
                Assert.False(planForBundle.CanAcquireBundle);
            }
            else
            {
                // The 128-ref limit is enforced already by F-032v2:
                // F-035v2 cannot receive an accepted 129-ref workpack.
                Assert.Equal("LaterCheckpointWorkpackEvidenceInvalid",
                    Assert.Throws<GitIncrementalCheckpointWorkpackException>(() =>
                        GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
                            plan, selected, source, compared,
                            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())).Code);
            }
        }
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
