using System.Diagnostics;
using System.Security.Cryptography;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// Executes REAL Git import commands exclusively against a disposable bare
// repository below the trusted scratch root. It never accepts a GitHub URL,
// never uses PATs, and never pushes to a destination outside that sandbox.
public sealed class LocalGitImportRehearsalRunner(
    string bundleRoot, string scratchRoot, bool enabled = false,
    TimeProvider? clock = null) : IGitImportRehearsalRunner
{
    public const int MaxCommandOutput = 2 * 1024 * 1024;
    // Bound simultaneous Git work *per process*; multi-replica global
    // admission / durable leases must be added before remote execution.
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(55);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<GitImportRehearsalArtifact> RunAsync(
        GitImportRehearsalRequest request, CancellationToken ct)
    {
        if (!enabled)
            throw new GitImportRehearsalException("GitRehearsalDisabled");
        if (request is null || request.IntentId == Guid.Empty ||
            request.WorkpackId == Guid.Empty ||
            request.Bundle.WorkpackId != request.WorkpackId ||
            request.Bundle.BundleSha256 != request.BundleSha256 ||
            request.Bundle.ReadyForExecution ||
            !GitSourceBundleStorageCheck.ValidSha(request.IntentEvidenceSha256))
            throw new GitImportRehearsalException("GitRehearsalRequestInvalid");

        ValidateRoot(scratchRoot);
        var storage = new LocalGitBundleArtifactStore(bundleRoot);
        var physical = await storage.VerifyAsync(request.Bundle, ct);
        if (!physical.IsCurrent || physical.ReadyForExecution ||
            physical.BundleSha256 != request.BundleSha256)
            throw new GitImportRehearsalException("GitRehearsalBundleChanged");

        var safe = GitHubSafeRefPlanner.Build(
            request.Bundle.PlanId, request.ManifestSha256,
            request.ReconciliationSha256, request.BundleSha256, request.Refs);
        if (safe.EvidenceSha256 != request.SafeRefPlanSha256 ||
            !safe.HasOnlyNonDestructiveCandidates ||
            safe.CreateCandidates is < 1 or > GitImportRehearsalIntegrity.MaxCandidateRefs)
            throw new GitImportRehearsalException("GitRehearsalRefPlanNotSafe");

        var sourceRefs = request.Refs
            .Where(x => x.SourceObjectId is not null)
            .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
        var creates = request.Refs
            .Where(x => x.Disposition == GitMirrorRefDisposition.CreateCandidate)
            .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
        var matches = request.Refs
            .Where(x => x.Disposition == GitMirrorRefDisposition.MatchingObjectId)
            .Select(x => new GitMirrorRef(x.Name, x.SourceObjectId!)).ToArray();
        var canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(sourceRefs);
        if (!canonical.SequenceEqual(sourceRefs) ||
            canonical.Count != request.Bundle.RefCount ||
            creates.Length != safe.CreateCandidates ||
            matches.Length != safe.AlreadyMatching)
            throw new GitImportRehearsalException("GitRehearsalSourceRefMismatch");

        var gitFormat = sourceRefs[0].ObjectId.Length == 40 ? "sha1" : "sha256";
        if (sourceRefs.Any(x => x.ObjectId.Length != sourceRefs[0].ObjectId.Length))
            throw new GitImportRehearsalException("GitRehearsalObjectFormatMismatch");

        var sandbox = Path.Combine(Path.GetFullPath(scratchRoot),
            request.WorkpackId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        // Directories are private, unpredictable and only created by this
        // method; neither the operator nor request body can choose a remote.
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(sandbox);
            var source = Path.Combine(sandbox, "source.git");
            var destination = Path.Combine(sandbox, "destination.git");
            var path = Path.Combine(storage.Root, request.Bundle.BundleFileName);
            await GitAsync(sandbox, ct, "clone", "--mirror", "--quiet", path, source);
            await GitAsync(sandbox, ct,
                "--git-dir=" + source, "fsck", "--full", "--strict",
                "--no-reflogs", "--no-progress");
            var actual = await ReadRefsAsync(source, sandbox, ct);
            if (!actual.SequenceEqual(canonical))
                throw new GitImportRehearsalException("GitRehearsalSourceRefMismatch");

            await GitAsync(sandbox, ct, "init", "--bare", "--quiet",
                "--object-format=" + gitFormat, destination);

            // Copy only matching OIDs to model pre-existing GitHub refs
            // that are known to equal the source. Target-only GitHub refs
            // are NOT physically materialized: their unknown objects are
            // intentionally not assumed to exist in the source bundle.
            if (matches.Length > 0)
                await PushAsync(source, destination, sandbox, matches, ct,
                    requireAbsent: true);
            var before = await ReadRefsAsync(destination, sandbox, ct);
            if (!before.SequenceEqual(matches))
                throw new GitImportRehearsalException("GitRehearsalBaselineMismatch");

            // Create-only, atomic ref update. Every target ref uses a lease
            // that explicitly expects ABSENCE. No '+' refspec, delete or
            // force-push command can be constructed from this API.
            await PushAsync(source, destination, sandbox, creates, ct,
                requireAbsent: true);
            var after = await ReadRefsAsync(destination, sandbox, ct);
            var expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(
                matches.Concat(creates).ToArray());
            if (!after.SequenceEqual(expected) || after.Count != before.Count + creates.Length)
                throw new GitImportRehearsalException("GitRehearsalDestinationMismatch");

            await GitAsync(sandbox, ct, "--git-dir=" + destination,
                "fsck", "--full", "--strict", "--no-reflogs", "--no-progress");
            var rehashed = await storage.VerifyAsync(request.Bundle, ct);
            if (rehashed.BundleSha256 != physical.BundleSha256 ||
                rehashed.BundleBytes != physical.BundleBytes)
                throw new GitImportRehearsalException("GitRehearsalBundleChanged");

            return new(gitFormat,
                GitImportRehearsalIntegrity.SealRefs("f041-source-refs-v1", canonical),
                creates.Length, matches.Length, safe.TargetOnlyPreserved,
                creates.Length,
                GitImportRehearsalIntegrity.SealRefs("f041-created-refs-v1", creates),
                _clock.GetUtcNow());
        }
        catch (GitImportRehearsalException) { throw; }
        catch (GitSourceBundleException) { throw; }
        catch (GitMirrorRefReconciliationException)
        { throw new GitImportRehearsalException("GitRehearsalSourceRefMismatch"); }
        catch (IOException)
        { throw new GitImportRehearsalException("GitRehearsalLocalIoFailure"); }
        catch (UnauthorizedAccessException)
        { throw new GitImportRehearsalException("GitRehearsalLocalIoFailure"); }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); }
            catch (IOException) { } // Never delete outside our private sandbox.
            catch (UnauthorizedAccessException) { }
            Gate.Release();
        }
    }

    private static async Task PushAsync(
        string source, string destination, string work,
        IReadOnlyList<GitMirrorRef> refs, CancellationToken ct,
        bool requireAbsent)
    {
        if (refs.Count == 0) return;
        var args = new List<string> {
            "--git-dir=" + source, "push", "--porcelain", "--atomic"
        };
        foreach (var entry in refs)
        {
            if (requireAbsent)
                args.Add("--force-with-lease=" + entry.Name + ":");
        }
        args.Add(destination);
        foreach (var entry in refs)
            args.Add(entry.ObjectId + ":" + entry.Name);
        var output = await GitAsync(work, ct, args.ToArray());
        // Git may say 'up to date' when an external actor created exactly
        // the same OID. That is still NOT a successful CREATE operation.
        if (output.Split('\n').Any(line => line.StartsWith("=", StringComparison.Ordinal)))
            throw new GitImportRehearsalException("GitRehearsalRefAlreadyExists");
    }

    private static async Task<IReadOnlyList<GitMirrorRef>> ReadRefsAsync(
        string repository, string work, CancellationToken ct)
    {
        var output = await GitAsync(work, ct, "--git-dir=" + repository,
            "for-each-ref", "--format=%(refname)%09%(objectname)");
        var refs = new List<GitMirrorRef>();
        foreach (var row in output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = row.TrimEnd('\r').Split('\t');
            if (fields.Length != 2)
                throw new GitImportRehearsalException("GitRehearsalRefOutputInvalid");
            refs.Add(new(fields[0], fields[1]));
        }
        try { return GitMirrorRefReconciler.CanonicalizeObservedRefs(refs); }
        catch (GitMirrorRefReconciliationException)
        { throw new GitImportRehearsalException("GitRehearsalRefOutputInvalid"); }
    }

    private static void ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new GitImportRehearsalException("GitRehearsalScratchRootNotConfigured");
        for (var folder = new DirectoryInfo(Path.GetFullPath(root));
            folder is not null; folder = folder.Parent)
            if (folder.Exists && folder.LinkTarget is not null)
                throw new GitImportRehearsalException("GitRehearsalSymlinkBlocked");
    }

    private static async Task<string> GitAsync(
        string work, CancellationToken external, params string[] args)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(external);
        deadline.CancelAfter(CommandDeadline);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo {
            FileName = "/usr/bin/git", UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = work
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
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);
        var started = false;
        try
        {
            if (!process.Start())
                throw new GitImportRehearsalException("GitRehearsalGitUnavailable");
            started = true;
            var output = ReadOutputAsync(process.StandardOutput, deadline.Token);
            var errors = ReadOutputAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var text = await output;
            _ = await errors; // never persist raw CLI stderr: may contain paths
            if (process.ExitCode != 0)
                throw new GitImportRehearsalException("GitRehearsalGitCommandRejected");
            return text;
        }
        catch (OperationCanceledException) when (!external.IsCancellationRequested)
        { throw new GitImportRehearsalException("GitRehearsalDeadline"); }
        catch (System.ComponentModel.Win32Exception)
        { throw new GitImportRehearsalException("GitRehearsalGitUnavailable"); }
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

    private static async Task<string> ReadOutputAsync(
        StreamReader stream, CancellationToken ct)
    {
        var data = new char[8192];
        var result = new System.Text.StringBuilder();
        while (true)
        {
            var n = await stream.ReadAsync(data.AsMemory(), ct);
            if (n == 0) break;
            if (n > MaxCommandOutput - result.Length)
                throw new GitImportRehearsalException("GitRehearsalGitOutputLimit");
            result.Append(data, 0, n);
        }
        return result.ToString();
    }
}
