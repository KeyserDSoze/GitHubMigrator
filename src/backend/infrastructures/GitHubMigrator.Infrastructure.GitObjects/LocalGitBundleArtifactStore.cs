using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// Filesystem boundary of F-038. The store never deletes a .bundle or
// accepts an arbitrary path. All inputs are server-derived workpack IDs.
public sealed class LocalGitBundleArtifactStore(
    string bundleRoot, long maxStorageBytes = 4L * 1024 * 1024 * 1024,
    TimeProvider? clock = null) : IGitSourceBundleArtifactVerifier
{
    public const long DefaultMaxStorageBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxCleanupFiles = 100;
    public static readonly TimeSpan MinimumCleanupAge = TimeSpan.FromHours(24);

    private static readonly Regex TempPattern = new(
        "^[0-9a-f]{32}[.][0-9a-f]{32}[.]tmp$", RegexOptions.Compiled);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Root
    {
        get
        {
            EnsureSafeRoot();
            return Path.GetFullPath(bundleRoot);
        }
    }

    public async Task<GitSourceBundleStorageCheck> VerifyAsync(
        GitSourceBundleView evidence, CancellationToken ct)
    {
        if (evidence is null || evidence.WorkpackId == Guid.Empty ||
            evidence.BundleFileName != evidence.WorkpackId.ToString("N") + ".bundle" ||
            !GitSourceBundleStorageCheck.ValidSha(evidence.BundleSha256) ||
            evidence.BundleBytes is < 1 or > LocalGitSourceBundleAcquirer.MaxBundleBytes ||
            evidence.ReadyForExecution)
            throw new GitSourceBundleException("BundleStorageProofInvalid");

        var path = Path.Combine(Root, evidence.BundleFileName);
        var measured = await VerifyFileAsync(path, evidence.BundleBytes,
            evidence.BundleSha256, ct);
        return new(evidence.WorkpackId, measured, evidence.BundleBytes,
            _clock.GetUtcNow(), evidence.IsCurrent, false);
    }

    public async Task<(string Sha256, long Bytes)> InspectExistingAsync(
        Guid workpackId, CancellationToken ct)
    {
        if (workpackId == Guid.Empty)
            throw new GitSourceBundleException("BundleStorageProofInvalid");
        var path = Path.Combine(Root, workpackId.ToString("N") + ".bundle");
        if (!File.Exists(path))
            throw new GitSourceBundleException("BundleStoredFileMissing");
        AssertNotSymlink(path);
        var size = new FileInfo(path).Length;
        if (size is < 1 or > LocalGitSourceBundleAcquirer.MaxBundleBytes)
            throw new GitSourceBundleException("BundleStoredSizeMismatch");
        var digest = await HashAsync(path, size, ct);
        return (digest, size);
    }

    // Serialize writers across processes sharing a POSIX-lock-capable volume.
    // The outer Git export retains this lease until after its final move.
    public async Task<FileStream> LockWritesAsync(CancellationToken ct)
    {
        var lockPath = Path.Combine(Root, ".git-bundle-writes.lock");
        AssertNotSymlink(lockPath);
        FileStream handle;
        try
        {
            handle = new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException) { throw new GitSourceBundleException("BundleQuotaLockUnavailable"); }
        catch (UnauthorizedAccessException) { throw new GitSourceBundleException("BundleQuotaLockUnavailable"); }
        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    handle.Lock(0, 1);
                    return handle;
                }
                catch (IOException) when (attempt < 29)
                { await Task.Delay(100, ct); }
            }
            throw new GitSourceBundleException("BundleQuotaLockUnavailable");
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }
    }

    public void AssertWithinQuota()
    {
        if (maxStorageBytes < 1 || maxStorageBytes > 1L << 40)
            throw new GitSourceBundleException("BundleStoreQuotaInvalid");

        long used = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFileSystemEntries(Root,
            "*", SearchOption.TopDirectoryOnly))
        {
            if (++count > 100_000)
                throw new GitSourceBundleException("BundleStoreEntryLimit");
            // Count unknown files too: storage budgets must not ignore
            // files an operator has put on the same volume.
            AssertNotSymlink(file);
            if (Directory.Exists(file))
                throw new GitSourceBundleException("BundleStoreUnexpectedDirectory");
            if (Path.GetFileName(file) == ".git-bundle-writes.lock") continue;
            checked { used += new FileInfo(file).Length; }
            if (used > maxStorageBytes)
                throw new GitSourceBundleException("BundleStoreQuotaExceeded");
        }
    }

    // Deletes ONLY expired random staging paths, never evidence-backed bundles
    // or unrecognized files. Not exposed as a public API or scheduled GC.
    public async Task<int> CleanupStaleTempsAsync(
        TimeSpan minimumAge, CancellationToken ct)
    {
        if (minimumAge < MinimumCleanupAge)
            throw new GitSourceBundleException("BundleCleanupAgeTooShort");

        await using var lease = await LockWritesAsync(ct);
        var cutoff = _clock.GetUtcNow().UtcDateTime - minimumAge;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "*.tmp",
            SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (removed >= MaxCleanupFiles) break;
            if (!TempPattern.IsMatch(Path.GetFileName(file))) continue;
            AssertNotSymlink(file);
            if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
            try { File.Delete(file); removed++; }
            catch (IOException) { throw new GitSourceBundleException("BundleCleanupIoFailure"); }
        }
        return removed;
    }

    private async Task<string> VerifyFileAsync(
        string path, long expectedBytes, string expectedSha, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new GitSourceBundleException("BundleStoredFileMissing");
        AssertNotSymlink(path);
        if (new FileInfo(path).Length != expectedBytes)
            throw new GitSourceBundleException("BundleStoredSizeMismatch");
        var actual = await HashAsync(path, expectedBytes, ct);
        if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
            throw new GitSourceBundleException("BundleStoredDigestMismatch");
        return actual;
    }

    private static async Task<string> HashAsync(
        string path, long expectedBytes, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedBytes)
                throw new GitSourceBundleException("BundleStoredSizeMismatch");
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (stream.Position != expectedBytes)
                throw new GitSourceBundleException("BundleStoredSizeMismatch");
            return digest;
        }
        catch (IOException) { throw new GitSourceBundleException("BundleStoredReadFailure"); }
        catch (UnauthorizedAccessException)
        { throw new GitSourceBundleException("BundleStoredReadFailure"); }
    }

    private void EnsureSafeRoot()
    {
        if (string.IsNullOrWhiteSpace(bundleRoot) ||
            !Path.IsPathFullyQualified(bundleRoot) ||
            !Directory.Exists(bundleRoot))
            throw new GitSourceBundleException("LocalBundleStoreNotConfigured");
        for (var dir = new DirectoryInfo(Path.GetFullPath(bundleRoot));
            dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.LinkTarget is not null)
                throw new GitSourceBundleException("LocalBundleSymlinkBlocked");
    }

    private static void AssertNotSymlink(string file)
    {
        if (File.Exists(file) || Directory.Exists(file))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new GitSourceBundleException("LocalBundleSymlinkBlocked");
        }
    }
}
