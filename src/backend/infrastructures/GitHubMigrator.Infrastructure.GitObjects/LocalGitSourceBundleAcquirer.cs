using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// Deliberately local-only. This adapter consumes an operator-provisioned
// bare mirror; it neither knows an Azure/GitHub URL nor accepts one from HTTP.
// Git is invoked without a shell, inherited environment, hooks or networking.
public sealed class LocalGitSourceBundleAcquirer(
    string sourceRoot, string bundleRoot, TimeProvider? clock = null,
    long maxStoreBytes = LocalGitBundleArtifactStore.DefaultMaxStorageBytes)
    : IGitSourceBundleAcquirer
{
    public const long MaxBundleBytes = 512L * 1024 * 1024;
    private const int MaxGitOutputChars = 16 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(65);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string LocalMirrorDirectory(string trustedSourceRoot,
        Guid connectionId, string repositoryId)
    {
        if (connectionId == Guid.Empty || string.IsNullOrWhiteSpace(repositoryId) ||
            repositoryId.Length > 256 || !Path.IsPathFullyQualified(trustedSourceRoot))
            throw new GitSourceBundleException("InvalidLocalMirrorMapping");
        var key = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId)));
        return Path.Combine(Path.GetFullPath(trustedSourceRoot),
            connectionId.ToString("N"), key + ".git");
    }

    public async Task<GitSourceBundleArtifact> AcquireAsync(
        GitSourceBundleRequest request, CancellationToken ct)
    {
        if (request is null || request.WorkpackId == Guid.Empty ||
            request.SourceConnectionId == Guid.Empty ||
            !ValidSha(request.ReconciliationSha256) ||
            !ValidSha(request.SourceEvidenceSha256))
            throw new GitSourceBundleException("InvalidBundleRequest");

        IReadOnlyList<GitMirrorRef> expected;
        try
        {
            expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                request.ExpectedRefs);
        }
        catch (GitMirrorRefReconciliationException)
        { throw new GitSourceBundleException("InvalidBundleRefs"); }
        if (expected.Count == 0)
            throw new GitSourceBundleException("EmptyBundleRefsUnsupported");

        ValidateRoot(sourceRoot, mustExist: true);
        ValidateRoot(bundleRoot, mustExist: true);
        var repo = LocalMirrorDirectory(
            sourceRoot, request.SourceConnectionId, request.SourceRepositoryId);
        AssertNoSymlinkAncestry(repo);
        if (!Directory.Exists(repo) || !File.Exists(Path.Combine(repo, "HEAD")) ||
            !Directory.Exists(Path.Combine(repo, "objects")) ||
            !Directory.Exists(Path.Combine(repo, "refs")) ||
            File.Exists(Path.Combine(repo, "objects", "info", "alternates")))
            throw new GitSourceBundleException("BareMirrorUnavailable");
        foreach (var segment in new[] {
            Path.Combine(repo, "objects"), Path.Combine(repo, "objects", "pack"),
            Path.Combine(repo, "refs"), Path.Combine(repo, "objects", "info")
        })
            if (Directory.Exists(segment)) AssertNoSymlinkAncestry(segment);

        // Read ALL refs, not only the expected ones, so hidden refs/notes or
        // unverified source branches cannot enter a --all bundle silently.
        var observed = await RunGitAsync(repo, null, ct,
            "for-each-ref", "--format=%(refname)%09%(objectname)");
        var actual = CanonicalizeRefLines(observed, tabDelimited: true, skipHead: false);
        if (!actual.SequenceEqual(expected))
            throw new GitSourceBundleException("BundleSourceRefMismatch");

        await RunGitAsync(repo, null, ct,
            "fsck", "--full", "--strict", "--no-reflogs", "--no-progress");

        var temp = Path.Combine(bundleRoot,
            request.WorkpackId.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var final = Path.Combine(bundleRoot,
            request.WorkpackId.ToString("N") + ".bundle");
        var storage = new LocalGitBundleArtifactStore(
            bundleRoot, maxStoreBytes, _clock);
        await using var writeLock = await storage.LockWritesAsync(ct);
        storage.AssertWithinQuota();

        // A crash may leave an atomically promoted bundle without its DB
        // checkpoint. Validate the COMPLETE object/ref proof before safely
        // recovering; never overwrite the existing file.
        if (File.Exists(final))
        {
            var recovered = await storage.InspectExistingAsync(
                request.WorkpackId, ct);
            await CheckBundleRefsAsync(repo, final, expected, ct);
            return new(request.WorkpackId, request.SourceEvidenceSha256,
                Path.GetFileName(final), recovered.Sha256, recovered.Bytes,
                expected.Count, _clock.GetUtcNow());
        }
        try
        {
            await RunGitAsync(repo, temp, ct,
                "bundle", "create", temp, "--all");
            if (!File.Exists(temp))
                throw new GitSourceBundleException("BundleOutputMissing");
            var info = new FileInfo(temp);
            if (info.Length is <= 0 or > MaxBundleBytes)
                throw new GitSourceBundleException("BundleSizeLimitExceeded");

            await CheckBundleRefsAsync(repo, temp, expected, ct);
            storage.AssertWithinQuota();

            // One hash over the complete, verified binary bundle; no pack
            // contents, credentials or local paths enter API/DB responses.
            await using var file = new FileStream(temp, FileMode.Open,
                FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(file, ct));
            // Exclusive, atomic promotion; never overwrite older evidence.
            if (File.Exists(final))
                throw new GitSourceBundleException("BundleAlreadyExists");
            File.Move(temp, final);
            return new(request.WorkpackId, request.SourceEvidenceSha256,
                Path.GetFileName(final), sha, info.Length, expected.Count,
                _clock.GetUtcNow());
        }
        catch (OperationCanceledException) { throw; }
        catch (GitSourceBundleException) { throw; }
        catch (IOException) { throw new GitSourceBundleException("BundleLocalIoFailure"); }
        catch (UnauthorizedAccessException)
        { throw new GitSourceBundleException("BundleLocalIoFailure"); }
        finally
        {
            // Aborted exports do not leave partially consumable .bundle files.
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static async Task CheckBundleRefsAsync(
        string repo, string bundle, IReadOnlyList<GitMirrorRef> expected,
        CancellationToken ct)
    {
        await RunGitAsync(repo, null, ct, "bundle", "verify", bundle);
        var heads = await RunGitAsync(repo, null, ct,
            "bundle", "list-heads", bundle);
        var actual = CanonicalizeRefLines(
            heads, tabDelimited: false, skipHead: true);
        if (!actual.SequenceEqual(expected))
            throw new GitSourceBundleException("BundleHeadMismatch");
    }

    private static bool ValidSha(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateRoot(string path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            mustExist && !Directory.Exists(path))
            throw new GitSourceBundleException("LocalBundleStoreNotConfigured");
        AssertNoSymlinkAncestry(path);
    }

    private static void AssertNoSymlinkAncestry(string path)
    {
        for (var cursor = new DirectoryInfo(Path.GetFullPath(path));
             cursor is not null; cursor = cursor.Parent)
        {
            if (cursor.Exists && cursor.LinkTarget is not null)
                throw new GitSourceBundleException("LocalBundleSymlinkBlocked");
        }
    }

    private static IReadOnlyList<GitMirrorRef> CanonicalizeRefLines(
        string output, bool tabDelimited, bool skipHead)
    {
        var refs = new List<GitMirrorRef>();
        foreach (var line in output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            var separator = tabDelimited ? trimmed.IndexOf('\t') :
                trimmed.IndexOf(' ');
            if (separator <= 0 || separator >= trimmed.Length - 1)
                throw new GitSourceBundleException("BundleRefOutputInvalid");
            var name = tabDelimited ? trimmed[..separator] :
                trimmed[(separator + 1)..];
            var oid = tabDelimited ? trimmed[(separator + 1)..] :
                trimmed[..separator];
            if (skipHead && name == "HEAD") continue;
            refs.Add(new(name, oid));
            if (refs.Count > GitMirrorRefReconciler.MaxRefsPerSide)
                throw new GitSourceBundleException("BundleRefLimitExceeded");
        }
        try { return GitMirrorRefReconciler.CanonicalizeObservedRefs(refs); }
        catch (GitMirrorRefReconciliationException)
        { throw new GitSourceBundleException("BundleUnexpectedRef"); }
    }

    private static async Task<string> RunGitAsync(
        string bareRepo, string? outputFile, CancellationToken cancellation,
        params string[] arguments)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(CommandTimeout);
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo {
            FileName = "/usr/bin/git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = bareRepo
        };
        proc.StartInfo.Environment.Clear();
        proc.StartInfo.Environment["PATH"] = "/usr/bin:/bin";
        proc.StartInfo.Environment["HOME"] = "/nonexistent";
        proc.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        proc.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        proc.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        proc.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        proc.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        proc.StartInfo.Environment["GIT_CONFIG_COUNT"] = "2";
        proc.StartInfo.Environment["GIT_CONFIG_KEY_0"] = "protocol.allow";
        proc.StartInfo.Environment["GIT_CONFIG_VALUE_0"] = "never";
        proc.StartInfo.Environment["GIT_CONFIG_KEY_1"] = "core.hooksPath";
        proc.StartInfo.Environment["GIT_CONFIG_VALUE_1"] = "/dev/null";

        proc.StartInfo.ArgumentList.Add("--git-dir=" + bareRepo);
        foreach (var argument in arguments)
            proc.StartInfo.ArgumentList.Add(argument);
        var started = false;
        try
        {
            if (!proc.Start())
                throw new GitSourceBundleException("GitProcessUnavailable");
            started = true;
            var stdout = ReadBoundedAsync(proc.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(proc.StandardError, deadline.Token);
            while (!proc.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (outputFile is not null && File.Exists(outputFile) &&
                    new FileInfo(outputFile).Length > MaxBundleBytes)
                    throw new GitSourceBundleException("BundleSizeLimitExceeded");
                await Task.Delay(70, deadline.Token);
            }
            await proc.WaitForExitAsync(deadline.Token);
            var text = await stdout;
            _ = await stderr; // Never publish/log Git stderr (may contain paths).
            if (proc.ExitCode != 0)
                throw new GitSourceBundleException("GitObjectVerificationFailed");
            return text;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new GitSourceBundleException("BundleAcquisitionDeadline"); }
        catch (System.ComponentModel.Win32Exception)
        { throw new GitSourceBundleException("GitProcessUnavailable"); }
        finally
        {
            if (started && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return result.ToString();
            if (result.Length + count > MaxGitOutputChars)
                throw new GitSourceBundleException("GitOutputLimitExceeded");
            result.Append(buffer, 0, count);
        }
    }
}
