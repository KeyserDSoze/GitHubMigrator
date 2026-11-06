using System.Diagnostics;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-048 local Git transport implementation. A destination is derived ONLY
// from a server-minted SandboxId under a trusted pre-existing root.
// No URL, credential, remote Git protocol, shell, system configuration,
// external filesystem path, or GitHub API client can enter this adapter.
public sealed class LocalGitCreateOnlyTransferRunner(
    string bundleRoot, string sandboxRoot, string scratchRoot,
    bool enabled = false, TimeProvider? clock = null,
    Func<CancellationToken, Task>? testAfterPush = null)
    : ILocalGitCreateOnlyTransferRunner
{
    private const int MaxGitOutput = 2 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(55);
    private static readonly SemaphoreSlim Gate = new(2, 2);
    // Linux fcntl byte-range locks are process-scoped: two tasks inside one
    // .NET replica could both acquire the SAME file lock. A bounded striped
    // in-process gate fixes this without retaining one semaphore per repo.
    // The OS file lock still coordinates separate replicas/processes.
    private static readonly SemaphoreSlim[] TargetStripes =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim Stripe(Guid sandboxId) =>
        TargetStripes[(int)((uint)sandboxId.GetHashCode() %
            (uint)TargetStripes.Length)];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<LocalGitCreateOnlyTransferReceipt> RunAsync(
        LocalGitCreateOnlyTransferRequest request, CancellationToken ct)
    {
        EnsureEnabled();
        LocalGitCreateOnlyTransferIntegrity.Verify(request);
        var paths = PathsFor(request.SandboxId);
        var verifier = new LocalGitBundleArtifactStore(bundleRoot);
        var physical = await verifier.VerifyAsync(request.Bundle, ct);
        if (!physical.IsCurrent ||
            physical.BundleSha256 != request.Packet.BundleSha256)
            throw new LocalGitCreateOnlyTransferException("SandboxBundleChanged");

        await Gate.WaitAsync(ct);
        var stripe = Stripe(request.SandboxId);
        var stripeHeld = false;
        try
        {
            await stripe.WaitAsync(ct);
            stripeHeld = true;
            await using var targetLock = await LockTargetAsync(paths.Lock, ct);
            // Revalidate root, repo and symlink boundaries AFTER waiting.
            paths = PathsFor(request.SandboxId);
            var working = Path.Combine(paths.Scratch,
                request.SandboxId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Launch Git only from our fresh private working directory,
                // identical to the already tested F-041 process boundary.
                Directory.CreateDirectory(working);
                if (!Directory.Exists(working))
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxScratchCreationFailed");
                await AssertBareAndFormatAsync(paths.Destination,
                    working,
                    request.SourceRefs[0].ObjectId.Length == 40 ? "sha1" : "sha256",
                    ct);
                var source = Path.Combine(working, "source.git");
                var bundleFile = Path.Combine(verifier.Root,
                    request.Bundle.BundleFileName);
                await GitAsync(working, ct, "clone", "--mirror",
                    "--quiet", bundleFile, source);
                await GitAsync(working, ct, "--git-dir=" + source, "fsck",
                    "--full", "--strict", "--no-reflogs", "--no-progress");
                var physicalSource = await ReadRefsAsync(source, working, ct);
                if (!physicalSource.SequenceEqual(request.SourceRefs))
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxBundleSourceRefsMismatch");

                var before = await ReadRefsAsync(paths.Destination,
                    working, ct);
                if (!before.SequenceEqual(request.ExpectedTargetRefs))
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxBaselineChanged");

                var args = new List<string> {
                    "--git-dir=" + source, "push", "--porcelain", "--atomic"
                };
                foreach (var item in request.CreateRefs)
                    args.Add("--force-with-lease=" + item.Name + ":");
                args.Add(paths.Destination);
                foreach (var item in request.CreateRefs)
                    args.Add(item.ObjectId + ":" + item.Name);

                // After this statement, any connection error, process crash
                // or lost acknowledgement is an ambiguous result. NEVER
                // retry the push. Independently call InspectAsync instead.
                try
                {
                    var output = await GitAsync(working, ct, args.ToArray());
                    if (output.Split('\n').Any(x =>
                        x.StartsWith("=", StringComparison.Ordinal)))
                        throw new LocalGitCreateOnlyTransferException(
                            "SandboxPushWasNotCreateOnly");
                    if (testAfterPush is not null)
                        await testAfterPush(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxOutcomeRequiresReconciliation");
                }
                catch (LocalGitCreateOnlyTransferException)
                {
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxOutcomeRequiresReconciliation");
                }
                catch (Exception ex) when (
                    ex is IOException or InvalidOperationException or
                    OperationCanceledException)
                {
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxOutcomeRequiresReconciliation");
                }

                var after = await ReadRefsAsync(paths.Destination, working, ct);
                LocalGitCreateOnlyTransferReceipt receipt;
                try
                {
                    receipt = LocalGitCreateOnlyTransferIntegrity.Seal(
                        request, before, after, _clock.GetUtcNow());
                    await GitAsync(working, ct, "--git-dir=" + paths.Destination,
                        "fsck", "--full", "--strict", "--no-reflogs", "--no-progress");
                    var finalBundle = await verifier.VerifyAsync(request.Bundle, ct);
                    if (finalBundle.BundleSha256 != physical.BundleSha256 ||
                        finalBundle.BundleBytes != physical.BundleBytes)
                        throw new LocalGitCreateOnlyTransferException(
                            "SandboxBundleChanged");
                }
                catch (GitSourceBundleException)
                {
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxOutcomeRequiresReconciliation");
                }
                catch (LocalGitCreateOnlyTransferException)
                {
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxOutcomeRequiresReconciliation");
                }
                return receipt;
            }
            finally
            {
                try { Directory.Delete(working, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            if (stripeHeld) stripe.Release();
            Gate.Release();
        }
    }

    public async Task<LocalGitCreateOnlyTransferInspection> InspectAsync(
        LocalGitCreateOnlyTransferRequest request, CancellationToken ct)
    {
        EnsureEnabled();
        LocalGitCreateOnlyTransferIntegrity.Verify(request);
        var paths = PathsFor(request.SandboxId);
        await Gate.WaitAsync(ct);
        var stripe = Stripe(request.SandboxId);
        var stripeHeld = false;
        try
        {
            await stripe.WaitAsync(ct);
            stripeHeld = true;
            await using var targetLock = await LockTargetAsync(paths.Lock, ct);
            paths = PathsFor(request.SandboxId);
            await AssertBareAndFormatAsync(paths.Destination,
                paths.Scratch,
                request.SourceRefs[0].ObjectId.Length == 40 ? "sha1" : "sha256",
                ct);
            var actual = await ReadRefsAsync(
                paths.Destination, paths.Scratch, ct);
            // An observed ref SHA alone is insufficient: the target might
            // contain missing or corrupt objects after an uncertain send.
            // Inspection must verify object reachability without a push.
            await GitAsync(paths.Scratch, ct, "--git-dir=" + paths.Destination,
                "fsck", "--full", "--strict", "--no-reflogs", "--no-progress");
            return LocalGitCreateOnlyTransferIntegrity.Inspect(request, actual);
        }
        finally
        {
            if (stripeHeld) stripe.Release();
            Gate.Release();
        }
    }

    private void EnsureEnabled()
    {
        if (!enabled)
            throw new LocalGitCreateOnlyTransferException(
                "SandboxTransportDisabled");
    }

    private (string Root, string Scratch, string Destination, string Lock)
        PathsFor(Guid sandboxId)
    {
        if (sandboxId == Guid.Empty)
            throw new LocalGitCreateOnlyTransferException(
                "SandboxRequestInvalid");
        var root = ValidateRoot(sandboxRoot);
        var scratch = ValidateRoot(scratchRoot);
        if (root == scratch || root.StartsWith(scratch + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            scratch.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new LocalGitCreateOnlyTransferException("SandboxRootsOverlap");
        var key = sandboxId.ToString("N");
        var destination = Path.Combine(root, key + ".git");
        var lockFile = Path.Combine(root, key + ".transport.lock");
        CheckNoLinks(destination);
        CheckNoLinks(lockFile);
        if (!Directory.Exists(destination) ||
            !File.Exists(Path.Combine(destination, "HEAD")) ||
            !File.Exists(Path.Combine(destination, "config")) ||
            !Directory.Exists(Path.Combine(destination, "objects")) ||
            !Directory.Exists(Path.Combine(destination, "refs")) ||
            File.Exists(Path.Combine(destination, "objects", "info", "alternates")))
            throw new LocalGitCreateOnlyTransferException("SandboxBareRepoMissing");
        foreach (var name in new[] { "HEAD", "config", "packed-refs", "objects",
            "objects/info", "objects/pack", "refs", "hooks" })
            CheckNoLinks(Path.Combine(destination, name));
        CheckTreeNoLinks(destination);
        return (root, scratch, destination, lockFile);
    }

    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new LocalGitCreateOnlyTransferException("SandboxRootNotConfigured");
        var full = Path.GetFullPath(root);
        CheckNoLinks(full);
        return full;
    }

    private static void CheckNoLinks(string path)
    {
        for (var item = new FileInfo(Path.GetFullPath(path));
            item is not null; item = item.Directory is null ? null :
                new FileInfo(item.Directory.FullName))
        {
            if (File.Exists(item.FullName) || Directory.Exists(item.FullName))
            {
                if (File.GetAttributes(item.FullName)
                    .HasFlag(FileAttributes.ReparsePoint))
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxSymlinkBlocked");
            }
            if (item.Directory is null) break;
        }
    }

    private static void CheckTreeNoLinks(string root)
    {
        // Git can follow a symlink in refs/heads, refs/tags or objects even
        // when its outer bare directory is safe. Traverse manually: never
        // recurse through a link before evaluating its metadata. Bound work.
        var dirs = new Stack<(string Path, int Depth)>();
        dirs.Push((root, 0));
        var entries = 0;
        try
        {
            while (dirs.Count > 0)
            {
                var (folder, depth) = dirs.Pop();
                if (depth > 32)
                    throw new LocalGitCreateOnlyTransferException(
                        "SandboxTreeDepthExceeded");
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    if (++entries > 250_000)
                        throw new LocalGitCreateOnlyTransferException(
                            "SandboxTreeEntryLimit");
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new LocalGitCreateOnlyTransferException(
                            "SandboxSymlinkBlocked");
                    if (attributes.HasFlag(FileAttributes.Directory))
                        dirs.Push((entry, depth + 1));
                }
            }
        }
        catch (IOException)
        { throw new LocalGitCreateOnlyTransferException("SandboxTreeUnreadable"); }
        catch (UnauthorizedAccessException)
        { throw new LocalGitCreateOnlyTransferException("SandboxTreeUnreadable"); }
    }

    private static async Task<FileStream> LockTargetAsync(
        string path, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        { throw new LocalGitCreateOnlyTransferException("SandboxTargetLockUnavailable"); }
        try
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    file.Lock(0, 1);
                    return file;
                }
                catch (IOException) when (attempt < 199)
                { await Task.Delay(20, ct); }
            }
            throw new LocalGitCreateOnlyTransferException(
                "SandboxTargetLockUnavailable");
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertBareAndFormatAsync(
        string destination, string working, string expectedFormat,
        CancellationToken ct)
    {
        var bare = await GitAsync(working, ct, "--git-dir=" + destination,
            "rev-parse", "--is-bare-repository");
        var format = await GitAsync(working, ct, "--git-dir=" + destination,
            "rev-parse", "--show-object-format");
        if (bare.Trim() != "true" || format.Trim() != expectedFormat)
            throw new LocalGitCreateOnlyTransferException(
                "SandboxObjectFormatMismatch");
    }

    private static async Task<IReadOnlyList<GitMirrorRef>> ReadRefsAsync(
        string target, string working, CancellationToken ct)
    {
        var text = await GitAsync(working, ct, "--git-dir=" + target,
            "for-each-ref", "--format=%(refname)%09%(objectname)");
        var refs = new List<GitMirrorRef>();
        foreach (var row in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = row.TrimEnd('\r').Split('\t');
            if (fields.Length != 2)
                throw new LocalGitCreateOnlyTransferException(
                    "SandboxRemoteRefOutputInvalid");
            refs.Add(new(fields[0], fields[1]));
        }
        try { return GitMirrorRefReconciler.CanonicalizeObservedRefs(refs); }
        catch (GitMirrorRefReconciliationException)
        { throw new LocalGitCreateOnlyTransferException(
            "SandboxRemoteRefOutputInvalid"); }
    }

    private static async Task<string> GitAsync(
        string work, CancellationToken external, params string[] args)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(external);
        deadline.CancelAfter(CommandTimeout);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo {
            FileName = "/usr/bin/git",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = work
        };
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = "/usr/bin:/bin";
        process.StartInfo.Environment["HOME"] = "/nonexistent";
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        process.StartInfo.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        process.StartInfo.Environment["GIT_CONFIG_COUNT"] = "2";
        process.StartInfo.Environment["GIT_CONFIG_KEY_0"] = "core.hooksPath";
        process.StartInfo.Environment["GIT_CONFIG_VALUE_0"] = "/dev/null";
        process.StartInfo.Environment["GIT_CONFIG_KEY_1"] = "protocol.allow";
        process.StartInfo.Environment["GIT_CONFIG_VALUE_1"] = "never";
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

        // Fail early with fixed, non-sensitive diagnostic codes.
        if (!File.Exists("/usr/bin/git"))
            throw new LocalGitCreateOnlyTransferException("SandboxGitBinaryMissing");
        if (!Directory.Exists(work))
            throw new LocalGitCreateOnlyTransferException("SandboxGitWorkdirMissing");
        var started = false;
        try
        {
            if (!process.Start())
                throw new LocalGitCreateOnlyTransferException(
                    "SandboxGitUnavailable");
            started = true;
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            _ = await stderr; // Never persist raw stderr, paths or tokens.
            if (process.ExitCode != 0)
                throw new LocalGitCreateOnlyTransferException(
                    "SandboxGitCommandRejected");
            return output;
        }
        catch (OperationCanceledException) when (!external.IsCancellationRequested)
        { throw new LocalGitCreateOnlyTransferException("SandboxGitDeadline"); }
        catch (System.ComponentModel.Win32Exception)
        { throw new LocalGitCreateOnlyTransferException("SandboxGitUnavailable"); }
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

    private static async Task<string> ReadBoundedAsync(
        StreamReader input, CancellationToken ct)
    {
        var data = new char[8192];
        var result = new System.Text.StringBuilder();
        while (true)
        {
            var n = await input.ReadAsync(data.AsMemory(), ct);
            if (n == 0) break;
            if (result.Length + n > MaxGitOutput)
                throw new LocalGitCreateOnlyTransferException(
                    "SandboxGitOutputLimit");
            result.Append(data, 0, n);
        }
        return result.ToString();
    }
}
