using System.Diagnostics;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// F-084 executes the ACTUAL F-083 --atomic --force-with-lease Git command
// against an isolated local bare repository, then F-057 smart-Git fetch
// and F-056 physical full-object closure. No GitHub network or secret.
public sealed class GitHubSandboxAtomicPushFullClosureTests
{
    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task F084_real_atomic_create_only_push_and_full_history_closure(
        string format)
    {
        using var f = new Fixture();
        var input = await f.SetupAsync(format);
        var before = f.TargetRefs();
        Assert.Single(before);
        Assert.Equal("refs/heads/main", before[0].Name);

        await f.Runner().PushOnceAsync(input, "ephemeral-f084-test-token", default);
        var after = f.TargetRefs();
        Assert.Equal(3, after.Count);
        Assert.Contains(after, r => r.Name == "refs/heads/feature" &&
            input.Source.Candidates.Contains(r));
        Assert.Contains(after, r => r.Name == "refs/tags/v1" &&
            input.Source.Candidates.Contains(r));
        Assert.Equal(before[0], after.Single(x =>
            x.Name == "refs/heads/main"));
        Assert.Empty(Directory.GetFileSystemEntries(f.Scratch));
        f.Git("--git-dir=" + f.Target, "fsck", "--full",
            "--strict", "--no-reflogs", "--no-progress");

        // F-057 full closure is independently verified using a NEW local
        // smart-Git fetch. Not just sha1/sha256 refs or commit tip reads.
        var observed = GitHubRefObservationIntegrity.Seal(
            "gh-org", "sandbox", DateTimeOffset.UtcNow, f.TypedTargetRefs());
        var request = new GitHubReadOnlyObjectMirrorRequest(
            f.SourceMirrorId, f.RepositoryId, "gh-org", "sandbox",
            f.SourceRefs(), observed, DateTimeOffset.UtcNow.AddMinutes(30));
        var closure = await new GitHubReadOnlyObjectMirrorRunner(
            f.ClosureSource, f.ClosureScratch,
            new GitHubReadOnlyObjectMirrorOptions(
                true, f.RepositoryId, "gh-org/sandbox"), f.Target)
            .VerifyAsync(request, "ephemeral-f084-test-token", default);
        Assert.Equal(format, closure.GitObjectFormat);
        // Two commits + two trees + two blobs + annotated tag = 7.
        Assert.True(closure.VerifiedReachableObjects >= 7);
        Assert.Equal(closure.SourceObjectSetSha256,
            closure.RemoteObjectSetSha256);
        Assert.Equal(GitHubRemoteRefFenceIntegrity.Fingerprint(
            observed.Refs), closure.RemoteRefFingerprintSha256);
        Assert.True(closure.GitHubFetchPerformed);
        Assert.False(closure.RemotePushInvoked);
        Assert.False(closure.RemoteWritesAuthorized);
        Assert.False(closure.MigrationComplete);
        Assert.Empty(Directory.GetFileSystemEntries(f.ClosureScratch));

        var replay = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
            () => f.Runner().PushOnceAsync(input,
                "ephemeral-f084-test-token", default));
        Assert.Equal("SandboxPushAlreadyAttempted", replay.Code);
        Assert.Equal(after, f.TargetRefs());
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("unrelated")]
    public async Task F084_live_git_remote_fence_refuses_a_changed_target_without_overwrite(
        string kind)
    {
        using var f = new Fixture();
        var input = await f.SetupAsync();
        var before = f.TargetRefs();
        Assert.Single(before);
        var name = kind == "candidate"
            ? "refs/heads/feature" : "refs/heads/unrelated";
        f.Git("--git-dir=" + f.Target, "update-ref",
            name, before[0].ObjectId);
        var drifted = f.TargetRefs();

        var error = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
            () => f.Runner().PushOnceAsync(input,
                "ephemeral-f084-test-token", default));
        Assert.Equal("SandboxPushRemoteBaselineChanged", error.Code);
        Assert.Equal(drifted, f.TargetRefs());
        Assert.DoesNotContain(f.TargetRefs(), r => r.Name == "refs/tags/v1");
        Assert.Empty(Directory.GetFileSystemEntries(f.Scratch));

        var repeat = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(
            () => f.Runner().PushOnceAsync(input,
                "ephemeral-f084-test-token", default));
        Assert.Equal("SandboxPushAlreadyAttempted", repeat.Code);
        Assert.Equal(drifted, f.TargetRefs());
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(),
            "f084-one-shot-" + Guid.NewGuid().ToString("N"));
        public readonly long RepositoryId =
            Random.Shared.NextInt64(1_000_000, 9_000_000);
        public readonly Guid SourceMirrorId = Guid.NewGuid();
        public readonly Guid WorkpackId = Guid.NewGuid();
        public readonly Guid ConnectionId = Guid.NewGuid();
        public const string SourceRepositoryId = "source-repo-01";

        public string Work => Path.Combine(Root, "work");
        public string SourceRoot => Path.Combine(Root, "source-root");
        public string Source => LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
            SourceRoot, ConnectionId, SourceRepositoryId);
        public string BundleRoot => Path.Combine(Root, "bundles");
        public string Scratch => Path.Combine(Root, "push-scratch");
        public string Target => Path.Combine(Root, "remote.git");
        public string ClosureSource => Path.Combine(Root, "closure-source");
        public string ClosureScratch => Path.Combine(Root, "closure-scratch");

        public Fixture()
        {
            foreach (var dir in new[] {
                Root, SourceRoot, BundleRoot, Scratch,
                ClosureSource, ClosureScratch
            }) Directory.CreateDirectory(dir);
        }

        public GitHubSandboxCreateOnlyPushRunner Runner() =>
            new(BundleRoot, Scratch,
                new GitHubSandboxOneShotOptions(
                    true, RepositoryId, "gh-org", "sandbox"), Target);

        public async Task<GitHubSandboxPushInput> SetupAsync(
            string objectFormat = "sha1")
        {
            Git("init", "-q", "--object-format=" + objectFormat,
                "--initial-branch=main", Work);
            Git("-C", Work, "config", "user.name", "F084 isolated test");
            Git("-C", Work, "config", "user.email", "f084@example.invalid");
            File.WriteAllText(Path.Combine(Work, "original.txt"), "A\n");
            Git("-C", Work, "add", ".");
            Git("-C", Work, "commit", "-q", "-m", "original");
            Git("-C", Work, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(Work, "feature.txt"), "B\n");
            Git("-C", Work, "add", ".");
            Git("-C", Work, "commit", "-q", "-m", "feature");
            Git("-C", Work, "tag", "-a", "v1", "-m", "annotated");
            Git("-C", Work, "checkout", "-q", "main");

            Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
            Git("clone", "--mirror", "--quiet", Work, Source);
            Git("clone", "--mirror", "--quiet", Source,
                Path.Combine(ClosureSource, SourceMirrorId.ToString("N") + ".git"));
            Git("init", "--bare", "-q",
                "--object-format=" + objectFormat, Target);
            Git("-C", Work, "push", "--quiet", Target,
                "refs/heads/main:refs/heads/main");

            var all = SourceRefs();
            var main = all.Single(x => x.Name == "refs/heads/main");
            var creates = all.Where(x =>
                x.Name != "refs/heads/main").ToArray();
            Assert.Equal(2, creates.Length);
            var baseline = GitHubRefObservationIntegrity.Seal(
                "gh-org", "sandbox", DateTimeOffset.UtcNow,
                [new("refs/heads/main", main.ObjectId, "commit")]);
            var baselineSha = GitHubRemoteRefFenceIntegrity.Fingerprint(
                baseline.Refs);

            var acquirer = new LocalGitSourceBundleAcquirer(
                SourceRoot, BundleRoot);
            var artifact = await acquirer.AcquireAsync(new(
                WorkpackId, ConnectionId, SourceRepositoryId,
                Sha('3'), Sha('2'), all), default);
            var packet = new GitHubCreateOnlyPacket(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                WorkpackId, RepositoryId, "gh-org", "sandbox",
                Sha('1'), artifact.BundleSha256, Sha('3'),
                Sha('4'), Sha('5'), Sha('6'), Sha('7'), Sha('8'),
                Sha('a'), GitImportRehearsalIntegrity.SealRefs(
                    "f044-create-only-candidate-refs-v1", creates),
                Sha('9'), creates.Length, 1, 0,
                GitHubCreateOnlyPacketIntegrity.State,
                false, false, false, false);
            var bundle = new GitSourceBundleView(
                WorkpackId, packet.PlanId, Guid.NewGuid(),
                SourceRepositoryId, Sha('2'), packet.ReconciliationSha256,
                artifact.BundleFileName, artifact.BundleSha256,
                artifact.BundleBytes, artifact.RefCount,
                artifact.AcquiredAtUtc, true, false);
            var draft = new GitHubRemoteSandboxPreSendBarrierReceipt(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                RepositoryId, Guid.NewGuid(),
                Sha('b'), Sha('c'), Sha('d'),
                packet.EvidenceSha256, packet.CandidateSetSha256,
                Sha('e'), DateTimeOffset.UtcNow, "",
                GitHubRemoteSandboxPreSendBarrierIntegrity.Recorded,
                false, false, false, false);
            var marker = draft with {
                EvidenceSha256 =
                    GitHubRemoteSandboxPreSendBarrierIntegrity.Digest(draft)
            };
            return new(new(packet, creates), bundle, marker,
                baselineSha, baseline);
        }

        public IReadOnlyList<GitMirrorRef> SourceRefs() => ReadRefs(Source);
        public IReadOnlyList<GitMirrorRef> TargetRefs() => ReadRefs(Target);

        private IReadOnlyList<GitMirrorRef> ReadRefs(string repo) =>
            GitMirrorRefReconciler.CanonicalizeObservedRefs(
                Git("--git-dir=" + repo, "for-each-ref",
                        "--format=%(refname)%09%(objectname)")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.TrimEnd('\r').Split('\t'))
                    .Select(x => new GitMirrorRef(x[0], x[1])).ToArray());

        public IReadOnlyList<GitHubObservedRef> TypedTargetRefs() =>
            Git("--git-dir=" + Target, "for-each-ref",
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
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                throw new InvalidOperationException("Git fixture unavailable");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(25_000) || process.ExitCode != 0)
                throw new InvalidOperationException(
                    "F084 Git fixture failed: " + stderr);
            return stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Sha(char c) => new(c, 64);
    }
}
