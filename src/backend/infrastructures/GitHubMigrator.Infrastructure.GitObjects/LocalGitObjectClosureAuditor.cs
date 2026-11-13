using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-056: read-only, local bare-to-bare verification. Roots are controlled
// by deployment. Callers supply GUID handles, NEVER paths, URLs or refspecs.
// No GitHub client, token, git push, clone or network action exists here.
public sealed class LocalGitObjectClosureAuditor(
    string sourceRoot, string targetRoot, bool enabled = false,
    TimeProvider? clock = null) : ILocalGitObjectClosureAuditor
{
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(90);
    private const int MaxGitOutput = 16 * 1024 * 1024;
    private const int MaxGitErrors = 128 * 1024;
    private const int MaxFilesystemNodes = 450_000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<GitObjectClosureAuditReceipt> AuditAsync(
        GitObjectClosureAuditRequest request, CancellationToken ct)
    {
        if (!enabled)
            throw new GitObjectClosureAuditException("GitClosureDisabled");
        var (sourceRefs, targetRefs, format) =
            GitObjectClosureAuditIntegrity.Verify(request);
        var source = SafeBarePath(sourceRoot, request.SourceSnapshotId);
        var target = SafeBarePath(targetRoot, request.TargetSandboxId);
        if (string.Equals(source, target, StringComparison.Ordinal))
            throw new GitObjectClosureAuditException("GitClosureSameRepository");

        await Gate.WaitAsync(ct);
        try
        {
            // Recheck paths after the concurrency gate (e.g. alias swap).
            CheckBare(source);
            CheckBare(target);
            await VerifyRepositoryAsync(source, sourceRefs, format, ct);
            await VerifyRepositoryAsync(target, targetRefs, format, ct);

            // Enumerate only the source-proven object roots on BOTH sides,
            // excluding unrelated target-only refs from equality comparison.
            var sourceSet = await ReachableAsync(source, sourceRefs, format, ct);
            var targetSet = await ReachableAsync(target, sourceRefs, format, ct);
            if (sourceSet.Count != targetSet.Count ||
                sourceSet.Digest != targetSet.Digest)
                throw new GitObjectClosureAuditException("GitClosureMismatch");

            // A concurrent local writer or object pruning process must not
            // turn the earlier fsck / ref read into a success receipt.
            await VerifyRepositoryAsync(source, sourceRefs, format, ct);
            await VerifyRepositoryAsync(target, targetRefs, format, ct);
            var finalSource = await ReachableAsync(source, sourceRefs, format, ct);
            var finalTarget = await ReachableAsync(target, sourceRefs, format, ct);
            if (sourceSet != finalSource || targetSet != finalTarget)
                throw new GitObjectClosureAuditException("GitClosureChangedDuringAudit");
            CheckBare(source);
            CheckBare(target);

            return GitObjectClosureAuditIntegrity.Seal(
                request, format, sourceSet.Digest, targetSet.Digest,
                sourceSet.Count, targetSet.Count, _clock.GetUtcNow());
        }
        catch (GitObjectClosureAuditException) { throw; }
        catch (IOException)
        {
            throw new GitObjectClosureAuditException("GitClosureLocalIoFailure");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitObjectClosureAuditException("GitClosureLocalIoFailure");
        }
        finally { Gate.Release(); }
    }

    // F-060: reuse the SAME strict fsck, complete refs, no-alternates,
    // no-shallow/promisor and bounded sorted-object hashing checks as
    // F-056, but verify a single newly materialized source mirror.
    internal static async Task<(int Count, string Digest)> VerifyOneAsync(
        string root, Guid mirrorId,
        IReadOnlyList<GitMirrorRef> expected, CancellationToken ct)
    {
        if (expected is null || expected.Count == 0)
            throw new GitObjectClosureAuditException("GitClosureRefOutputInvalid");
        var canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(expected);
        if (!canonical.SequenceEqual(expected))
            throw new GitObjectClosureAuditException("GitClosureRefsChanged");
        return await VerifyOneAtPathAsync(
            SafeBarePath(root, mirrorId), expected, ct);
    }

    // #123: full, read-only post-rename verification of the EXACT
    // deterministic quarantined UUID.git path. This code NEVER accepts
    // user-supplied filesystem paths and never treats quarantine as an
    // F-061 Verified/source authority. Used only for crash diagnosis.
    internal static async Task<(int Count, string Digest)>
        VerifyQuarantinedOneAsync(
            string root, Guid mirrorId, string decisionSha256,
            IReadOnlyList<GitMirrorRef> expected, CancellationToken ct)
    {
        if (mirrorId == Guid.Empty ||
            !GitSourceBundleStorageCheck.ValidSha(decisionSha256) ||
            string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new GitObjectClosureAuditException("GitClosureRootInvalid");
        var physical = Path.GetFullPath(root);
        for (var dir = new DirectoryInfo(physical);
            dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null ||
                (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new GitObjectClosureAuditException("GitClosureSymlinkBlocked");

        var basename = mirrorId.ToString("N") + "." + decisionSha256 +
            ".quarantine.git";
        var path = Path.Combine(physical, basename);
        CheckBare(path);
        if (expected is null || expected.Count == 0 ||
            !GitMirrorRefReconciler.CanonicalizeObservedRefs(expected)
                .SequenceEqual(expected))
            throw new GitObjectClosureAuditException("GitClosureRefsChanged");
        return await VerifyOneAtPathAsync(path, expected, ct);
    }

    private static async Task<(int Count, string Digest)> VerifyOneAtPathAsync(
        string path, IReadOnlyList<GitMirrorRef> expected,
        CancellationToken ct)
    {
        var format = expected[0].ObjectId.Length == 40 ? "sha1" : "sha256";
        await VerifyRepositoryAsync(path, expected, format, ct);
        var first = await ReachableAsync(path, expected, format, ct);
        await VerifyRepositoryAsync(path, expected, format, ct);
        var second = await ReachableAsync(path, expected, format, ct);
        if (first != second)
            throw new GitObjectClosureAuditException("GitClosureChangedDuringAudit");
        CheckBare(path);
        return first;
    }

    internal static string SafeBarePath(string root, Guid id)
    {
        if (string.IsNullOrWhiteSpace(root) || id == Guid.Empty ||
            !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new GitObjectClosureAuditException("GitClosureRootInvalid");
        var physical = Path.GetFullPath(root);
        for (var p = new DirectoryInfo(physical); p is not null; p = p.Parent)
            if (p.LinkTarget is not null)
                throw new GitObjectClosureAuditException("GitClosureSymlinkBlocked");
        var repository = Path.Combine(physical, id.ToString("N") + ".git");
        CheckBare(repository);
        return repository;
    }

    private static void CheckBare(string repository)
    {
        if (!Directory.Exists(repository) ||
            !Directory.Exists(Path.Combine(repository, "objects")) ||
            !Directory.Exists(Path.Combine(repository, "refs")) ||
            !File.Exists(Path.Combine(repository, "HEAD")))
            throw new GitObjectClosureAuditException("GitClosureRepositoryInvalid");

        // Git alternates, grafts, shallow clones and partial/promisor
        // repositories invalidate a standalone, closed-object proof.
        if (File.Exists(Path.Combine(repository, "objects", "info", "alternates")) ||
            File.Exists(Path.Combine(repository, "info", "grafts")) ||
            File.Exists(Path.Combine(repository, "shallow")))
            throw new GitObjectClosureAuditException("GitClosureNotSelfContained");

        var visited = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(repository));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (dir.LinkTarget is not null ||
                (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new GitObjectClosureAuditException("GitClosureSymlinkBlocked");
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (++visited > MaxFilesystemNodes)
                    throw new GitObjectClosureAuditException("GitClosureFilesystemLimit");
                if (entry.LinkTarget is not null ||
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new GitObjectClosureAuditException("GitClosureSymlinkBlocked");
                if (entry is DirectoryInfo nested)
                    pending.Push(nested);
                if (entry is FileInfo file &&
                    file.Extension.Equals(".promisor", StringComparison.OrdinalIgnoreCase))
                    throw new GitObjectClosureAuditException("GitClosureNotSelfContained");
            }
        }
    }

    private static async Task VerifyRepositoryAsync(string path,
        IReadOnlyList<GitMirrorRef> expected, string format,
        CancellationToken ct)
    {
        var gitFormat = (await GitAsync(path, ct, null,
            "rev-parse", "--show-object-format")).Trim();
        if (gitFormat != format)
            throw new GitObjectClosureAuditException("GitClosureObjectFormatMismatch");
        if ((await GitAsync(path, ct, null,
            "rev-parse", "--is-shallow-repository")).Trim() != "false")
            throw new GitObjectClosureAuditException("GitClosureNotSelfContained");
        var partialClone = await GitAsync(path, ct, null,
            "config", "--local", "--get-regexp", "^extensions\\.partialclone$",
            acceptExitCode1: true);
        if (partialClone.Length > 0)
            throw new GitObjectClosureAuditException("GitClosureNotSelfContained");

        var actualRefs = await ReadRefsAsync(path, ct);
        if (!actualRefs.SequenceEqual(expected))
            throw new GitObjectClosureAuditException("GitClosureRefsChanged");

        // No --connectivity-only shortcut: Git must hash/check commits,
        // annotated tags, trees and blobs and reject missing/corrupt objects.
        await GitAsync(path, ct, null, "fsck", "--full", "--strict",
            "--no-reflogs", "--no-progress");
    }

    private static async Task<IReadOnlyList<GitMirrorRef>> ReadRefsAsync(
        string path, CancellationToken ct)
    {
        var output = await GitAsync(path, ct, null,
            "for-each-ref", "--format=%(refname)%09%(objectname)");
        var refs = new List<GitMirrorRef>();
        foreach (var line in output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 2 ||
                refs.Count >= GitObjectClosureAuditIntegrity.MaxRefs)
                throw new GitObjectClosureAuditException("GitClosureRefOutputInvalid");
            refs.Add(new(fields[0], fields[1]));
        }
        try { return GitMirrorRefReconciler.CanonicalizeObservedRefs(refs); }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitObjectClosureAuditException("GitClosureRefOutputInvalid");
        }
    }

    private static async Task<(int Count, string Digest)> ReachableAsync(
        string path, IReadOnlyList<GitMirrorRef> sourceRefs,
        string format, CancellationToken ct)
    {
        var input = string.Join('\n', sourceRefs.Select(x => x.ObjectId)) + "\n";
        var output = await GitAsync(path, ct, input, "rev-list",
            "--objects", "--no-object-names", "--stdin");
        var objects = new HashSet<string>(StringComparer.Ordinal);
        var expectedLength = format == "sha1" ? 40 : 64;
        foreach (var row in output.Split('\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            var oid = row.TrimEnd('\r');
            if (oid.Length != expectedLength ||
                oid.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new GitObjectClosureAuditException("GitClosureObjectOutputInvalid");
            if (!objects.Add(oid))
                throw new GitObjectClosureAuditException("GitClosureObjectDuplicate");
            if (objects.Count > GitObjectClosureAuditIntegrity.MaxObjects)
                throw new GitObjectClosureAuditException("GitClosureObjectLimit");
        }
        if (objects.Count == 0)
            throw new GitObjectClosureAuditException("GitClosureObjectOutputInvalid");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(
            "f056-reachable-object-set-v1\n" + format + "\n" + objects.Count + "\n"));
        foreach (var oid in objects.OrderBy(x => x, StringComparer.Ordinal))
            sha.AppendData(Encoding.UTF8.GetBytes(oid + "\n"));
        return (objects.Count, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    // F-094 reuses the same hardened local-only Git invocation and
    // rejects anything but an exact exit-0/exit-1 graph predicate result.
    internal static async Task<bool> HasCommitAsync(
        string bareRepository, string objectId, CancellationToken ct)
    {
        // cat-file -e exits 128 for unknown objects on some Git versions.
        // Batch-check reports "missing" as data with exit 0, so a truly
        // absent target commit is Unknown rather than an operational error.
        var result = (await GitAsync(bareRepository, ct, objectId + "\n",
            "cat-file", "--batch-check=%(objectname) %(objecttype)")).Trim();
        if (result == objectId + " missing") return false;
        if (result == objectId + " commit") return true;
        if (result == objectId + " tree" ||
            result == objectId + " blob" ||
            result == objectId + " tag") return false;
        throw new GitObjectClosureAuditException(
            "GitClosureObjectOutputInvalid");
    }

    internal static async Task<bool> IsAncestorAsync(
        string bareRepository, string ancestor, string descendant,
        CancellationToken ct)
    {
        var status = await GitAsync(bareRepository, ct, null,
            "merge-base", "--is-ancestor", ancestor, descendant,
            acceptExitCode1: true, returnExitCode: true);
        return status == "0";
    }

    private static async Task<string> GitAsync(
        string repository, CancellationToken external, string? standardInput,
        string arg1, string? arg2 = null, string? arg3 = null,
        string? arg4 = null, string? arg5 = null,
        bool acceptExitCode1 = false, bool returnExitCode = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(external);
        deadline.CancelAfter(CommandDeadline);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo {
            FileName = "/usr/bin/git",
            WorkingDirectory = repository,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true
        };
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = "/usr/bin:/bin";
        process.StartInfo.Environment["HOME"] = "/nonexistent";
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        process.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        process.StartInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_CONFIG_COUNT"] = "2";
        process.StartInfo.Environment["GIT_CONFIG_KEY_0"] = "protocol.allow";
        process.StartInfo.Environment["GIT_CONFIG_VALUE_0"] = "never";
        process.StartInfo.Environment["GIT_CONFIG_KEY_1"] = "core.hooksPath";
        process.StartInfo.Environment["GIT_CONFIG_VALUE_1"] = "/dev/null";
        process.StartInfo.ArgumentList.Add("--git-dir=" + repository);
        foreach (var arg in new[] { arg1, arg2, arg3, arg4, arg5 })
            if (arg is not null) process.StartInfo.ArgumentList.Add(arg);
        var started = false;
        try
        {
            if (!process.Start())
                throw new GitObjectClosureAuditException("GitClosureGitUnavailable");
            started = true;
            var output = ReadLimitedAsync(process.StandardOutput,
                MaxGitOutput, deadline.Token);
            var error = ReadLimitedAsync(process.StandardError,
                MaxGitErrors, deadline.Token);
            if (standardInput is not null)
                await process.StandardInput.WriteAsync(
                    standardInput.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var result = await output;
            _ = await error; // Never log or persist paths in raw stderr.
            if (process.ExitCode != 0 &&
                !(acceptExitCode1 && process.ExitCode == 1))
                throw new GitObjectClosureAuditException("GitClosureGitCommandRejected");
            return returnExitCode ? process.ExitCode.ToString() : result;
        }
        catch (OperationCanceledException) when (!external.IsCancellationRequested)
        {
            throw new GitObjectClosureAuditException("GitClosureDeadline");
        }
        catch (Win32Exception)
        {
            throw new GitObjectClosureAuditException("GitClosureGitUnavailable");
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
        StreamReader input, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var n = await input.ReadAsync(buffer.AsMemory(), ct);
            if (n == 0) return sb.ToString();
            if (n > max - sb.Length)
                throw new GitObjectClosureAuditException("GitClosureGitOutputLimit");
            sb.Append(buffer, 0, n);
        }
    }
}
