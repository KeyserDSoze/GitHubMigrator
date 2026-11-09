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
    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task F035v2_internal_rehearsal_exports_real_offline_bundle_and_rechecks_provenance(
        int oidLength)
    {
        using var root = new LocalFixture();
        var (_, refs, commit) = root.ProvisionBareMirror(oidLength);
        var plan = RehearsalPlan(root, refs);
        var gate = new StubLaterCheckpointBundlePreflight(plan);
        var service = new GitIncrementalLaterCheckpointLocalBundleAcquisition(
            new(true), gate, root.Acquirer,
            new LocalGitBundleArtifactStore(root.BundleRoot),
            TimeProvider.System);
        var before = Directory.GetFiles(root.BundleRoot, "*.bundle");
        Assert.Empty(before);
        var result = await service.RehearseAsync(
            plan.PreparationId, plan.PlanId, plan.CheckpointId,
            plan.TargetObservationId, plan.SourceOnlyRequest.WorkpackId,
            plan.SourceOnlyRequest.SourceRepositoryId, plan.TargetReceiptSha256,
            default);
        Assert.Equal(2, gate.Calls);
        Assert.False(result.DurableEvidencePersisted ||
            result.CanPush || result.CanRetry ||
            result.CanDispatch || result.MigrationComplete);
        Assert.Equal(plan.EvidenceSha256, result.ReadPlanSha256);
        Assert.Equal(plan.SourceFullRefProofSha256,
            result.SourceFullRefProofSha256);
        Assert.Equal(plan.TargetReceiptSha256,
            result.TargetReceiptSha256);
        Assert.Equal(oidLength, commit.Length);
        Assert.Equal(2, result.RefCount);
        var bundlePath = Path.Combine(root.BundleRoot, result.BundleFileName);
        Assert.True(File.Exists(bundlePath));
        await using (var file = File.OpenRead(bundlePath))
            Assert.Equal(result.BundleSha256,
                Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
        Assert.Equal(new FileInfo(bundlePath).Length, result.BundleBytes);
        var imported = Path.Combine(root.Temp, "v2-inspected.git");
        RunGit("clone", "--mirror", "--quiet", bundlePath, imported);
        Assert.Equal(commit, RunGit("--git-dir=" + imported,
            "rev-parse", "refs/heads/main").Trim());
        var replay = await service.RehearseAsync(
            plan.PreparationId, plan.PlanId, plan.CheckpointId,
            plan.TargetObservationId, plan.SourceOnlyRequest.WorkpackId,
            plan.SourceOnlyRequest.SourceRepositoryId, plan.TargetReceiptSha256,
            default);
        Assert.Equal(4, gate.Calls);
        Assert.Equal(result.BundleSha256, replay.BundleSha256);
        Assert.Equal(result.BundleBytes, replay.BundleBytes);
        Assert.Single(Directory.GetFiles(root.BundleRoot, "*.bundle"));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public async Task F035v2_rehearsal_refuses_revoked_gate_and_wrong_source_OIDs(
        int oidLength)
    {
        using var root = new LocalFixture();
        var (_, refs, _) = root.ProvisionBareMirror(oidLength);
        var plan = RehearsalPlan(root, refs);
        var gate = new StubLaterCheckpointBundlePreflight(plan) {
            RejectSecond = true
        };
        var service = new GitIncrementalLaterCheckpointLocalBundleAcquisition(
            new(true), gate, root.Acquirer,
            new LocalGitBundleArtifactStore(root.BundleRoot),
            TimeProvider.System);
        var revoked = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            service.RehearseAsync(plan.PreparationId, plan.PlanId,
                plan.CheckpointId, plan.TargetObservationId,
                plan.SourceOnlyRequest.WorkpackId,
                plan.SourceOnlyRequest.SourceRepositoryId,
                plan.TargetReceiptSha256, default));
        Assert.Equal("FakeLaterCheckpointApprovalRevoked", revoked.Code);
        Assert.Equal(2, gate.Calls);
        // A promoted orphan cannot authorize migration: nothing was
        // returned or persisted to PostgreSQL.
        Assert.Single(Directory.GetFiles(root.BundleRoot, "*.bundle"));

        var separate = plan with {
            SourceOnlyRequest = plan.SourceOnlyRequest with {
                WorkpackId = Guid.NewGuid(),
                ExpectedRefs = refs.Select((x, i) => i == 0
                    ? x with { ObjectId = new string('f', oidLength) }
                    : x).ToArray()
            }
        };
        var mismatched = new StubLaterCheckpointBundlePreflight(separate);
        var refused = new GitIncrementalLaterCheckpointLocalBundleAcquisition(
            new(true), mismatched, root.Acquirer,
            new LocalGitBundleArtifactStore(root.BundleRoot),
            TimeProvider.System);
        var error = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            refused.RehearseAsync(separate.PreparationId, separate.PlanId,
                separate.CheckpointId, separate.TargetObservationId,
                separate.SourceOnlyRequest.WorkpackId,
                separate.SourceOnlyRequest.SourceRepositoryId,
                separate.TargetReceiptSha256, default));
        Assert.Equal("BundleSourceRefMismatch", error.Code);
        Assert.Equal(1, mismatched.Calls);
        Assert.Single(Directory.GetFiles(root.BundleRoot, "*.bundle"));

        var disabled = new StubLaterCheckpointBundlePreflight(plan);
        var safeDefault = new GitIncrementalLaterCheckpointLocalBundleAcquisition(
            new(false), disabled, root.Acquirer,
            new LocalGitBundleArtifactStore(root.BundleRoot),
            TimeProvider.System);
        var closed = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            safeDefault.RehearseAsync(plan.PreparationId, plan.PlanId,
                plan.CheckpointId, plan.TargetObservationId,
                plan.SourceOnlyRequest.WorkpackId,
                plan.SourceOnlyRequest.SourceRepositoryId,
                plan.TargetReceiptSha256, default));
        Assert.Equal("LaterCheckpointLocalBundleDisabled", closed.Code);
        Assert.Equal(0, disabled.Calls);
    }

    private static GitIncrementalLaterCheckpointBundlePlan RehearsalPlan(
        LocalFixture root, IReadOnlyList<GitMirrorRef> refs)
    {
        var baseline = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        return new(1, Guid.NewGuid(), Guid.NewGuid(),
            baseline, snapshot, Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64),
            root.Request(refs), new string('e', 64),
            false, false, false, false, false);
    }

    // Stub is only a test boundary. Production resolves independently
    // verified PostgreSQL F-091/F-097b/F-031/F-038v2 evidence.
    private sealed class StubLaterCheckpointBundlePreflight(
        GitIncrementalLaterCheckpointBundlePlan plan)
        : IGitIncrementalLaterCheckpointBundleReadPreflight
    {
        public int Calls { get; private set; }
        public bool RejectSecond { get; set; }
        public Task<GitIncrementalLaterCheckpointBundlePlan> ReadAsync(
            Guid preparationId, Guid planId, Guid checkpointId,
            Guid targetObservationId, Guid workpackId, string sourceRepositoryId,
            string expectedTargetReceiptSha256, CancellationToken ct)
        {
            ++Calls;
            if (RejectSecond && Calls == 2)
                throw new GitSourceBundleException(
                    "FakeLaterCheckpointApprovalRevoked");
            return Task.FromResult(plan);
        }
    }

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
            ProvisionBareMirror(int oidLength = 40)
        {
            var work = Path.Combine(Temp, "working");
            Directory.CreateDirectory(work);
            if (oidLength == 64)
                RunGit("init", "--object-format=sha256", "-q", work);
            else
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
