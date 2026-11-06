using System.Diagnostics;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real Linux Git SHA-1/SHA-256 repositories, no GitHub network or sender.
public sealed class LocalGitObjectClosureAuditorTests
{
    [Fact]
    public async Task Real_merge_history_tree_blobs_and_annotated_tag_match_with_target_only_refs()
    {
        using var git = new Fixture();
        var request = git.Setup();
        var originalSource = git.Refs(git.Source);
        var originalTarget = git.Refs(git.Target);
        var receipt = await git.Runner().AuditAsync(request, default);

        Assert.Equal("sha1", receipt.GitObjectFormat);
        Assert.Equal(request.SourceSnapshotId, receipt.SourceSnapshotId);
        Assert.Equal(request.TargetSandboxId, receipt.TargetSandboxId);
        Assert.True(receipt.ReachableObjectCount >= 9);
        Assert.Equal(receipt.SourceObjectSetSha256, receipt.TargetObjectSetSha256);
        Assert.Equal(64, receipt.EvidenceSha256.Length);
        Assert.False(receipt.TransportInvoked);
        Assert.False(receipt.RemoteWritesAuthorized);
        Assert.False(receipt.RemoteMigrationVerified);
        Assert.Equal(originalSource, git.Refs(git.Source));
        Assert.Equal(originalTarget, git.Refs(git.Target));

        // Repeated independent verification of unchanged objects has the
        // same digest, but never promises remote GitHub migration success.
        var replay = await git.Runner().AuditAsync(request, default);
        Assert.Equal(receipt.EvidenceSha256, replay.EvidenceSha256);
        Assert.Equal(receipt.ReachableObjectCount, replay.ReachableObjectCount);
    }

    [Fact]
    public async Task Sha256_git_object_format_is_verified_without_sha1_assumptions()
    {
        using var git = new Fixture();
        var request = git.Setup("sha256");
        var receipt = await git.Runner().AuditAsync(request, default);
        Assert.Equal("sha256", receipt.GitObjectFormat);
        Assert.True(receipt.ReachableObjectCount >= 9);
        Assert.False(receipt.RemoteMigrationVerified);
    }

    [Fact]
    public async Task Missing_packed_blob_or_tree_must_fail_full_fsck()
    {
        using var git = new Fixture();
        var request = git.Setup();
        git.Git("--git-dir=" + git.Target, "repack", "-a", "-d");
        git.Git("--git-dir=" + git.Target, "prune-packed");
        var pack = Assert.Single(Directory.GetFiles(
            Path.Combine(git.Target, "objects", "pack"), "*.pack"));
        // Destructive byte corruption only in this private target fixture.
        File.WriteAllBytes(pack, [1, 2, 3, 4, 5, 6]);
        var ex = await Assert.ThrowsAsync<GitObjectClosureAuditException>(
            () => git.Runner().AuditAsync(request, default));
        Assert.Equal("GitClosureGitCommandRejected", ex.Code);
    }

    [Fact]
    public async Task Changed_target_ref_is_refused_even_if_objects_still_exist()
    {
        using var git = new Fixture();
        var request = git.Setup();
        var feature = request.ExpectedSourceRefs.Single(x =>
            x.Name == "refs/heads/feature").ObjectId;
        git.Git("--git-dir=" + git.Target, "update-ref",
            "refs/heads/main", feature);
        var ex = await Assert.ThrowsAsync<GitObjectClosureAuditException>(
            () => git.Runner().AuditAsync(request, default));
        Assert.Equal("GitClosureRefsChanged", ex.Code);
    }

    [Fact]
    public async Task Shallow_alternates_promisor_and_symlinks_fail_before_any_receipt()
    {
        using (var git = new Fixture())
        {
            var request = git.Setup();
            File.WriteAllText(Path.Combine(git.Target, "shallow"),
                request.ExpectedSourceRefs[0].ObjectId + "\n");
            Assert.Equal("GitClosureNotSelfContained",
                (await Assert.ThrowsAsync<GitObjectClosureAuditException>(
                    () => git.Runner().AuditAsync(request, default))).Code);
        }
        using (var git = new Fixture())
        {
            var request = git.Setup();
            File.WriteAllText(Path.Combine(git.Target,
                "objects", "info", "alternates"), git.Source + "/objects\n");
            Assert.Equal("GitClosureNotSelfContained",
                (await Assert.ThrowsAsync<GitObjectClosureAuditException>(
                    () => git.Runner().AuditAsync(request, default))).Code);
        }
        using (var git = new Fixture())
        {
            var request = git.Setup();
            git.Git("--git-dir=" + git.Target, "repack", "-a", "-d");
            var pack = Assert.Single(Directory.GetFiles(
                Path.Combine(git.Target, "objects", "pack"), "*.pack"));
            File.WriteAllText(Path.ChangeExtension(pack, ".promisor"), "");
            Assert.Equal("GitClosureNotSelfContained",
                (await Assert.ThrowsAsync<GitObjectClosureAuditException>(
                    () => git.Runner().AuditAsync(request, default))).Code);
        }
        using (var git = new Fixture())
        {
            var request = git.Setup();
            var outside = Path.Combine(git.Root, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(
                Path.Combine(git.Target, "refs", "external"), outside);
            Assert.Equal("GitClosureSymlinkBlocked",
                (await Assert.ThrowsAsync<GitObjectClosureAuditException>(
                    () => git.Runner().AuditAsync(request, default))).Code);
        }
    }

    [Fact]
    public async Task No_github_transport_and_disabled_path_refusals()
    {
        using var git = new Fixture();
        var request = git.Setup();
        var disabled = new LocalGitObjectClosureAuditor(
            git.SourceRoot, git.TargetRoot);
        Assert.Equal("GitClosureDisabled",
            (await Assert.ThrowsAsync<GitObjectClosureAuditException>(
                () => disabled.AuditAsync(request, default))).Code);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            git.Runner().AuditAsync(request, cancelled.Token));

        var missing = request with { TargetSandboxId = Guid.NewGuid() };
        Assert.Equal("GitClosureRepositoryInvalid",
            (await Assert.ThrowsAsync<GitObjectClosureAuditException>(
                () => git.Runner().AuditAsync(missing, default))).Code);

        var linked = Path.Combine(git.Root, "source-link");
        Directory.CreateSymbolicLink(linked, git.SourceRoot);
        Assert.Equal("GitClosureSymlinkBlocked",
            (await Assert.ThrowsAsync<GitObjectClosureAuditException>(
                () => new LocalGitObjectClosureAuditor(linked,
                    git.TargetRoot, enabled: true)
                    .AuditAsync(request, default))).Code);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "f056-closure-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "source");
        public string TargetRoot => Path.Combine(Root, "target");
        public Guid SourceSnapshotId { get; } = Guid.NewGuid();
        public Guid TargetSandboxId { get; } = Guid.NewGuid();
        public string Source => Path.Combine(SourceRoot,
            SourceSnapshotId.ToString("N") + ".git");
        public string Target => Path.Combine(TargetRoot,
            TargetSandboxId.ToString("N") + ".git");

        public Fixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(TargetRoot);
        }

        public LocalGitObjectClosureAuditor Runner() =>
            new(SourceRoot, TargetRoot, enabled: true);

        public GitObjectClosureAuditRequest Setup(string format = "sha1")
        {
            var work = Path.Combine(Root, "work");
            Git("init", "-q", "--object-format=" + format, work);
            Git("-C", work, "config", "user.name", "F056 Fixture");
            Git("-C", work, "config", "user.email", "f056@example.invalid");
            File.WriteAllText(Path.Combine(work, "readme.txt"), "original\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "first");
            Git("-C", work, "branch", "-M", "main");
            Git("-C", work, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(work, "feature.txt"), "branch\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "second");
            Git("-C", work, "checkout", "-q", "main");
            File.WriteAllText(Path.Combine(work, "readme.txt"), "updated\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "main change");
            Git("-C", work, "merge", "--no-ff", "-m", "merge feature", "feature");
            Git("-C", work, "tag", "-a", "v1", "-m", "annotated release");

            Git("clone", "--mirror", "--no-hardlinks", "-q", work, Source);
            Git("clone", "--mirror", "--no-hardlinks", "-q", Source, Target);

            // Extra TARGET-only reference must not count as source closure.
            var main = Git("--git-dir=" + Target,
                "rev-parse", "refs/heads/main").Trim();
            Git("--git-dir=" + Target, "update-ref",
                "refs/heads/target-only", main);
            return new(SourceSnapshotId, TargetSandboxId,
                Refs(Source), Refs(Target));
        }

        public IReadOnlyList<GitMirrorRef> Refs(string repository) =>
            GitMirrorRefReconciler.CanonicalizeObservedRefs(
                Git("--git-dir=" + repository, "for-each-ref",
                    "--format=%(refname)%09%(objectname)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t'))
                .Select(fields => new GitMirrorRef(fields[0], fields[1]))
                .ToArray());

        public string Git(params string[] args)
        {
            using var p = new Process();
            p.StartInfo.FileName = "/usr/bin/git";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
            if (!p.Start()) throw new InvalidOperationException("Git unavailable");
            var result = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20_000) || p.ExitCode != 0)
                throw new InvalidOperationException(
                    "Fixture git failed: " + stderr);
            return result;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
