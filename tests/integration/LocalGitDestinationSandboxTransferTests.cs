using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// True local Git object transfer, NOT a GitHub remote migration.
// F-071's internal transport has no API route, URL, token or push verb.
public sealed class LocalGitDestinationSandboxTransferTests
{
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Full_history_moves_to_an_isolated_bare_target_with_F056_closure(
        string objectFormat)
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan(objectFormat);
        var sourceProof = await f.Materializer().MaterializeAsync(plan, default);
        var targetRoot = Path.Combine(f.Root, "test-destination");
        Directory.CreateDirectory(targetRoot);
        var sandboxId = Guid.NewGuid();
        var runner = new LocalGitDestinationSandboxTransfer(
            f.MirrorRoot, targetRoot, enabled: true);

        var first = await runner.TransferAsync(
            plan, sourceProof, sandboxId, default);
        Assert.Equal(plan.SourceMirrorId, first.SourceMirrorId);
        Assert.Equal(sandboxId, first.TargetSandboxId);
        Assert.Equal(objectFormat, first.GitObjectFormat);
        Assert.Equal(sourceProof.ObjectSetSha256, first.SourceObjectSetSha256);
        Assert.Equal(sourceProof.ObjectSetSha256, first.TargetObjectSetSha256);
        Assert.Equal(sourceProof.VerifiedObjectCount, first.VerifiedObjectCount);
        Assert.Equal(64, first.EvidenceSha256.Length);
        Assert.Equal(64, first.AuditEvidenceSha256.Length);
        Assert.True(first.SandboxWritesPerformed);
        Assert.False(first.RemotePushInvoked);
        Assert.False(first.RemoteWritesAuthorized);
        Assert.False(first.MigrationComplete);

        var destination = Path.Combine(
            targetRoot, sandboxId.ToString("N") + ".git");
        Assert.Equal(plan.SourceRefs, f.Refs(destination));
        Assert.Contains(plan.SourceRefs, x =>
            x.Name == "refs/heads/main");
        Assert.Contains(plan.SourceRefs, x =>
            x.Name == "refs/heads/feature");
        Assert.Contains(plan.SourceRefs, x => x.Name == "refs/tags/v1");
        Assert.Equal(objectFormat, f.Git("--git-dir=" + destination,
            "rev-parse", "--show-object-format").Trim());

        // Independently re-audit full reachable Git object graphs.
        var audit = await new LocalGitObjectClosureAuditor(
            f.MirrorRoot, targetRoot, enabled: true)
            .AuditAsync(new(plan.SourceMirrorId, sandboxId,
                plan.SourceRefs, plan.SourceRefs), default);
        Assert.Equal(audit.EvidenceSha256, first.AuditEvidenceSha256);
        Assert.Equal(audit.SourceObjectSetSha256,
            audit.TargetObjectSetSha256);
        Assert.Equal(first.TargetObjectSetSha256,
            audit.TargetObjectSetSha256);
        Assert.False(audit.RemoteMigrationVerified);

        // Identical proof can be replayed after a process restart:
        // existing target is re-fscked and NEVER fetched into again.
        var replay = await new LocalGitDestinationSandboxTransfer(
            f.MirrorRoot, targetRoot, enabled: true)
            .TransferAsync(plan, sourceProof, sandboxId, default);
        Assert.Equal(first.EvidenceSha256, replay.EvidenceSha256);
        Assert.Equal(first.AuditEvidenceSha256, replay.AuditEvidenceSha256);
        Assert.Equal(plan.SourceRefs, f.Refs(destination));
        Assert.Single(Directory.GetDirectories(targetRoot, "*.git"));
    }

    [Fact]
    public async Task Parallel_sandbox_rehearsals_converge_on_one_verified_bare()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        var proof = await f.Materializer().MaterializeAsync(plan, default);
        var targetRoot = Path.Combine(f.Root, "parallel-targets");
        Directory.CreateDirectory(targetRoot);
        var sandboxId = Guid.NewGuid();

        var completed = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => new LocalGitDestinationSandboxTransfer(
                f.MirrorRoot, targetRoot, enabled: true)
                .TransferAsync(plan, proof, sandboxId, default)));

        Assert.All(completed, x => {
            Assert.Equal(completed[0].EvidenceSha256, x.EvidenceSha256);
            Assert.Equal(completed[0].TargetObjectSetSha256,
                x.TargetObjectSetSha256);
            Assert.False(x.RemotePushInvoked);
            Assert.False(x.MigrationComplete);
        });
        Assert.Single(Directory.GetDirectories(targetRoot, "*.git"));
        Assert.Equal(plan.SourceRefs, f.Refs(Path.Combine(
            targetRoot, sandboxId.ToString("N") + ".git")));
    }

    [Fact]
    public async Task Corrupt_existing_target_is_never_overwritten_or_repaired()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        var proof = await f.Materializer().MaterializeAsync(plan, default);
        var root = Path.Combine(f.Root, "tampered-target");
        Directory.CreateDirectory(root);
        var sandboxId = Guid.NewGuid();
        var runner = new LocalGitDestinationSandboxTransfer(
            f.MirrorRoot, root, enabled: true);
        await runner.TransferAsync(plan, proof, sandboxId, default);
        var destination = Path.Combine(root, sandboxId.ToString("N") + ".git");
        var feature = Assert.Single(plan.SourceRefs.Where(
            x => x.Name == "refs/heads/feature"));
        f.Git("--git-dir=" + destination, "update-ref",
            "refs/heads/main", feature.ObjectId);
        var tampered = f.Refs(destination);
        Assert.NotEqual(plan.SourceRefs, tampered);

        var error = await Assert.ThrowsAsync<LocalGitSandboxTransferException>(
            () => runner.TransferAsync(plan, proof, sandboxId, default));
        Assert.Equal("SandboxTransferClosureInvalid", error.Code);
        Assert.Equal(tampered, f.Refs(destination));
        Assert.Single(Directory.GetDirectories(root, "*.git"));
    }

    [Fact]
    public async Task Wrong_source_receipt_and_mutated_physical_source_fail_closed()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        var proof = await f.Materializer().MaterializeAsync(plan, default);
        var root = Path.Combine(f.Root, "refused-target");
        Directory.CreateDirectory(root);
        var runner = new LocalGitDestinationSandboxTransfer(
            f.MirrorRoot, root, enabled: true);
        var sandboxId = Guid.NewGuid();

        var forged = proof with {
            ObjectSetSha256 = new string('a', 64)
        };
        Assert.Equal("SandboxTransferSourceProofInvalid",
            (await Assert.ThrowsAsync<LocalGitSandboxTransferException>(
                () => runner.TransferAsync(
                    plan, forged, sandboxId, default))).Code);
        var otherWorkpack = f.CopyForAnotherWorkpack(plan);
        Assert.Equal("SandboxTransferSourceProofInvalid",
            (await Assert.ThrowsAsync<LocalGitSandboxTransferException>(
                () => runner.TransferAsync(
                    otherWorkpack, proof, sandboxId, default))).Code);

        // Valid cryptographic receipt but source refs physically changed:
        // the source is re-audited immediately before any target writes.
        var feature = Assert.Single(plan.SourceRefs.Where(
            x => x.Name == "refs/heads/feature"));
        f.Git("--git-dir=" + f.Physical(plan),
            "update-ref", "refs/heads/main", feature.ObjectId);
        Assert.Equal("SandboxTransferClosureInvalid",
            (await Assert.ThrowsAsync<LocalGitSandboxTransferException>(
                () => runner.TransferAsync(
                    plan, proof, sandboxId, default))).Code);
        Assert.Empty(Directory.GetDirectories(root, "*.git"));
    }

    [Fact]
    public async Task Disabled_and_symlinked_roots_are_rejected_without_destination()
    {
        using var f = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var plan = f.CreatePlan();
        var proof = await f.Materializer().MaterializeAsync(plan, default);
        var root = Path.Combine(f.Root, "safe-target");
        Directory.CreateDirectory(root);
        Assert.Equal("SandboxTransferDisabled",
            (await Assert.ThrowsAsync<LocalGitSandboxTransferException>(
                () => new LocalGitDestinationSandboxTransfer(
                    f.MirrorRoot, root)
                    .TransferAsync(plan, proof, Guid.NewGuid(), default))).Code);
        Assert.Empty(Directory.GetFileSystemEntries(root));

        var symlink = Path.Combine(f.Root, "linked-target");
        Directory.CreateSymbolicLink(symlink, root);
        Assert.Equal("SandboxTransferSymlinkBlocked",
            (await Assert.ThrowsAsync<LocalGitSandboxTransferException>(
                () => new LocalGitDestinationSandboxTransfer(
                    f.MirrorRoot, symlink, enabled: true)
                    .TransferAsync(plan, proof, Guid.NewGuid(), default))).Code);
        Assert.Empty(Directory.GetDirectories(root, "*.git"));
    }

    [Fact]
    public async Task A_new_source_mirror_cannot_force_replace_an_existing_sandbox()
    {
        using var original = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        using var changed = new LocalGitSourceSnapshotMirrorMaterializerTests.Fixture();
        var a = original.CreatePlan();
        var b = changed.CreatePlan();
        var proofA = await original.Materializer().MaterializeAsync(a, default);
        var proofB = await changed.Materializer().MaterializeAsync(b, default);
        var root = Path.Combine(original.Root, "never-force");
        Directory.CreateDirectory(root);
        var sandboxId = Guid.NewGuid();
        await new LocalGitDestinationSandboxTransfer(
            original.MirrorRoot, root, enabled: true)
            .TransferAsync(a, proofA, sandboxId, default);
        var path = Path.Combine(root, sandboxId.ToString("N") + ".git");
        var originalRefs = original.Refs(path);

        var error = await Assert.ThrowsAsync<LocalGitSandboxTransferException>(
            () => new LocalGitDestinationSandboxTransfer(
                changed.MirrorRoot, root, enabled: true)
                .TransferAsync(b, proofB, sandboxId, default));
        Assert.Equal("SandboxTransferClosureInvalid", error.Code);
        Assert.Equal(originalRefs, original.Refs(path));
        Assert.Single(Directory.GetDirectories(root, "*.git"));
    }
}
