using System.Diagnostics;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The transport seam is INTERNAL. Real Git ls-remote/fetch runs against a
// private local test bare instead of reaching GitHub in CI.
public sealed class GitHubReadOnlyObjectMirrorRunnerTests
{
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task Real_readonly_fetch_verifies_all_commits_trees_blobs_and_annotated_tags(
        string format)
    {
        using var fixture = new Fixture();
        var request = fixture.Setup(format);
        var beforeSource = fixture.SourceRefs();
        var beforeRemote = fixture.RemoteRefs();
        var proof = await fixture.Runner().VerifyAsync(
            request, "test-only-ephemeral-token", default);

        Assert.Equal(format, proof.GitObjectFormat);
        Assert.Equal(request.SourceSnapshotId, proof.SourceSnapshotId);
        Assert.True(proof.VerifiedReachableObjects >= 12);
        Assert.Equal(beforeRemote.Count, proof.ObservedRemoteRefs);
        Assert.Equal(proof.SourceObjectSetSha256,
            proof.RemoteObjectSetSha256);
        Assert.Equal(
            GitHubRemoteRefFenceIntegrity.Fingerprint(
                request.ExpectedRemoteRefs.Refs),
            proof.RemoteRefFingerprintSha256);
        Assert.True(proof.GitHubFetchPerformed);
        Assert.False(proof.RemotePushInvoked);
        Assert.False(proof.RemoteWritesAuthorized);
        Assert.False(proof.MigrationComplete);
        Assert.Equal(64, proof.EvidenceSha256.Length);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
        Assert.Equal(beforeSource, fixture.SourceRefs());
        Assert.Equal(beforeRemote, fixture.RemoteRefs());

        // New independent temporary mirror yields the same object-set proof.
        var second = await fixture.Runner().VerifyAsync(
            request, "another-test-ephemeral-token", default);
        Assert.Equal(proof.RemoteObjectSetSha256,
            second.RemoteObjectSetSha256);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Changed_remote_ref_since_sealed_observation_is_rejected_without_receipt()
    {
        using var fixture = new Fixture();
        var request = fixture.Setup();
        var featureOid = request.ExpectedSourceRefs.Single(
            x => x.Name == "refs/heads/feature").ObjectId;
        fixture.Git("--git-dir=" + fixture.Remote, "update-ref",
            "refs/heads/main", featureOid);

        var ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => fixture.Runner().VerifyAsync(
                request, "ephemeral-test-token", default));
        Assert.Equal("RemoteMirrorRemoteRefsChanged", ex.Code);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Forged_annotated_tag_type_is_rejected_by_fetched_physical_git_objects()
    {
        using var fixture = new Fixture();
        var request = fixture.Setup();
        var forged = GitHubRefObservationIntegrity.Seal(
            request.Organization, request.Repository,
            request.ExpectedRemoteRefs.ObservedAtUtc,
            request.ExpectedRemoteRefs.Refs.Select(x =>
                x.Name == "refs/tags/v1"
                    ? x with { ObjectType = "commit" } : x).ToArray());
        var ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => fixture.Runner().VerifyAsync(
                request with { ExpectedRemoteRefs = forged },
                "ephemeral-test-token", default));
        Assert.Equal("RemoteMirrorPhysicalRefsMismatch", ex.Code);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Disabled_alias_escape_stale_credentials_and_cancellation_fail_closed()
    {
        using var fixture = new Fixture();
        var request = fixture.Setup();
        var disabled = new GitHubReadOnlyObjectMirrorRunner(
            fixture.SourceRoot, fixture.ScratchRoot,
            new(false, fixture.RepositoryId, "gh-org/test-sandbox"));
        var ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => disabled.VerifyAsync(
                request, "ephemeral-test-token", default));
        Assert.Equal("RemoteMirrorDisabledOrWrongRepository", ex.Code);

        ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => fixture.Runner().VerifyAsync(
                request with { Organization = "github.com@attacker.invalid" },
                "ephemeral-test-token", default));
        Assert.Equal("RemoteMirrorInputInvalid", ex.Code);

        ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => fixture.Runner().VerifyAsync(request,
                "contains\nheaders", default));
        Assert.Equal("RemoteMirrorCredentialInvalid", ex.Code);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));

        ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => fixture.Runner().VerifyAsync(
                request with { TokenExpiresAtUtc =
                    DateTimeOffset.UtcNow.AddSeconds(3) },
                "ephemeral-test-token", default));
        Assert.Equal("RemoteMirrorInputInvalid", ex.Code);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Runner().VerifyAsync(
                request, "ephemeral-test-token", stopped.Token));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Symlinked_scratch_root_and_missing_source_are_rejected_before_fetch()
    {
        using var fixture = new Fixture();
        var request = fixture.Setup();
        var symlink = Path.Combine(fixture.Root, "scratch-symlink");
        Directory.CreateSymbolicLink(symlink, fixture.ScratchRoot);
        var runner = new GitHubReadOnlyObjectMirrorRunner(
            fixture.SourceRoot, symlink,
            new(true, fixture.RepositoryId, "gh-org/test-sandbox"),
            fixture.Remote);
        var ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => runner.VerifyAsync(
                request, "ephemeral-test-token", default));
        Assert.Equal("RemoteMirrorSymlinkBlocked", ex.Code);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));

        var missing = request with { SourceSnapshotId = Guid.NewGuid() };
        ex = await Assert.ThrowsAsync<GitHubReadOnlyObjectMirrorException>(
            () => fixture.Runner().VerifyAsync(
                missing, "ephemeral-test-token", default));
        Assert.Equal("RemoteMirrorOfflineClosureRejected:GitClosureRepositoryInvalid",
            ex.Code);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "f057-github-readonly-mirror-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "source");
        public string RemoteRoot => Path.Combine(Root, "remote");
        public string ScratchRoot => Path.Combine(Root, "scratch");
        public Guid SourceSnapshotId { get; } = Guid.NewGuid();
        public long RepositoryId { get; } = 194857392L;
        public string Source => Path.Combine(SourceRoot,
            SourceSnapshotId.ToString("N") + ".git");
        public string Remote => Path.Combine(RemoteRoot, "git-target.git");

        public Fixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(RemoteRoot);
            Directory.CreateDirectory(ScratchRoot);
        }

        public GitHubReadOnlyObjectMirrorRunner Runner() =>
            new(SourceRoot, ScratchRoot,
                new(true, RepositoryId, "gh-org/test-sandbox"), Remote);

        public GitHubReadOnlyObjectMirrorRequest Setup(string format = "sha1")
        {
            var work = Path.Combine(Root, "work");
            Git("init", "-q", "--object-format=" + format, work);
            Git("-C", work, "config", "user.name", "F057 Test");
            Git("-C", work, "config", "user.email",
                "f057@example.invalid");
            File.WriteAllText(Path.Combine(work, "first.txt"), "first\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "first");
            Git("-C", work, "branch", "-M", "main");
            Git("-C", work, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(work, "feature.txt"), "feature\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "feature");
            Git("-C", work, "checkout", "-q", "main");
            File.WriteAllText(Path.Combine(work, "first.txt"), "main\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "main change");
            Git("-C", work, "merge", "--no-ff", "-m", "merge", "feature");
            Git("-C", work, "tag", "-a", "v1", "-m", "annotated");

            Git("clone", "--mirror", "--no-hardlinks", "-q", work, Source);
            Git("clone", "--mirror", "--no-hardlinks", "-q", Source, Remote);

            // Only the target knows this extra branch. Source closure
            // still matches every selected SHA and no target ref is deleted.
            var main = Git("--git-dir=" + Remote,
                "rev-parse", "refs/heads/main").Trim();
            Git("--git-dir=" + Remote, "update-ref",
                "refs/heads/target-only", main);

            var expected = GitHubRefObservationIntegrity.Seal(
                "gh-org", "test-sandbox",
                DateTimeOffset.UtcNow, TypedRemoteRefs());
            return new(SourceSnapshotId, RepositoryId, "gh-org",
                "test-sandbox", SourceRefs(), expected,
                DateTimeOffset.UtcNow.AddMinutes(30));
        }

        public IReadOnlyList<GitMirrorRef> SourceRefs() =>
            MirrorRefs(Source);

        public IReadOnlyList<GitMirrorRef> RemoteRefs() =>
            MirrorRefs(Remote);

        private IReadOnlyList<GitMirrorRef> MirrorRefs(string repo) =>
            GitMirrorRefReconciler.CanonicalizeObservedRefs(
                Git("--git-dir=" + repo, "for-each-ref",
                    "--format=%(refname)%09%(objectname)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.TrimEnd('\r').Split('\t'))
                .Select(x => new GitMirrorRef(x[0], x[1])).ToArray());

        private IReadOnlyList<GitHubObservedRef> TypedRemoteRefs() =>
            Git("--git-dir=" + Remote, "for-each-ref",
                "--format=%(refname)%09%(objectname)%09%(objecttype)")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.TrimEnd('\r').Split('\t'))
            .Select(x => new GitHubObservedRef(x[0], x[1], x[2]))
            .ToArray();

        public string Git(params string[] args)
        {
            using var process = new Process();
            process.StartInfo.FileName = "/usr/bin/git";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                throw new InvalidOperationException("git not available");
            var output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20_000) || process.ExitCode != 0)
                throw new InvalidOperationException(
                    "Git fixture command failed: " + errors);
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
