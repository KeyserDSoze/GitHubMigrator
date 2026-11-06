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
            receipt.SourceObjectSetSha256 is { Length: 64 }
                ? proof.ObjectSetSha256 : "");
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

    private sealed class Fixture : IDisposable
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
