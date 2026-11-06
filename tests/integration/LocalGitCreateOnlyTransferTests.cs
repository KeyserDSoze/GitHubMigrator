using System.Diagnostics;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Executes the Linux Git binary against persistent, private, local bare
// destinations. No GitHub network, credentials, mock push or remote URL.
public sealed class LocalGitCreateOnlyTransferTests
{
    [Fact]
    public async Task Real_git_creates_only_missing_heads_and_annotated_tags()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        var receipt = await fixture.Runner().RunAsync(request, default);
        Assert.Equal(LocalGitCreateOnlyTransferIntegrity.VerifiedState,
            receipt.State);
        Assert.Equal(2, receipt.CreatedRefs);
        Assert.Equal(2, receipt.PreservedRefs);
        Assert.Equal("sha1", receipt.GitObjectFormat);
        Assert.Equal(request.Packet.EvidenceSha256, receipt.PacketSha256);
        Assert.Equal(request.Packet.RepositoryId, receipt.GitHubRepositoryId);
        Assert.Equal(request.Packet.CandidateSetSha256,
            GitImportRehearsalIntegrity.SealRefs(
                "f044-create-only-candidate-refs-v1", request.CreateRefs));
        Assert.True(receipt.LocalSandboxWritesPerformed);
        Assert.False(receipt.RemoteWritesPerformed);
        Assert.False(receipt.ExecutionAuthorized);
        Assert.False(receipt.ReadyForExecution);
        Assert.Equal(64, receipt.EvidenceSha256.Length);
        Assert.Equal(LocalGitCreateOnlyTransferIntegrity.RefsDigest(
            request.ExpectedTargetRefs.Concat(request.CreateRefs)
                .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray()),
            receipt.AfterRefsSha256);

        var inspected = await fixture.Runner().InspectAsync(request, default);
        Assert.Equal("ExpectedLocalRefsObserved", inspected.State);
        Assert.Equal(4, inspected.RemoteRefs);
        Assert.Equal(2, inspected.MatchingCreatedRefs);
        Assert.False(inspected.MayAutomaticallyRetry);
        Assert.False(inspected.ExecutionAuthorized);
        Assert.False(inspected.RemoteWritesPerformed);
        Assert.Equal(receipt.AfterRefsSha256, inspected.ActualRefsSha256);

        var after = fixture.ReadTargetRefs();
        Assert.Equal(4, after.Count);
        Assert.Contains(after, x => x.Name == "refs/tags/zzz-target-only"
            && request.ExpectedTargetRefs.Contains(x));
        Assert.Equal(request.CreateRefs.Where(x =>
            x.Name.StartsWith("refs/tags/", StringComparison.Ordinal))
            .Single().ObjectId,
            after.Single(x => x.Name == "refs/tags/v1").ObjectId);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
        Assert.Single(Directory.GetFiles(fixture.BundleRoot, "*.bundle"));
        fixture.Git("--git-dir=" + fixture.Target, "fsck", "--full",
            "--strict", "--no-reflogs", "--no-progress");
    }

    [Fact]
    public async Task Existing_candidate_ref_causes_fenced_refusal_not_overwrite()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        var main = request.SourceRefs.Single(x =>
            x.Name == "refs/heads/main").ObjectId;
        // Another actor creates the intended feature branch with an
        // object which differs from the source's feature commit.
        fixture.Git("--git-dir=" + fixture.Target, "update-ref",
            "refs/heads/feature", main);
        var before = fixture.ReadTargetRefs();
        var ex = await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
            () => fixture.Runner().RunAsync(request, default));
        Assert.Equal("SandboxBaselineChanged", ex.Code);
        Assert.Equal(before, fixture.ReadTargetRefs());
        Assert.DoesNotContain(fixture.ReadTargetRefs(),
            x => x.Name == "refs/tags/v1");
        var inspected = await fixture.Runner().InspectAsync(request, default);
        Assert.Equal(LocalGitCreateOnlyTransferIntegrity.DivergedState,
            inspected.State);
        Assert.False(inspected.MayAutomaticallyRetry);
    }

    [Fact]
    public async Task Lost_confirmation_after_real_push_requires_independent_inspection()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        var runner = fixture.Runner(_ =>
            throw new IOException("simulated lost response after real git push"));
        var ex = await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
            () => runner.RunAsync(request, default));
        Assert.Equal("SandboxOutcomeRequiresReconciliation", ex.Code);
        Assert.Equal(4, fixture.ReadTargetRefs().Count);
        var observed = await fixture.Runner().InspectAsync(request, default);
        Assert.Equal("ExpectedLocalRefsObserved", observed.State);
        Assert.Equal(2, observed.MatchingCreatedRefs);
        Assert.False(observed.MayAutomaticallyRetry);
        var retry = await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
            () => fixture.Runner().RunAsync(request, default));
        Assert.Equal("SandboxBaselineChanged", retry.Code);
    }

    [Fact]
    public async Task Duplicate_workers_are_serialized_and_never_force_push()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        async Task<string> AttemptAsync()
        {
            try
            {
                var result = await fixture.Runner().RunAsync(request, default);
                return result.State;
            }
            catch (LocalGitCreateOnlyTransferException ex)
            { return ex.Code; }
        }

        var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());
        Assert.Equal(1, results.Count(x =>
            x == LocalGitCreateOnlyTransferIntegrity.VerifiedState));
        Assert.Equal(1, results.Count(x => x == "SandboxBaselineChanged"));
        Assert.Equal(4, fixture.ReadTargetRefs().Count);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Forged_packet_ref_selection_and_sandbox_path_are_rejected()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        var bad = request with {
            Packet = request.Packet with { ExecutionAuthorized = true }
        };
        Assert.Equal("SandboxPacketInvalid",
            (await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
                () => fixture.Runner().RunAsync(bad, default))).Code);
        bad = request with {
            CreateRefs = request.CreateRefs.Reverse().ToArray()
        };
        Assert.Equal("SandboxRefPlanInvalid",
            (await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
                () => fixture.Runner().RunAsync(bad, default))).Code);
        bad = request with {
            SandboxId = Guid.NewGuid()
        };
        Assert.Equal("SandboxBareRepoMissing",
            (await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
                () => fixture.Runner().RunAsync(bad, default))).Code);
        Assert.Equal(2, fixture.ReadTargetRefs().Count);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Damaged_source_bundle_fails_before_the_local_push()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        await File.WriteAllBytesAsync(Path.Combine(fixture.BundleRoot,
            request.Bundle.BundleFileName), new byte[checked((int)
            request.Bundle.BundleBytes)]);
        var ex = await Assert.ThrowsAsync<GitSourceBundleException>(
            () => fixture.Runner().RunAsync(request, default));
        Assert.Equal("BundleStoredDigestMismatch", ex.Code);
        Assert.Equal(2, fixture.ReadTargetRefs().Count);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    [Fact]
    public async Task Configuration_and_symlink_target_never_trigger_git_transport()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        var disabled = new LocalGitCreateOnlyTransferRunner(
            fixture.BundleRoot, fixture.SandboxRoot, fixture.ScratchRoot);
        Assert.Equal("SandboxTransportDisabled",
            (await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
                () => disabled.RunAsync(request, default))).Code);

        var targetAlias = Path.Combine(fixture.SandboxRoot,
            Guid.NewGuid().ToString("N") + ".git");
        Directory.CreateSymbolicLink(targetAlias, fixture.Target);
        var bad = request with {
            SandboxId = Guid.Parse(Path.GetFileNameWithoutExtension(targetAlias))
        };
        Assert.Equal("SandboxSymlinkBlocked",
            (await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
                () => fixture.Runner().RunAsync(bad, default))).Code);
        Assert.Equal(2, fixture.ReadTargetRefs().Count);
    }

    [Fact]
    public async Task Nested_symlink_under_bare_refs_is_blocked_before_push()
    {
        using var fixture = new Fixture();
        var request = await fixture.PrepareAsync();
        var external = Path.Combine(fixture.Root, "external-ref-folder");
        Directory.CreateDirectory(external);
        Directory.CreateSymbolicLink(
            Path.Combine(fixture.Target, "refs", "malicious-link"), external);
        var before = fixture.ReadTargetRefs();
        var error = await Assert.ThrowsAsync<LocalGitCreateOnlyTransferException>(
            () => fixture.Runner().RunAsync(request, default));
        Assert.Equal("SandboxSymlinkBlocked", error.Code);
        Assert.Equal(before, fixture.ReadTargetRefs());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ScratchRoot));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "f048-local-git-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "mirrors");
        public string BundleRoot => Path.Combine(Root, "bundles");
        public string ScratchRoot => Path.Combine(Root, "scratch");
        public string SandboxRoot => Path.Combine(Root, "destinations");
        public Guid SandboxId { get; } = Guid.NewGuid();
        public Guid WorkpackId { get; } = Guid.NewGuid();
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public Guid PlanId { get; } = Guid.NewGuid();
        public string Target => Path.Combine(SandboxRoot,
            SandboxId.ToString("N") + ".git");

        public Fixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(BundleRoot);
            Directory.CreateDirectory(ScratchRoot);
            Directory.CreateDirectory(SandboxRoot);
        }

        public LocalGitCreateOnlyTransferRunner Runner(
            Func<CancellationToken, Task>? hook = null) => new(
                BundleRoot, SandboxRoot, ScratchRoot, enabled: true,
                testAfterPush: hook);

        public async Task<LocalGitCreateOnlyTransferRequest> PrepareAsync()
        {
            var work = Path.Combine(Root, "work");
            Git("init", "-q", work);
            Git("-C", work, "config", "user.email", "f048@example.invalid");
            Git("-C", work, "config", "user.name", "Git Transport Test");
            File.WriteAllText(Path.Combine(work, "main.txt"), "main\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "main");
            Git("-C", work, "branch", "-M", "main");
            Git("-C", work, "tag", "-a", "v1", "-m", "annotated release");
            Git("-C", work, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(work, "feature.txt"), "feature\n");
            Git("-C", work, "add", ".");
            Git("-C", work, "commit", "-q", "-m", "feature");
            Git("-C", work, "checkout", "-q", "main");

            const string sourceId = "fixture-repository";
            var source = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
                SourceRoot, ConnectionId, sourceId);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Git("clone", "--bare", "--quiet", work, source);
            var refs = ReadRefs(source);
            Assert.Equal(3, refs.Count);
            var acquirer = new LocalGitSourceBundleAcquirer(
                SourceRoot, BundleRoot);
            var bundle = await acquirer.AcquireAsync(new(
                WorkpackId, ConnectionId, sourceId,
                new string('a', 64), new string('b', 64), refs), default);
            var view = new GitSourceBundleView(WorkpackId, PlanId,
                Guid.NewGuid(), sourceId, bundle.SourceEvidenceSha256,
                new string('a', 64), bundle.BundleFileName, bundle.BundleSha256,
                bundle.BundleBytes, bundle.RefCount,
                bundle.AcquiredAtUtc, true, false);

            Git("init", "--bare", "--quiet", "--object-format=sha1", Target);
            var main = refs.Single(x => x.Name == "refs/heads/main");
            Git("--git-dir=" + source, "push", "--atomic", Target,
                main.ObjectId + ":refs/heads/main",
                main.ObjectId + ":refs/tags/zzz-target-only");
            var before = ReadRefs(Target);
            Assert.Equal(2, before.Count);
            var create = refs.Where(x =>
                !before.Any(y => y.Name == x.Name)).ToArray();
            var packet = new GitHubCreateOnlyPacket(
                Guid.NewGuid(), PlanId, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), WorkpackId, 2468,
                "gh-org", "sandbox",
                new string('c', 64), bundle.BundleSha256,
                new string('a', 64), new string('d', 64),
                new string('e', 64), new string('f', 64),
                new string('1', 64), new string('2', 64),
                new string('3', 64),
                GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", create),
                new string('4', 64), create.Length, 1, 1,
                GitHubCreateOnlyPacketIntegrity.State,
                false, false, false, false);
            return new(packet, view, SandboxId, refs, before, create);
        }

        public IReadOnlyList<GitMirrorRef> ReadTargetRefs() => ReadRefs(Target);

        private IReadOnlyList<GitMirrorRef> ReadRefs(string bare)
        {
            var lines = Git("--git-dir=" + bare, "for-each-ref",
                "--format=%(refname)%09%(objectname)");
            return GitMirrorRefReconciler.CanonicalizeObservedRefs(
                lines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.TrimEnd('\r').Split('\t'))
                    .Select(x => new GitMirrorRef(x[0], x[1])).ToArray());
        }

        public string Git(params string[] args)
        {
            using var process = new Process();
            process.StartInfo.FileName = "/usr/bin/git";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                throw new InvalidOperationException("git missing");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000) || process.ExitCode != 0)
                throw new InvalidOperationException(
                    "Local test Git fixture failed: " + stderr);
            return stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
