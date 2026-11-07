using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-060. The ONLY imported source is the immutable F-038 local bundle.
// All writes are confined to a random temporary bare repo beneath a
// trusted local root; the atomically promoted mirror is verification-only.
// No GitHub transport, credentials, API route or remote push is involved.
public sealed class LocalGitSourceSnapshotMirrorMaterializer(
    string bundleRoot, string mirrorRoot,
    IGitSourceBundleArtifactVerifier bundles,
    bool enabled = false, TimeProvider? clock = null)
    : IGitSourceSnapshotMirrorMaterializer
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(90);

    public async Task<GitSourceSnapshotMirrorProof> MaterializeAsync(
        GitHubTrustedRemoteReadPlan plan, CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException("SourceMirrorDisabled");
        GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
        var verifiedBundle = plan.SourceBundle!;
        var source = new LocalGitBundleArtifactStore(bundleRoot);
        var root = source.Root; // Rechecks absolute root and symlink ancestry.
        var targetRoot = CheckMirrorRoot(mirrorRoot);
        if (Path.GetFullPath(root) == Path.GetFullPath(targetRoot))
            throw new GitSourceSnapshotMirrorException("SourceMirrorRootsOverlap");

        var final = Path.Combine(targetRoot,
            plan.SourceMirrorId.ToString("N") + ".git");
        var temp = Path.Combine(targetRoot,
            plan.SourceMirrorId.ToString("N") + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        await ProcessGate.WaitAsync(ct);
        try
        {
            // File-system advisory lock coordinates separate worker
            // processes sharing the same POSIX-lock capable volume.
            await using var crossProcess = await AcquireRootLockAsync(
                targetRoot, ct);
            await VerifyBundleAsync(bundles, verifiedBundle, ct);

            (int Count, string Digest) measured;
            if (Directory.Exists(final) || File.Exists(final))
            {
                if (!Directory.Exists(final))
                    throw new GitSourceSnapshotMirrorException(
                        "SourceMirrorExistingEntryInvalid");
                measured = await VerifySourceAsync(
                    targetRoot, plan, ct);
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(temp);
                    var format = plan.SourceRefs[0].ObjectId.Length == 40 ?
                        "sha1" : "sha256";
                    await GitAsync(temp, ct,
                        "init", "--bare", "--quiet",
                        "--object-format=" + format);
                    var path = Path.Combine(root, verifiedBundle.BundleFileName);

                    // Never reuse ambient Git config or a remote URI.
                    // Only locally verified bundle bytes supply objects.
                    await GitAsync(temp, ct,
                        "fetch", "--quiet", "--no-tags",
                        "--no-write-fetch-head", "--no-auto-maintenance",
                        "--no-recurse-submodules", path,
                        "refs/heads/*:refs/heads/*",
                        "refs/tags/*:refs/tags/*");
                    measured = await VerifyOneAsync(
                        targetRoot, temp, plan.SourceRefs, ct);
                    await VerifyBundleAsync(bundles, verifiedBundle, ct);

                    // Atomic same-volume promotion; never overwrite an
                    // existing or possibly corrupted proof directory.
                    if (Directory.Exists(final) || File.Exists(final))
                        throw new GitSourceSnapshotMirrorException(
                            "SourceMirrorExistingEntryInvalid");
                    Directory.Move(temp, final);
                    var promoted = await VerifySourceAsync(
                        targetRoot, plan, ct);
                    if (measured != promoted)
                        throw new GitSourceSnapshotMirrorException(
                            "SourceMirrorChangedDuringPromotion");
                    measured = promoted;
                }
                finally
                {
                    // No recognized final path is deleted even if its
                    // metadata is corrupt: only this random temp is owned.
                    if (Directory.Exists(temp) &&
                        new DirectoryInfo(temp).LinkTarget is null)
                    {
                        try { Directory.Delete(temp, recursive: true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            await VerifyBundleAsync(bundles, verifiedBundle, ct);
            return GitSourceSnapshotMirrorIntegrity.Seal(
                plan, measured.Digest, measured.Count, _clock.GetUtcNow());
        }
        catch (GitSourceBundleException)
        {
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorBundleInvalid");
        }
        catch (GitObjectClosureAuditException)
        {
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorGitClosureInvalid");
        }
        catch (IOException)
        {
            throw new GitSourceSnapshotMirrorException("SourceMirrorLocalIoFailure");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitSourceSnapshotMirrorException("SourceMirrorLocalIoFailure");
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    // F-061: explicit operator-only orphan cleanup, NOT a scheduled GC.
    // No persisted final .git is ever removed or overwritten, including
    // an unrecognized bare repository from an interrupted rename.
    public async Task<int> CleanupStaleStagingAsync(
        TimeSpan minimumAge, CancellationToken ct)
    {
        if (!enabled)
            throw new GitSourceSnapshotMirrorException("SourceMirrorDisabled");
        if (minimumAge < TimeSpan.FromHours(24))
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorCleanupAgeTooShort");
        var root = CheckMirrorRoot(mirrorRoot);
        await ProcessGate.WaitAsync(ct);
        try
        {
            await using var guard = await AcquireRootLockAsync(root, ct);
            var cutoff = _clock.GetUtcNow().UtcDateTime - minimumAge;
            var cleaned = 0;
            var inspected = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(
                root, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (++inspected > 5000 || cleaned >= 40) break;
                var name = Path.GetFileName(path);
                // <deterministic-mirror-guid>.<random-uuid>.tmp only.
                if (name.Length != 73 ||
                    name[32] != '.' || !name.EndsWith(".tmp",
                        StringComparison.Ordinal) ||
                    name.AsSpan(0, 32).ToString().Any(c =>
                        c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                    name.AsSpan(33, 32).ToString().Any(c =>
                        c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    continue;
                var info = new DirectoryInfo(path);
                if (!info.Exists || info.LinkTarget is not null ||
                    (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new GitSourceSnapshotMirrorException(
                        "SourceMirrorCleanupUnsafeEntry");
                if (info.LastWriteTimeUtc > cutoff) continue;

                var pending = new Stack<DirectoryInfo>();
                pending.Push(info);
                var count = 0;
                while (pending.TryPop(out var dir))
                {
                    foreach (var entry in dir.EnumerateFileSystemInfos())
                    {
                        if (++count > 450_000 || entry.LinkTarget is not null ||
                            (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new GitSourceSnapshotMirrorException(
                                "SourceMirrorCleanupUnsafeEntry");
                        if (entry is DirectoryInfo nested) pending.Push(nested);
                    }
                }
                Directory.Delete(path, recursive: true);
                cleaned++;
            }
            return cleaned;
        }
        finally { ProcessGate.Release(); }
    }

    private static async Task<(int Count, string Digest)> VerifySourceAsync(
        string root, GitHubTrustedRemoteReadPlan plan, CancellationToken ct) =>
        await LocalGitObjectClosureAuditor.VerifyOneAsync(
            root, plan.SourceMirrorId, plan.SourceRefs, ct);

    private static async Task<(int Count, string Digest)> VerifyOneAsync(
        string root, string temp,
        IReadOnlyList<GitMirrorRef> expected, CancellationToken ct)
    {
        // F-056 accepts only UUID.git handles. During materialization the
        // randomized staging directory is first *renamed* into a private
        // verification directory that still uses the same UUID shape.
        // Never expose this path or any file handle outside the lock.
        var scratchId = Guid.NewGuid();
        var scratch = Path.Combine(root, scratchId.ToString("N") + ".git");
        Directory.Move(temp, scratch);
        try
        {
            return await LocalGitObjectClosureAuditor.VerifyOneAsync(
                root, scratchId, expected, ct);
        }
        finally
        {
            // This staging proof is itself a temporary repository and
            // must not persist after an unsuccessful import.
            if (Directory.Exists(scratch))
            {
                try { Directory.Move(scratch, temp); }
                catch (IOException)
                {
                    // Error handling remains fail-closed: never promote
                    // the target if staging cannot be restored.
                    throw new GitSourceSnapshotMirrorException(
                        "SourceMirrorStagingRestoreFailed");
                }
            }
        }
    }

    private static async Task VerifyBundleAsync(
        IGitSourceBundleArtifactVerifier verifier,
        GitSourceBundleView evidence, CancellationToken ct)
    {
        var check = await verifier.VerifyAsync(evidence, ct);
        if (!check.IsCurrent || check.ReadyForExecution ||
            check.WorkpackId != evidence.WorkpackId ||
            check.BundleSha256 != evidence.BundleSha256 ||
            check.BundleBytes != evidence.BundleBytes)
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorBundleChanged");
    }

    private static string CheckMirrorRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(root) ||
            !Directory.Exists(root))
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorRootInvalid");
        var path = Path.GetFullPath(root);
        for (var cursor = new DirectoryInfo(path);
            cursor is not null; cursor = cursor.Parent)
        {
            if (cursor.LinkTarget is not null ||
                (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new GitSourceSnapshotMirrorException(
                    "SourceMirrorSymlinkBlocked");
        }
        return path;
    }

    private static async Task<FileStream> AcquireRootLockAsync(
        string root, CancellationToken ct)
    {
        var path = Path.Combine(root, ".f060-source-materialization.lock");
        if (File.Exists(path) &&
            (new FileInfo(path).LinkTarget is not null ||
             (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorSymlinkBlocked");
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorLockUnavailable");
        }
        try
        {
            for (var attempt = 0; attempt < 180; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    stream.Lock(0, 1);
                    return stream;
                }
                catch (IOException) when (attempt < 179)
                {
                    await Task.Delay(100, ct);
                }
            }
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorLockUnavailable");
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task GitAsync(
        string barePath, CancellationToken external, params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(external);
        timeout.CancelAfter(GitTimeout);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/git",
            WorkingDirectory = barePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        var env = process.StartInfo.Environment;
        env.Clear();
        env["PATH"] = "/usr/bin:/bin";
        env["HOME"] = "/nonexistent";
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_GLOBAL"] = "/dev/null";
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GIT_OPTIONAL_LOCKS"] = "0";
        env["GIT_NO_REPLACE_OBJECTS"] = "1";
        env["GIT_NO_LAZY_FETCH"] = "1";
        env["GIT_ALLOW_PROTOCOL"] = "file";
        env["GIT_CONFIG_COUNT"] = "3";
        env["GIT_CONFIG_KEY_0"] = "protocol.allow";
        env["GIT_CONFIG_VALUE_0"] = "file";
        env["GIT_CONFIG_KEY_1"] = "core.hooksPath";
        env["GIT_CONFIG_VALUE_1"] = "/dev/null";
        env["GIT_CONFIG_KEY_2"] = "credential.helper";
        env["GIT_CONFIG_VALUE_2"] = "";
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        var started = false;
        try
        {
            if (!process.Start())
                throw new GitSourceSnapshotMirrorException(
                    "SourceMirrorGitUnavailable");
            started = true;
            var stdout = ReadLimitedAsync(process.StandardOutput,
                128 * 1024, timeout.Token);
            var stderr = ReadLimitedAsync(process.StandardError,
                128 * 1024, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            _ = await stderr;
            if (process.ExitCode != 0)
                throw new GitSourceSnapshotMirrorException(
                    "SourceMirrorGitReadRejected");
        }
        catch (OperationCanceledException) when (!external.IsCancellationRequested)
        {
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorGitDeadline");
        }
        catch (Win32Exception)
        {
            throw new GitSourceSnapshotMirrorException(
                "SourceMirrorGitUnavailable");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader source, int limit, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var chars = await source.ReadAsync(buffer.AsMemory(), ct);
            if (chars == 0) return result.ToString();
            if (chars > limit - result.Length)
                throw new GitSourceSnapshotMirrorException(
                    "SourceMirrorGitOutputLimit");
            result.Append(buffer, 0, chars);
        }
    }
}
