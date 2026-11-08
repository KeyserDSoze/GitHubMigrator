using System.Diagnostics;
using System.Security.Cryptography;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Runs the actual Git binary on isolated local repositories. No internet,
// Azure/GitHub PAT, target writes, mock "Git output" or HTTP is involved.
public sealed class LocalGitSourceBundleAcquirerTests
{
    [Fact]
    public async Task Real_git_bundle_exports_complete_history_and_exact_tag_branch_refs()
    {
        using var root = new LocalFixture();
        var (src, expected, commit) = root.ProvisionBareMirror();
        var request = root.Request(expected);
        var result = await root.Acquirer.AcquireAsync(request, default);
        Assert.Equal(request.WorkpackId, result.WorkpackId);
        Assert.Equal(expected.Count, result.RefCount);
        Assert.Equal(request.SourceEvidenceSha256, result.SourceEvidenceSha256);
        Assert.Equal(request.WorkpackId.ToString("N") + ".bundle", result.BundleFileName);
        Assert.True(result.BundleBytes > 0);
        Assert.InRange(result.BundleBytes, 1L, LocalGitSourceBundleAcquirer.MaxBundleBytes);
        var bundlePath = Path.Combine(root.BundleRoot, result.BundleFileName);
        Assert.True(File.Exists(bundlePath));
        Assert.Equal(new FileInfo(bundlePath).Length, result.BundleBytes);
        await using (var file = File.OpenRead(bundlePath))
            Assert.Equal(Convert.ToHexStringLower(await SHA256.HashDataAsync(file)),
                result.BundleSha256);

        // Import from the generated binary bundle into a brand-new bare
        // target proves this is not merely a list of matching ref strings.
        var imported = Path.Combine(root.Temp, "imported.git");
        RunGit("clone", "--mirror", "--quiet", bundlePath, imported);
        Assert.Equal(commit, RunGit("--git-dir=" + imported, "rev-parse", "refs/heads/main").Trim());
        Assert.Equal(commit, RunGit("--git-dir=" + imported, "rev-parse", "refs/tags/v1").Trim());
        Assert.Equal("source content\n", RunGit("--git-dir=" + imported,
            "show", "refs/heads/main:README.md"));
        Assert.True(Directory.Exists(src));
    }

    [Fact]
    public async Task Changed_source_oid_fails_before_any_bundle_is_committed()
    {
        using var root = new LocalFixture();
        var (src, expected, _) = root.ProvisionBareMirror();
        var changed = expected.Select(x => x with {
            ObjectId = x.ObjectId == new string('c', 40)
                ? new string('b', 40) : new string('c', 40)
        }).ToArray();
        var ex = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            root.Acquirer.AcquireAsync(root.Request(changed), default));
        Assert.Equal("BundleSourceRefMismatch", ex.Code);
        Assert.Empty(Directory.GetFiles(root.BundleRoot, "*.bundle"));
    }

    [Fact]
    public async Task Hidden_git_refs_are_not_silently_added_to_export()
    {
        using var root = new LocalFixture();
        var (src, expected, commit) = root.ProvisionBareMirror();
        RunGit("--git-dir=" + src, "update-ref", "refs/notes/unreviewed", commit);
        var ex = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            root.Acquirer.AcquireAsync(root.Request(expected), default));
        Assert.Equal("BundleUnexpectedRef", ex.Code);
        Assert.Empty(Directory.GetFiles(root.BundleRoot));
    }

    [Fact]
    public async Task Existing_bundle_is_not_replaced_on_replay()
    {
        using var root = new LocalFixture();
        var (_, refs, _) = root.ProvisionBareMirror();
        var request = root.Request(refs);
        var prior = await root.Acquirer.AcquireAsync(request, default);
        // If a process crashed after atomic promotion but before the DB
        // checkpoint, the verified file is recovered without a rewrite.
        var replay = await root.Acquirer.AcquireAsync(request, default);
        Assert.Equal(prior.BundleSha256, replay.BundleSha256);
        Assert.Equal(prior.BundleBytes, replay.BundleBytes);
        var path = Path.Combine(root.BundleRoot, prior.BundleFileName);
        await using var file = File.OpenRead(path);
        Assert.Equal(prior.BundleSha256,
            Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
    }

    [Fact]
    public async Task Missing_roots_symlinked_paths_and_cancelled_commands_fail_closed()
    {
        using var root = new LocalFixture();
        var (_, refs, _) = root.ProvisionBareMirror();
        var request = root.Request(refs);
        var disabled = new LocalGitSourceBundleAcquirer("", "");
        Assert.Equal("LocalBundleStoreNotConfigured",
            (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                disabled.AcquireAsync(request, default))).Code);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            root.Acquirer.AcquireAsync(request, stopped.Token));

        var linked = Path.Combine(root.Temp, "linked");
        Directory.CreateSymbolicLink(linked, root.SourceRoot);
        var unsafeAcquirer = new LocalGitSourceBundleAcquirer(linked, root.BundleRoot);
        Assert.Equal("LocalBundleSymlinkBlocked",
            (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                unsafeAcquirer.AcquireAsync(request, default))).Code);
    }

    [Fact]
    public async Task Persisted_bundle_checksum_is_rehashed_and_byte_tampering_fails_closed()
    {
        using var root = new LocalFixture();
        var (_, refs, _) = root.ProvisionBareMirror();
        var artifact = await root.Acquirer.AcquireAsync(root.Request(refs), default);
        var stored = new GitSourceBundleView(
            root.WorkpackId, Guid.NewGuid(), Guid.NewGuid(),
            root.RepositoryId, artifact.SourceEvidenceSha256,
            new string('a', 64), artifact.BundleFileName,
            artifact.BundleSha256, artifact.BundleBytes,
            artifact.RefCount, artifact.AcquiredAtUtc, true, false);
        var verifier = new LocalGitBundleArtifactStore(root.BundleRoot);
        var verified = await verifier.VerifyAsync(stored, default);
        Assert.Equal(artifact.BundleSha256, verified.BundleSha256);
        Assert.False(verified.ReadyForExecution);

        var path = Path.Combine(root.BundleRoot, artifact.BundleFileName);
        await File.WriteAllBytesAsync(path, new byte[checked((int)artifact.BundleBytes)]);
        var tamper = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            verifier.VerifyAsync(stored, default));
        Assert.Equal("BundleStoredDigestMismatch", tamper.Code);

        var orphan = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            root.Acquirer.AcquireAsync(root.Request(refs), default));
        Assert.Equal("GitObjectVerificationFailed", orphan.Code);
    }

    [Fact]
    public async Task Missing_or_symlinked_persisted_files_never_count_as_verified()
    {
        using var root = new LocalFixture();
        var (_, refs, _) = root.ProvisionBareMirror();
        var artifact = await root.Acquirer.AcquireAsync(root.Request(refs), default);
        var evidence = new GitSourceBundleView(
            root.WorkpackId, Guid.NewGuid(), Guid.NewGuid(),
            root.RepositoryId, artifact.SourceEvidenceSha256,
            new string('a', 64), artifact.BundleFileName,
            artifact.BundleSha256, artifact.BundleBytes,
            artifact.RefCount, artifact.AcquiredAtUtc, true, false);
        var verifier = new LocalGitBundleArtifactStore(root.BundleRoot);
        var path = Path.Combine(root.BundleRoot, artifact.BundleFileName);
        var safeCopy = Path.Combine(root.Temp, "copy.bundle");
        File.Copy(path, safeCopy);
        File.Delete(path);
        Assert.Equal("BundleStoredFileMissing",
            (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                verifier.VerifyAsync(evidence, default))).Code);
        File.CreateSymbolicLink(path, safeCopy);
        Assert.Equal("LocalBundleSymlinkBlocked",
            (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                verifier.VerifyAsync(evidence, default))).Code);
    }

    [Fact]
    public async Task Quota_is_checked_before_atomic_promotion_and_aborted_temp_is_deleted()
    {
        using var root = new LocalFixture();
        var (_, refs, _) = root.ProvisionBareMirror();
        var restricted = new LocalGitSourceBundleAcquirer(
            root.SourceRoot, root.BundleRoot, maxStoreBytes: 128);
        var rejected = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            restricted.AcquireAsync(root.Request(refs), default));
        Assert.Equal("BundleStoreQuotaExceeded", rejected.Code);
        Assert.Empty(Directory.GetFiles(root.BundleRoot, "*.bundle"));
        Assert.Empty(Directory.GetFiles(root.BundleRoot, "*.tmp"));
    }

    [Fact]
    public async Task Cleanup_only_removes_old_recognized_temp_files_never_bundles()
    {
        using var root = new LocalFixture();
        var store = new LocalGitBundleArtifactStore(root.BundleRoot);
        var name = root.WorkpackId.ToString("N");
        var obsolete = Path.Combine(root.BundleRoot,
            name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var active = Path.Combine(root.BundleRoot,
            name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var unknown = Path.Combine(root.BundleRoot, "other.tmp");
        var proof = Path.Combine(root.BundleRoot, name + ".bundle");
        await File.WriteAllTextAsync(obsolete, "abandoned staging");
        await File.WriteAllTextAsync(active, "active staging");
        await File.WriteAllTextAsync(unknown, "unrecognized temp");
        await File.WriteAllTextAsync(proof, "protected bundle");
        File.SetLastWriteTimeUtc(obsolete, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddDays(-2));
        Assert.Equal(1, await store.CleanupStaleTempsAsync(
            TimeSpan.FromHours(24), default));
        Assert.False(File.Exists(obsolete));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(unknown));
        Assert.True(File.Exists(proof));
        Assert.Equal("BundleCleanupAgeTooShort",
            (await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                store.CleanupStaleTempsAsync(TimeSpan.FromHours(1), default))).Code);
    }

    private sealed class LocalFixture : IDisposable
    {
        public string Temp { get; } =
            Path.Combine(Path.GetTempPath(), "gmb-f035-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Temp, "source");
        public string BundleRoot => Path.Combine(Temp, "bundles");
        public Guid SourceConnectionId { get; } = Guid.NewGuid();
        public Guid WorkpackId { get; } = Guid.NewGuid();
        public string RepositoryId => "repo-id-α";
        public LocalGitSourceBundleAcquirer Acquirer { get; }

        public LocalFixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(BundleRoot);
            Acquirer = new LocalGitSourceBundleAcquirer(SourceRoot, BundleRoot);
        }

        public (string Source, IReadOnlyList<GitMirrorRef> Refs, string Commit)
            ProvisionBareMirror()
        {
            var work = Path.Combine(Temp, "working");
            Directory.CreateDirectory(work);
            RunGit("init", "-q", work);
            RunGit("-C", work, "config", "user.email", "git-test@example.invalid");
            RunGit("-C", work, "config", "user.name", "Git Test");
            File.WriteAllText(Path.Combine(work, "README.md"), "source content\n");
            RunGit("-C", work, "add", "README.md");
            RunGit("-C", work, "commit", "-q", "-m", "initial");
            RunGit("-C", work, "branch", "-M", "main");
            RunGit("-C", work, "tag", "v1");
            var commit = RunGit("-C", work, "rev-parse", "HEAD").Trim();
            var bare = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
                SourceRoot, SourceConnectionId, RepositoryId);
            Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
            RunGit("clone", "--bare", "--quiet", work, bare);
            var refs = new[] {
                new GitMirrorRef("refs/heads/main", commit),
                new GitMirrorRef("refs/tags/v1", commit)
            };
            return (bare, refs, commit);
        }

        public GitSourceBundleRequest Request(IReadOnlyList<GitMirrorRef> refs) =>
            new(WorkpackId, SourceConnectionId, RepositoryId,
                new string('a', 64), new string('b', 64), refs);

        public void Dispose()
        {
            try { Directory.Delete(Temp, recursive: true); }
            catch (IOException) { }
        }
    }

    private static string RunGit(params string[] args)
    {
        using var process = new Process();
        process.StartInfo.FileName = "/usr/bin/git";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000) || process.ExitCode != 0)
            throw new InvalidOperationException("git fixture command failed: " + error);
        return output;
    }
}
