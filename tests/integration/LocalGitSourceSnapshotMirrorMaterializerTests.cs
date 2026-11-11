using System.Diagnostics;
using System.Security.Cryptography;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-060 uses actual Git bundles (SHA1/256) and a real bare Git import.
// No GitHub token, remote push or test-network access is necessary.
public sealed class LocalGitSourceSnapshotMirrorMaterializerTests
{
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Verified_F038_bundle_becomes_an_atomic_full_history_source_for_F057(
        string format)
    {
        using var f = new Fixture();
        var plan = f.CreatePlan(format);
        var proof = await f.Materializer().MaterializeAsync(plan, default);
        Assert.Equal(plan.SourceMirrorId, proof.PhysicalMirrorId);
        Assert.Equal(plan.SourceSnapshotId, proof.SourceSnapshotId);
        Assert.Equal(plan.SourceBundle!.BundleSha256, proof.BundleSha256);
        Assert.Equal(format == "sha1" ? 40 : 64,
            plan.SourceRefs[0].ObjectId.Length);
        Assert.True(proof.VerifiedObjectCount >= 9);
        Assert.Equal(64, proof.ObjectSetSha256.Length);
        Assert.False(proof.RemotePushInvoked);
        Assert.False(proof.RemoteWritesAuthorized);
        Assert.False(proof.MayAutomaticallyRetry);
        Assert.False(proof.MigrationComplete);
        Assert.Equal(plan.SourceRefs, f.Refs(f.Physical(plan)));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.tmp"));
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));

        // Independent post-crash process: no overwrite or re-import.
        var repeated = await f.Materializer().MaterializeAsync(plan, default);
        Assert.Equal(proof.EvidenceSha256, repeated.EvidenceSha256);
        Assert.Equal(proof.ObjectSetSha256, repeated.ObjectSetSha256);

        // Same real F-057 read-only fetch and strict F-056 fsck closure
        // that the F-059 coordinator would invoke. The origin is PRIVATE.
        var remote = Path.Combine(f.Root, "private-origin.git");
        f.Git("clone", "--mirror", "-q", f.SourceBare, remote);
        var observed = GitHubRefObservationIntegrity.Seal(
            "gh-org", "test-sandbox", DateTimeOffset.UtcNow,
            f.TypedRefs(remote));
        var scratch = Path.Combine(f.Root, "download-scratch");
        Directory.CreateDirectory(scratch);
        var mirror = new GitHubReadOnlyObjectMirrorRunner(
            f.MirrorRoot, scratch,
            new(true, 987654321, "gh-org/test-sandbox"), remote);
        var receipt = await mirror.VerifyAsync(new(
            plan.SourceMirrorId, 987654321, "gh-org", "test-sandbox",
            plan.SourceRefs, observed,
            DateTimeOffset.UtcNow.AddMinutes(30)),
            "test-only-ephemeral-token", default);
        Assert.Equal(format, receipt.GitObjectFormat);
        Assert.Equal(proof.ObjectSetSha256,
            receipt.SourceObjectSetSha256);
        Assert.Equal(receipt.SourceObjectSetSha256,
            receipt.RemoteObjectSetSha256);
        Assert.False(receipt.MigrationComplete);
        Assert.False(receipt.RemotePushInvoked);
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [Fact]
    public async Task Six_replicas_recover_one_immutable_source_directory()
    {
        using var f = new Fixture();
        var plan = f.CreatePlan();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            f.Materializer().MaterializeAsync(plan, default)));
        Assert.All(results, x =>
        {
            Assert.Equal(results[0].EvidenceSha256, x.EvidenceSha256);
            Assert.Equal(results[0].ObjectSetSha256, x.ObjectSetSha256);
        });
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.tmp"));
        Assert.Equal(plan.SourceRefs, f.Refs(f.Physical(plan)));
    }

    [Fact]
    public async Task Two_workpacks_in_one_snapshot_cannot_overwrite_each_other()
    {
        using var f = new Fixture();
        var first = f.CreatePlan();
        var second = f.CopyForAnotherWorkpack(first);
        Assert.Equal(first.SourceSnapshotId, second.SourceSnapshotId);
        Assert.NotEqual(first.SourceMirrorId, second.SourceMirrorId);
        var a = await f.Materializer().MaterializeAsync(first, default);
        var b = await f.Materializer().MaterializeAsync(second, default);
        Assert.NotEqual(a.PhysicalMirrorId, b.PhysicalMirrorId);
        Assert.Equal(2, Directory.GetDirectories(f.MirrorRoot, "*.git").Length);
        Assert.Equal(a.ObjectSetSha256, b.ObjectSetSha256);
    }

    [Fact]
    public async Task Altered_bundle_bytes_fail_closed_even_on_replay()
    {
        using var f = new Fixture();
        var plan = f.CreatePlan();
        await f.Materializer().MaterializeAsync(plan, default);
        var bundle = Path.Combine(f.BundleRoot, plan.SourceBundle!.BundleFileName);
        var bytes = await File.ReadAllBytesAsync(bundle);
        bytes[bytes.Length / 2] ^= 0x55;
        await File.WriteAllBytesAsync(bundle, bytes);
        var ex = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => f.Materializer().MaterializeAsync(plan, default));
        Assert.Equal("SourceMirrorBundleInvalid", ex.Code);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }

    [Fact]
    public async Task Damaged_imported_objects_are_never_silently_repaired_or_reused()
    {
        using var f = new Fixture();
        var plan = f.CreatePlan();
        await f.Materializer().MaterializeAsync(plan, default);
        var physical = f.Physical(plan);
        f.Git("--git-dir=" + physical, "repack", "-a", "-d");
        f.Git("--git-dir=" + physical, "prune-packed");
        var pack = Assert.Single(Directory.GetFiles(
            Path.Combine(physical, "objects", "pack"), "*.pack"));
        File.SetUnixFileMode(pack,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllBytesAsync(pack, [1, 2, 3, 4, 5]);
        var ex = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => f.Materializer().MaterializeAsync(plan, default));
        Assert.Equal("SourceMirrorGitClosureInvalid", ex.Code);
        Assert.True(Directory.Exists(physical));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.tmp"));
    }

    [Fact]
    public async Task Disabled_symlinked_root_untrusted_bundle_and_cancellation_are_rejected()
    {
        using var f = new Fixture();
        var plan = f.CreatePlan();
        var disabled = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, f.MirrorRoot,
            new LocalGitBundleArtifactStore(f.BundleRoot));
        Assert.Equal("SourceMirrorDisabled",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => disabled.MaterializeAsync(plan, default))).Code);

        var linked = Path.Combine(f.Root, "linked-root");
        Directory.CreateSymbolicLink(linked, f.MirrorRoot);
        var unsafeRunner = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, linked,
            new LocalGitBundleArtifactStore(f.BundleRoot), enabled: true);
        Assert.Equal("SourceMirrorSymlinkBlocked",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => unsafeRunner.MaterializeAsync(plan, default))).Code);

        var wrong = plan with { SourceBundle = plan.SourceBundle! with {
            BundleFileName = "../outside.bundle" } };
        Assert.Equal("SourceMirrorPlanNotBound",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => f.Materializer().MaterializeAsync(wrong, default))).Code);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.Materializer().MaterializeAsync(plan, cts.Token));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }

    [Fact]
    public async Task Old_recognized_staging_is_cleaned_without_touching_current_or_final_bare()
    {
        using var f = new Fixture();
        var plan = f.CreatePlan();
        await f.Materializer().MaterializeAsync(plan, default);
        var old = Path.Combine(f.MirrorRoot,
            plan.SourceMirrorId.ToString("N") + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        var fresh = Path.Combine(f.MirrorRoot,
            plan.SourceMirrorId.ToString("N") + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        var foreign = Path.Combine(f.MirrorRoot, "foreign.tmp");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(old, "partial"), "partial");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));

        var runner = f.Materializer();
        Assert.Equal("SourceMirrorCleanupAgeTooShort",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => runner.CleanupStaleStagingAsync(
                    TimeSpan.FromHours(1), default))).Code);
        Assert.Equal(1, await runner.CleanupStaleStagingAsync(
            TimeSpan.FromHours(24), default));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(f.Physical(plan)));
        Assert.Equal(plan.SourceRefs, f.Refs(f.Physical(plan)));
    }

    [Fact]
    public async Task Recognized_stale_symlink_is_never_followed_by_cleanup()
    {
        using var f = new Fixture();
        var plan = f.CreatePlan();
        var outside = Path.Combine(f.Root, "outside-protected");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(
            Path.Combine(outside, "never-delete"), "protected");
        var link = Path.Combine(f.MirrorRoot,
            plan.SourceMirrorId.ToString("N") + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateSymbolicLink(link, outside);
        var ex = await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
            () => f.Materializer().CleanupStaleStagingAsync(
                TimeSpan.FromHours(24), default));
        Assert.Equal("SourceMirrorCleanupUnsafeEntry", ex.Code);
        Assert.True(File.Exists(Path.Combine(outside, "never-delete")));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Later_checkpoint_offline_audit_checks_real_existing_Git_and_bundle(
        string format)
    {
        using var f = new Fixture();
        var sourcePlan = f.CreatePlan(format);
        var (stage, durable) = LaterOfflineEvidence(sourcePlan);
        var disabled = new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
            f.BundleRoot, f.MirrorRoot);
        Assert.Equal("LaterCheckpointMirrorOfflineAuditDisabled",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => disabled.AuditAsync(stage, durable, default))).Code);

        var auditor = new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
            f.BundleRoot, f.MirrorRoot, enabled: true);
        Assert.Equal("LaterCheckpointMirrorOfflineClosureInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(
                () => auditor.AuditAsync(stage, durable, default))).Code);

        // Only the pre-existing F-060 TEST fixture creates the bare mirror.
        // The new #114 offline auditor itself NEVER imports or modifies it.
        await f.Materializer().MaterializeAsync(sourcePlan, default);
        var first = await auditor.AuditAsync(stage, durable, default);
        Assert.Equal(stage.SourceSnapshotId, first.SourceSnapshotId);
        Assert.Equal(stage.CheckpointId, first.CheckpointId);
        Assert.Equal(stage.PhysicalMirrorId, first.PhysicalMirrorId);
        Assert.Equal(stage.BundleSha256, first.BundleSha256);
        Assert.Equal(format, first.GitObjectFormat);
        Assert.True(first.ObjectCount >= 9);
        Assert.Equal(64, first.ObjectSetSha256.Length);
        Assert.False(first.DurableEvidencePersisted || first.CanMaterialize ||
            first.CanPush || first.CanRetry || first.CanDispatch ||
            first.MigrationComplete);
        Assert.Equal(0, first.F061LeaseGeneration);
        GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity.Verify(
            first, stage, durable);
        var second = await auditor.AuditAsync(stage, durable, default);
        Assert.Equal(first.ObjectSetSha256, second.ObjectSetSha256);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));

        Assert.Equal("LaterCheckpointMirrorStagingInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                auditor.AuditAsync(stage with {
                    SourceRefs = stage.SourceRefs.Select((r, i) =>
                        i == 0 ? r with { ObjectId =
                            new string('0', r.ObjectId.Length) } : r).ToArray()
                }, durable, default))).Code);

        // An otherwise valid bundle cannot stand in for a broken,
        // non-self-contained bare object graph.
        var alternates = Path.Combine(f.Physical(sourcePlan),
            "objects", "info", "alternates");
        Directory.CreateDirectory(Path.GetDirectoryName(alternates)!);
        await File.WriteAllTextAsync(alternates, "/nonexistent");
        Assert.Equal("LaterCheckpointMirrorOfflineClosureInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                auditor.AuditAsync(stage, durable, default))).Code);
        File.Delete(alternates);

        var bundle = Path.Combine(f.BundleRoot, durable.BundleFileName);
        await File.AppendAllTextAsync(bundle, "altered");
        Assert.Equal("LaterCheckpointMirrorOfflineBundleChanged",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                auditor.AuditAsync(stage, durable, default))).Code);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F060v2_imports_real_later_checkpoint_bundle_without_legacy_currentness(
        string format)
    {
        using var f = new Fixture();
        var baseline = f.CreatePlan(format);
        var (stage, durable) = LaterOfflineEvidence(baseline);

        // This is an INTERNAL local-only adapter, not a registered F-061
        // worker. Legacy F-060 and v2 feature switches stay independent.
        var oldOnly = f.Materializer();
        Assert.Equal("LaterCheckpointMirrorMaterializationDisabled",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                oldOnly.MaterializeLaterCheckpointAsync(stage, durable, default))).Code);
        var local = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, f.MirrorRoot,
            new LocalGitBundleArtifactStore(f.BundleRoot),
            enabled: false, laterCheckpointEnabled: true);
        Assert.Equal("SourceMirrorDisabled",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                local.MaterializeAsync(baseline, default))).Code);
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git"));

        var first = await local.MaterializeLaterCheckpointAsync(
            stage, durable, default);
        Assert.Equal(stage.SourceSnapshotId, first.SourceSnapshotId);
        Assert.Equal(stage.PhysicalMirrorId, first.PhysicalMirrorId);
        Assert.Equal(stage.SourceFullRefProofSha256, first.FullRefProofSha256);
        Assert.Equal(stage.BundleSha256, first.BundleSha256);
        Assert.Equal(format, first.GitObjectFormat);
        Assert.True(first.ObjectCount >= 9);
        Assert.False(first.DurableEvidencePersisted || first.CanMaterialize ||
            first.CanPush || first.CanRetry || first.CanDispatch ||
            first.MigrationComplete);
        Assert.Equal(0, first.F061LeaseGeneration);
        GitIncrementalLaterCheckpointMirrorOfflineAuditIntegrity.Verify(
            first, stage, durable);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
        Assert.Equal(stage.SourceRefs, f.Refs(f.Physical(baseline)));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.tmp"));

        // Multiple local callers may read/reverify the same immutable
        // UUID.git but must not make a second physical import.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            local.MaterializeLaterCheckpointAsync(stage, durable, default)));
        Assert.All(results, x =>
        {
            Assert.Equal(first.ObjectSetSha256, x.ObjectSetSha256);
            Assert.Equal(first.PhysicalMirrorId, x.PhysicalMirrorId);
            Assert.False(x.MigrationComplete);
        });
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));

        // The #114 read-only inspector agrees with the new physical import.
        var reader = new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
            f.BundleRoot, f.MirrorRoot, enabled: true);
        var checkedMirror = await reader.AuditAsync(stage, durable, default);
        Assert.Equal(first.ObjectSetSha256, checkedMirror.ObjectSetSha256);
        Assert.Equal(first.ObjectCount, checkedMirror.ObjectCount);

        // After promotion, refuse corrupted source bundle replay. Existing
        // immutable mirror is never deleted, overwritten or silently healed.
        await File.AppendAllTextAsync(Path.Combine(f.BundleRoot,
            durable.BundleFileName), "changed");
        Assert.Equal("LaterCheckpointMirrorBundleChanged",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                local.MaterializeLaterCheckpointAsync(stage, durable, default))).Code);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F060v2_import_refuses_wrong_stage_missing_graph_and_unsafe_root(
        string format)
    {
        using var f = new Fixture();
        var baseline = f.CreatePlan(format);
        var (stage, durable) = LaterOfflineEvidence(baseline);
        var local = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, f.MirrorRoot,
            new LocalGitBundleArtifactStore(f.BundleRoot),
            laterCheckpointEnabled: true);

        Assert.Equal("LaterCheckpointMirrorStagingInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                local.MaterializeLaterCheckpointAsync(stage with {
                    SourceSnapshotId = stage.BaselineSnapshotId
                }, durable, default))).Code);
        Assert.Equal("LaterCheckpointMirrorStagingInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                local.MaterializeLaterCheckpointAsync(stage with {
                    F061LeaseGeneration = 1, CanPush = true
                }, durable, default))).Code);
        Assert.Equal("LaterCheckpointMirrorStagingInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                local.MaterializeLaterCheckpointAsync(stage, durable with {
                    BundleSha256 = new string('0', 64)
                }, default))).Code);
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git"));

        var linked = Path.Combine(f.Root, "symlinked-mirror-root");
        Directory.CreateSymbolicLink(linked, f.MirrorRoot);
        var unsafeRunner = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, linked, new LocalGitBundleArtifactStore(f.BundleRoot),
            laterCheckpointEnabled: true);
        Assert.Equal("SourceMirrorSymlinkBlocked",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                unsafeRunner.MaterializeLaterCheckpointAsync(
                    stage, durable, default))).Code);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            local.MaterializeLaterCheckpointAsync(stage, durable, cancel.Token));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git"));

        // No ghost mirror on Git IO error: the v2 importer cannot promote
        // files that do not match the immutable F-035v2 receipt.
        var path = Path.Combine(f.BundleRoot, durable.BundleFileName);
        File.Delete(path);
        Assert.Equal("SourceMirrorBundleInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                local.MaterializeLaterCheckpointAsync(stage, durable, default))).Code);
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git"));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F060v2_replay_rejects_corrupt_promoted_objects_without_repair(
        string format)
    {
        using var f = new Fixture();
        var source = f.CreatePlan(format);
        var (stage, durable) = LaterOfflineEvidence(source);
        var local = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, f.MirrorRoot,
            new LocalGitBundleArtifactStore(f.BundleRoot),
            laterCheckpointEnabled: true);
        await local.MaterializeLaterCheckpointAsync(stage, durable, default);

        var mirror = f.Physical(source);
        f.Git("--git-dir=" + mirror, "repack", "-a", "-d");
        f.Git("--git-dir=" + mirror, "prune-packed");
        var pack = Assert.Single(Directory.GetFiles(
            Path.Combine(mirror, "objects", "pack"), "*.pack"));
        // Git writes packed objects read-only (0444). The corruption
        // fixture must explicitly enable writing on POSIX runners;
        // production never modifies an existing promoted pack.
        File.SetUnixFileMode(pack,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllBytesAsync(pack, [1, 2, 3, 4, 5]);

        Assert.Equal("SourceMirrorGitClosureInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                local.MaterializeLaterCheckpointAsync(stage, durable, default))).Code);
        Assert.True(Directory.Exists(mirror));
        Assert.Single(Directory.GetDirectories(f.MirrorRoot, "*.git"));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.tmp"));
    }

    // #123: the OFF-by-default local physical primitive is deliberately
    // NOT registered with a DB or API and is NOT re-admission authority.
    // The production SQL coordinator must authenticate and recheck the
    // #122 reviewed decision before invoking any local rename.
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F061v2_reviewed_orphan_isolation_renames_real_Git_once_without_re_admission(
        string format)
    {
        using var f = new Fixture();
        var physical = f.CreatePlan(format);
        var (stage, durable) = LaterOfflineEvidence(physical);
        var importer = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, f.MirrorRoot,
            new LocalGitBundleArtifactStore(f.BundleRoot),
            laterCheckpointEnabled: true);
        await importer.MaterializeLaterCheckpointAsync(stage, durable, default);
        var observed = await new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
            f.BundleRoot, f.MirrorRoot, enabled: true).AuditAsync(
                stage, durable, default);
        var reviewedAt = DateTimeOffset.UtcNow;
        var inspection =
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Seal(
                stage, durable, Guid.NewGuid(), Guid.NewGuid(), 1,
                "ImportWorkerFailed", reviewedAt.AddMinutes(-1),
                2, 0, new string('d', 64), observed, reviewedAt);
        var actor = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var decision =
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Seal(
                inspection, actor, reviewer,
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview, reviewedAt);
        // #123: the historical #122 human approval remains verifiable
        // after a NEW physical/SQL inspection timestamp. It signs the
        // stable candidate, not the wall-clock time of a later read.
        var rereadInspection =
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Seal(
                stage, durable, inspection.WorkerId, inspection.LeaseToken,
                inspection.LeaseGeneration, inspection.FailureCode,
                inspection.QuarantinedAtUtc, inspection.LeaseEventCount,
                inspection.RenewedEventCount,
                inspection.LeaseEventHistorySha256, observed,
                reviewedAt.AddSeconds(15));
        Assert.NotEqual(inspection.EvidenceSha256,
            rereadInspection.EvidenceSha256);
        Assert.Equal(
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                .ReviewCandidateSha256(inspection),
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity
                .ReviewCandidateSha256(rereadInspection));
        var preparedAt = reviewedAt.AddSeconds(16);
        var prepared =
            GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity.Seal(
                rereadInspection, decision, preparedAt);
        GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity.Verify(
            prepared, rereadInspection, decision);
        Assert.Equal(decision.EvidenceSha256,
            prepared.RecoveryDecisionSha256);
        Assert.Equal(inspection.LeaseEventHistorySha256,
            prepared.LeaseEventHistorySha256);
        Assert.Equal(GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
            .IsolationName(decision), prepared.IsolationDirectoryName);
        Assert.False(prepared.CanReAdmit || prepared.CanMaterialize ||
            prepared.CanPush || prepared.CanRetry ||
            prepared.CanDispatch || prepared.MigrationComplete);
        Assert.Equal("LaterCheckpointMirrorIsolationIntentInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
                    .Verify(prepared with { CanPush = true },
                        rereadInspection, decision)).Code);
        Assert.Equal("LaterCheckpointMirrorRecoveryDecisionInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
                    .Seal(rereadInspection,
                        decision with { EvidenceSha256 = new string('0', 64) },
                        preparedAt)).Code);
        Assert.Equal("LaterCheckpointMirrorIsolationIntentInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity
                    .Seal(rereadInspection, decision,
                        preparedAt.AddMinutes(3))).Code);
        var off = new LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
            f.BundleRoot, f.MirrorRoot);
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationDisabled",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                off.IsolateAsync(stage, durable, inspection, decision,
                    default))).Code);

        var mover = new LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
            f.BundleRoot, f.MirrorRoot, enabled: true);
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedOriginalIntact,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        // A PREPARED original may legitimately have no marker before the
        // OS operation. A marker that exists with foreign bytes is
        // different: it proves neither this SQL intent nor a valid
        // interrupted rename and MUST NOT be reported as intact.
        var originalMarker = Path.Combine(f.Physical(physical),
            "agic-f061v2-reviewed-isolation-intent");
        await File.WriteAllTextAsync(originalMarker,
            "forged-or-partial-original-prepare");
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedOriginalUntrusted,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        var forgedOriginal = await Assert.ThrowsAsync<
            GitSourceSnapshotMirrorException>(() =>
                mover.WithVerifiedIsolationAsync(
                    stage, durable, rereadInspection, decision, prepared,
                    (proof, ct) => Task.FromResult("no SQL write"),
                    default));
        Assert.Equal("LaterCheckpointMirrorIsolationMarkerInvalid",
            forgedOriginal.Code);
        Assert.True(Directory.Exists(f.Physical(physical)));
        Assert.False(Directory.Exists(Path.Combine(f.MirrorRoot,
            prepared.IsolationDirectoryName)));
        File.Delete(originalMarker);
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedOriginalIntact,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        // The physical primitive also accepts a NEW real inspection
        // of the SAME signed candidate, not a time-travel review.
        var receipt = await mover.IsolateAsync(
            stage, durable, rereadInspection, decision, default);
        var original = f.Physical(physical);
        var isolated = Path.Combine(f.MirrorRoot,
            receipt.IsolatedDirectoryName);
        Assert.False(Directory.Exists(original));
        Assert.True(Directory.Exists(isolated));
        // An identical existing quarantine created by an older local
        // primitive (or an outsider) has no pre-rename durable marker.
        // It must NEVER be adopted as a trusted SQL terminal receipt
        // merely because the Git object graph hashes match.
        var unmarked = await Assert.ThrowsAsync<
            GitSourceSnapshotMirrorException>(() =>
            mover.WithVerifiedIsolationAsync(
                stage, durable, rereadInspection, decision, prepared,
                (proof, token) => Task.FromResult("must not run SQL"),
                default));
        Assert.Equal("LaterCheckpointMirrorIsolationMarkerInvalid",
            unmarked.Code);
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedIsolatedUntrusted,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        var reread = await mover.AuditIsolatedAsync(
            stage, durable, rereadInspection, decision, default);
        Assert.Equal(observed.ObjectCount, reread.Count);
        Assert.Equal(observed.ObjectSetSha256, reread.ObjectSetSha256);
        Assert.Equal(stage.PhysicalMirrorId, receipt.PhysicalMirrorId);
        Assert.Equal(observed.ObjectSetSha256, receipt.ObjectSetSha256);
        Assert.Equal(observed.ObjectCount, receipt.ObjectCount);
        Assert.Equal(decision.EvidenceSha256, receipt.RecoveryDecisionSha256);
        Assert.Equal(inspection.LeaseEventHistorySha256,
            receipt.OrderedEventHistorySha256);
        Assert.False(receipt.DurableIsolationRecorded ||
            receipt.CanReAdmit || receipt.CanMaterialize ||
            receipt.CanPush || receipt.CanRetry ||
            receipt.CanDispatch || receipt.MigrationComplete);
        GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity.Verify(
            receipt, inspection, decision);
        // #124: a real quarantined object graph only becomes a durable
        // SQL *audit* after the source-locked writer; its domain SHA
        // must still bind the original worker/token/generation, exact
        // Prepared event, bundle, graph and all false authority flags.
        var checkedAt = preparedAt.AddSeconds(1);
        var recordedAt = checkedAt.AddSeconds(1);
        var physicalProof = new
            GitIncrementalLaterCheckpointMirrorPhysicalIsolationProof(
                stage.PhysicalMirrorId, stage.WorkpackId,
                prepared.IsolationDirectoryName,
                reread.ObjectSetSha256, reread.Count,
                durable.BundleSha256, durable.BundleBytes,
                checkedAt, false);
        var recorded =
            GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity
                .Seal(rereadInspection, decision, prepared, durable,
                    physicalProof, recordedAt);
        GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity
            .Verify(recorded, rereadInspection, decision,
                prepared, durable, physicalProof);
        Assert.Equal(prepared.IntentSha256,
            recorded.PreparedIntentSha256);
        Assert.Equal(prepared.ObjectSetSha256,
            recorded.ObjectSetSha256);
        Assert.True(recorded.DurableIsolationRecorded);
        Assert.False(recorded.CanReAdmit || recorded.CanMaterialize ||
            recorded.CanPush || recorded.CanRetry ||
            recorded.CanDispatch || recorded.MigrationComplete);
        Assert.Equal("LaterCheckpointMirrorRecordedIsolationInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity
                    .Verify(recorded with { CanPush = true },
                        rereadInspection, decision, prepared,
                        durable, physicalProof)).Code);
        Assert.Equal("LaterCheckpointMirrorRecordedIsolationInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity
                    .Seal(rereadInspection, decision, prepared, durable,
                        physicalProof with { ObjectCount =
                            physicalProof.ObjectCount + 1 },
                        recordedAt)).Code);
        Assert.Equal("LaterCheckpointMirrorRecordedIsolationInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorRecordedIsolationIntegrity
                    .Seal(rereadInspection, decision, prepared, durable,
                        physicalProof, preparedAt.AddSeconds(-1))).Code);
        Assert.Single(Directory.GetDirectories(f.MirrorRoot,
            "*.quarantine.git"));
        Assert.Empty(Directory.GetDirectories(f.MirrorRoot, "*.git",
            SearchOption.TopDirectoryOnly).Where(p =>
                Path.GetFileName(p) == stage.PhysicalMirrorId.ToString("N") + ".git"));
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationAlreadyPresent",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                mover.IsolateAsync(stage, durable, inspection, decision,
                    default))).Code);
        Assert.True(Directory.Exists(isolated));
        // A concurrent/stale worker recreating the original UUID.git
        // must invalidate the quarantine read, not be silently ignored.
        Directory.CreateDirectory(original);
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedBothPresent,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationOriginalPresent",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                mover.AuditIsolatedAsync(stage, durable, inspection,
                    decision, default))).Code);
        Directory.Delete(original);
        Assert.Equal(observed.ObjectSetSha256,
            (await mover.AuditIsolatedAsync(
                stage, durable, inspection, decision,
                default)).ObjectSetSha256);

        // If a caller changes any immutable physical/reviewer audit
        // evidence, the offline seal cannot be reused for re-admission.
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                    .Verify(receipt with { CanReAdmit = true },
                        inspection, decision)).Code);
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationInvalid",
            Assert.Throws<GitSourceSnapshotMirrorException>(() =>
                GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                    .Verify(receipt with { RecoveryDecisionSha256 =
                        new string('f', 64) }, inspection, decision)).Code);

        // A later orphan read is a fresh Git fsck, NOT a trust in
        // the previously sealed isolation receipt's object SHA.
        var isolatedRef = Path.Combine(isolated, "refs", "heads", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(isolatedRef)!);
        await File.WriteAllTextAsync(isolatedRef,
            new string('0', format == "sha1" ? 40 : 64) + "\n");
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationClosureInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                mover.AuditIsolatedAsync(stage, durable, inspection,
                    decision, default))).Code);
        Assert.False(Directory.Exists(original));
        Assert.True(Directory.Exists(isolated));
        // An unmarked mirror stays UNTRUSTED even when its graph
        // subsequently becomes corrupt. It must never be labeled a
        // verified completed isolation by the read-only diagnostic.
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedIsolatedUntrusted,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        // A crash with neither path is not a completed move. Neither
        // this read nor an operator can silently recreate UUID.git.
        var detached = Path.Combine(f.Root, "detached-orphan.git");
        Directory.Move(isolated, detached);
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedBothMissing,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        Assert.False(Directory.Exists(original));
        Assert.False(Directory.Exists(isolated));
        // A symlink to even a VALID previous isolated graph is not
        // accepted as the server-owned quarantine directory.
        Directory.CreateSymbolicLink(isolated, detached);
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedUnsafePath,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        Directory.Delete(isolated);
        // Nor may an outsider file be mistaken for an absent UUID.git
        // before an allegedly completed quarantine.
        await File.WriteAllTextAsync(original, "outsider-claim");
        Assert.Equal(
            LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                .PreparedUnsafePath,
            await mover.InspectPreparedPathsAsync(stage, durable,
                rereadInspection, decision, prepared, default));
        File.Delete(original);
    }

    // #124: simulate host loss after the intent marker is persisted,
    // either BEFORE kernel rename or between successful kernel rename
    // and fsync of the PARENT mirror directory. The original marker
    // is already present, but the recovery invocation must make the
    // quarantine name durable *before* handing physical proof to SQL.
    // No trusted coordinator, generation or remote write is faked here:
    // this exercises the exact offline post-rename filesystem branch.
    [Theory]
    [InlineData("sha1", false)]
    [InlineData("sha1", true)]
    [InlineData("sha256", false)]
    [InlineData("sha256", true)]
    public async Task F061v2_quarantine_restart_after_marker_or_rename_preserves_durability(
        string format, bool renameBeforeRestart)
    {
        using var f = new Fixture();
        var physical = f.CreatePlan(format);
        var (stage, durable) = LaterOfflineEvidence(physical);
        var importer = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, f.MirrorRoot,
            new LocalGitBundleArtifactStore(f.BundleRoot),
            laterCheckpointEnabled: true);
        await importer.MaterializeLaterCheckpointAsync(stage, durable, default);
        var observed = await new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
            f.BundleRoot, f.MirrorRoot, enabled: true)
            .AuditAsync(stage, durable, default);
        var reviewedAt = DateTimeOffset.UtcNow;
        var inspected =
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Seal(
                stage, durable, Guid.NewGuid(), Guid.NewGuid(), 1,
                "TestHostInterruptedFsync", reviewedAt.AddMinutes(-1),
                2, 0, new string('e', 64), observed, reviewedAt);
        var decision =
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Seal(
                inspected, Guid.NewGuid(), Guid.NewGuid(),
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview, reviewedAt);
        var current =
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Seal(
                stage, durable, inspected.WorkerId, inspected.LeaseToken,
                inspected.LeaseGeneration, inspected.FailureCode,
                inspected.QuarantinedAtUtc, inspected.LeaseEventCount,
                inspected.RenewedEventCount,
                inspected.LeaseEventHistorySha256, observed,
                reviewedAt.AddSeconds(10));
        var prepared =
            GitIncrementalLaterCheckpointMirrorIsolationIntentIntegrity.Seal(
                current, decision, reviewedAt.AddSeconds(11));
        var original = f.Physical(physical);
        var isolated = Path.Combine(f.MirrorRoot,
            prepared.IsolationDirectoryName);
        var marker = Path.Combine(original,
            "agic-f061v2-reviewed-isolation-intent");
        // The fixture simulates an exact already-persisted marker.
        // Newlines must be real LF bytes, not literal backslash+n:
        // the server rejects even graph-identical incorrectly marked
        // directories. If requested, simulate kernel rename without
        // calling fsync(parent), then restart the trusted physical path.
        var contents = System.Text.Encoding.ASCII.GetBytes(
            "f061v2-physical-source-isolation-intent-v1\n" +
            prepared.IntentSha256 + "\n" +
            decision.EvidenceSha256 + "\n");
        await using (var file = new FileStream(marker,
            FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            await file.WriteAsync(contents);
            file.Flush(flushToDisk: true);
        }
        if (renameBeforeRestart)
            Directory.Move(original, isolated);
        Assert.Equal(!renameBeforeRestart, Directory.Exists(original));
        Assert.Equal(renameBeforeRestart, Directory.Exists(isolated));

        var session = new LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
            f.BundleRoot, f.MirrorRoot, enabled: true);
        Assert.Equal(
            renameBeforeRestart
                ? LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                    .PreparedIsolatedIntact
                : LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator
                    .PreparedOriginalIntact,
            await session.InspectPreparedPathsAsync(
                stage, durable, current, decision, prepared, default));
        var callbackInvocations = 0;
        var proof = await session.WithVerifiedIsolationAsync(
            stage, durable, current, decision, prepared,
            (physicalProof, ct) =>
            {
                callbackInvocations++;
                return Task.FromResult(physicalProof);
            }, default);
        Assert.Equal(1, callbackInvocations);
        Assert.Equal(renameBeforeRestart, proof.RecoveredExistingQuarantine);
        Assert.Equal(prepared.IsolationDirectoryName,
            proof.IsolationDirectoryName);
        Assert.Equal(prepared.ObjectSetSha256, proof.ObjectSetSha256);
        Assert.Equal(prepared.ObjectCount, proof.ObjectCount);
        Assert.Equal(durable.BundleSha256, proof.BundleSha256);
        Assert.False(Directory.Exists(original));
        Assert.True(Directory.Exists(isolated));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F061v2_orphan_isolation_refuses_collisions_corrupt_Git_and_symlink_roots(
        string format)
    {
        using var f = new Fixture();
        var source = f.CreatePlan(format);
        var (stage, durable) = LaterOfflineEvidence(source);
        var materializer = new LocalGitSourceSnapshotMirrorMaterializer(
            f.BundleRoot, f.MirrorRoot,
            new LocalGitBundleArtifactStore(f.BundleRoot),
            laterCheckpointEnabled: true);
        await materializer.MaterializeLaterCheckpointAsync(stage, durable,
            default);
        var measured = await new LocalGitIncrementalLaterCheckpointMirrorOfflineAuditor(
            f.BundleRoot, f.MirrorRoot, enabled: true)
            .AuditAsync(stage, durable, default);
        var at = DateTimeOffset.UtcNow;
        var inspection =
            GitIncrementalLaterCheckpointMirrorRecoveryInspectionIntegrity.Seal(
                stage, durable, Guid.NewGuid(), Guid.NewGuid(), 1,
                "ImportWorkerFailed", at.AddMinutes(-1),
                2, 0, new string('a', 64), measured, at);
        var actor = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var decision =
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Seal(
                inspection, actor, reviewer,
                GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity
                    .ApprovedForIsolationReview, at);
        var mover = new LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
            f.BundleRoot, f.MirrorRoot, enabled: true);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                mover.IsolateAsync(stage, durable, inspection, decision,
                    cancelled.Token));
        }
        var original = f.Physical(source);
        Assert.True(Directory.Exists(original));

        var reject = GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Seal(
            inspection, actor, reviewer,
            GitIncrementalLaterCheckpointMirrorRecoveryDecisionIntegrity.Rejected,
            at);
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationScopeInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                mover.IsolateAsync(stage, durable, inspection, reject,
                    default))).Code);

        var destination = Path.Combine(f.MirrorRoot,
            GitIncrementalLaterCheckpointMirrorOrphanIsolationIntegrity
                .IsolationName(decision));
        Directory.CreateDirectory(destination);
        var operatorFile = Path.Combine(destination, "operator-evidence.txt");
        await File.WriteAllTextAsync(operatorFile, "must survive collision");
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationAlreadyPresent",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                mover.IsolateAsync(stage, durable, inspection, decision,
                    default))).Code);
        Assert.True(Directory.Exists(original));
        Assert.Equal("must survive collision",
            await File.ReadAllTextAsync(operatorFile));
        Directory.Delete(destination, recursive: true);

        // Even a non-directory outsider entry with the same quarantine
        // name is never removed, replaced or reinterpreted as recovery.
        await File.WriteAllTextAsync(destination, "operator-owned file");
        Assert.Equal("LaterCheckpointMirrorOrphanIsolationAlreadyPresent",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                mover.IsolateAsync(stage, durable, inspection, decision,
                    default))).Code);
        Assert.True(Directory.Exists(original));
        Assert.Equal("operator-owned file",
            await File.ReadAllTextAsync(destination));
        File.Delete(destination);

        var symlink = Path.Combine(f.Root, "unsafe-quarantine-root");
        Directory.CreateSymbolicLink(symlink, f.MirrorRoot);
        var unsafeMover =
            new LocalGitIncrementalLaterCheckpointMirrorOrphanIsolator(
                f.BundleRoot, symlink, enabled: true);
        Assert.Equal("SourceMirrorSymlinkBlocked",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                unsafeMover.IsolateAsync(stage, durable, inspection, decision,
                    default))).Code);
        Assert.True(Directory.Exists(original));

        // Corrupt a real Git OID after the earlier physical review.
        // The isolator MUST run fresh fsck under the POSIX lock, and
        // must leave the original UUID.git in place on mismatch.
        var refFile = Path.Combine(original, "refs", "heads", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(refFile)!);
        await File.WriteAllTextAsync(refFile,
            new string('0', format == "sha1" ? 40 : 64) + "\n");
        Assert.Equal("LaterCheckpointMirrorOfflineClosureInvalid",
            (await Assert.ThrowsAsync<GitSourceSnapshotMirrorException>(() =>
                mover.IsolateAsync(stage, durable, inspection, decision,
                    default))).Code);
        Assert.True(Directory.Exists(original));
        Assert.False(Directory.Exists(destination));
    }

    // Real Git objects are taken from the fixture, while the historical
    // F-091/F-097b/F-038v2/F-035v2 domain test input is deliberately
    // synthetic. It is NOT a substitute for the PostgreSQL source lock.
    private static (
        GitIncrementalLaterCheckpointMirrorStagingPlan Stage,
        GitIncrementalLaterCheckpointBundleReceipt Durable) LaterOfflineEvidence(
            GitHubTrustedRemoteReadPlan physical)
    {
        var file = physical.SourceBundle!;
        var now = DateTimeOffset.UtcNow;
        var prepId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sourceConnection = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        var checkpointId = Guid.NewGuid();
        var observationId = Guid.NewGuid();
        var sourceRefs = physical.SourceRefs;
        var objectFormat = sourceRefs[0].ObjectId.Length == 40 ?
            "sha1" : "sha256";
        var plan = new IncrementalOperationPlan(
            planId, prepId, baselineId, sourceConnection, Guid.NewGuid(),
            "gh-org", new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64), now, Guid.NewGuid(),
            1, 6, 1, 0, false, false, [],
            [new IncrementalOperationStep(1, null, "src-repo",
                "test-sandbox", "ReviewTargetName", "Blocked",
                "Blocked", 0)]);
        var source = new GitHubSourceCheckpointGitRefProof(
            1, checkpointId, prepId, planId, sourceConnection,
            physical.SourceSnapshotId, baselineId, "src-repo",
            new string('c', 64), new string('e', 64),
            new string('f', 64), physical.SourceEvidenceSha256,
            Guid.NewGuid(), new string('b', 64), sourceRefs,
            new string('d', 64), false, false, false, false);
        var selected = new GitIncrementalCheckpointAuditInputs(
            2, prepId, planId, sourceConnection,
            physical.SourceSnapshotId, checkpointId,
            source.CheckpointRefAttestationSha256, source.EvidenceSha256,
            observationId, new string('a', 64), "src-repo",
            "test-sandbox", objectFormat, sourceRefs,
            [sourceRefs[0]], false, false, false, false);
        var compared = GitMirrorRefReconciler.Compare(new(
            plan.Id, plan.ManifestSha256, source.SourceEvidenceSha256,
            selected.TargetEvidenceSha256, sourceRefs,
            selected.ExpectedTargetRefs));
        var workpack = GitIncrementalLaterCheckpointWorkpackIntegrity.Seal(
            plan, selected, source, compared, Guid.NewGuid(),
            file.WorkpackId, Guid.NewGuid());
        var bundlePlan = GitIncrementalLaterCheckpointBundleIntegrity.Prepare(
            plan, selected, source, compared, workpack);
        var checkedFile = new GitIncrementalLaterCheckpointLocalBundleResult(
            workpack.WorkpackId, checkpointId,
            physical.SourceSnapshotId, bundlePlan.EvidenceSha256,
            source.EvidenceSha256, selected.TargetEvidenceSha256,
            file.BundleFileName, file.BundleSha256, file.BundleBytes,
            sourceRefs.Count, now.AddSeconds(-2), now.AddSeconds(-1),
            false, false, false, false, false);
        var receipt = GitIncrementalLaterCheckpointBundleReceiptIntegrity.Seal(
            bundlePlan, checkedFile, workpack.RecordedBy, now);
        var stage = GitIncrementalLaterCheckpointMirrorStagingIntegrity.Prepare(
            plan, selected, source, bundlePlan, receipt, workpack);
        return (stage, receipt);
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "f060-source-" + Guid.NewGuid().ToString("N"));
        public string BundleRoot => Path.Combine(Root, "bundles");
        public string MirrorRoot => Path.Combine(Root, "physical");
        public string SourceBare => Path.Combine(Root, "original.git");
        private Guid SnapshotId { get; } = Guid.NewGuid();

        public Fixture()
        {
            Directory.CreateDirectory(BundleRoot);
            Directory.CreateDirectory(MirrorRoot);
        }

        public LocalGitSourceSnapshotMirrorMaterializer Materializer() =>
            new(BundleRoot, MirrorRoot,
                new LocalGitBundleArtifactStore(BundleRoot),
                enabled: true);

        public string Physical(GitHubTrustedRemoteReadPlan plan) =>
            Path.Combine(MirrorRoot, plan.SourceMirrorId.ToString("N") + ".git");

        public GitHubTrustedRemoteReadPlan CreatePlan(string format = "sha1")
        {
            var work = Path.Combine(Root, "work");
            Git("init", "-q", "--object-format=" + format, work);
            Git("-C", work, "config", "user.name", "F060 Test");
            Git("-C", work, "config", "user.email", "f060@example.invalid");
            File.WriteAllText(Path.Combine(work, "readme.txt"), "original\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "first");
            Git("-C", work, "branch", "-M", "main");
            Git("-C", work, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(work, "feature.txt"), "feature\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "feature");
            Git("-C", work, "checkout", "-q", "main");
            File.WriteAllText(Path.Combine(work, "readme.txt"), "revised\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "main revised");
            Git("-C", work, "merge", "--no-ff", "-m", "merge feature", "feature");
            Git("-C", work, "tag", "-a", "v1", "-m", "release");

            Git("clone", "--mirror", "-q", work, SourceBare);
            var workpack = Guid.NewGuid();
            var bundlePath = Path.Combine(
                BundleRoot, workpack.ToString("N") + ".bundle");
            Git("--git-dir=" + SourceBare, "bundle", "create",
                bundlePath, "--all");
            var bytes = new FileInfo(bundlePath).Length;
            using var stream = File.OpenRead(bundlePath);
            var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            var refs = Refs(SourceBare);
            var sourceSha = new string('a', 64);
            var bundle = new GitSourceBundleView(
                workpack, Guid.NewGuid(), Guid.NewGuid(), "src-repo",
                sourceSha, new string('b', 64),
                workpack.ToString("N") + ".bundle", sha,
                bytes, refs.Count, DateTimeOffset.UtcNow, true, false);
            return new GitHubTrustedRemoteReadPlan(
                SnapshotId, "gh-org", "test-sandbox", sourceSha,
                refs, new string('c', 64), new string('d', 64),
                new string('e', 64), "")
            {
                SourceMirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                    SnapshotId, workpack),
                SourceBundle = bundle
            };
        }

        public GitHubTrustedRemoteReadPlan CopyForAnotherWorkpack(
            GitHubTrustedRemoteReadPlan first)
        {
            var newWorkpack = Guid.NewGuid();
            var file = newWorkpack.ToString("N") + ".bundle";
            File.Copy(Path.Combine(BundleRoot,
                first.SourceBundle!.BundleFileName),
                Path.Combine(BundleRoot, file));
            return first with
            {
                SourceMirrorId = GitSourceSnapshotMirrorIntegrity.PhysicalMirrorId(
                    first.SourceSnapshotId, newWorkpack),
                SourceBundle = first.SourceBundle with {
                    WorkpackId = newWorkpack, BundleFileName = file
                }
            };
        }

        public IReadOnlyList<GitMirrorRef> Refs(string bare) =>
            GitMirrorRefReconciler.CanonicalizeObservedRefs(
                Git("--git-dir=" + bare, "for-each-ref",
                    "--format=%(refname)%09%(objectname)")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r').Split('\t'))
                    .Select(fields => new GitMirrorRef(fields[0], fields[1]))
                    .ToArray());

        public IReadOnlyList<GitHubObservedRef> TypedRefs(string bare) =>
            Git("--git-dir=" + bare, "for-each-ref",
                "--format=%(refname)%09%(objectname)%09%(objecttype)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t'))
                .Select(fields => new GitHubObservedRef(
                    fields[0], fields[1], fields[2])).ToArray();

        public string Git(params string[] args)
        {
            using var process = new Process();
            process.StartInfo.FileName = "/usr/bin/git";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                throw new InvalidOperationException("Git test fixture failed");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20_000) || process.ExitCode != 0)
                throw new InvalidOperationException("Git fixture: " + error);
            return output;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
