using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-071: INTERNAL, locally isolated transport experiment.
// This class is not registered in production DI and has no API route.
// No caller-provided remote URL, credential, arbitrary file path or refspec.
// A real Git fetch writes only to one independently created bare sandbox.
// GitHub HTTP, git push, migration execution and approvals are impossible.
internal sealed record LocalGitSandboxTransferReceipt(
    Guid SourceMirrorId, Guid TargetSandboxId, string GitObjectFormat,
    string SourceObjectSetSha256, string TargetObjectSetSha256,
    int VerifiedObjectCount, string AuditEvidenceSha256,
    string EvidenceSha256, DateTimeOffset VerifiedAtUtc,
    bool SandboxWritesPerformed, bool RemotePushInvoked,
    bool RemoteWritesAuthorized, bool MigrationComplete);

internal sealed class LocalGitSandboxTransferException(string code)
    : Exception("Local Git sandbox transfer refused")
{
    public string Code { get; } = code;
}

internal sealed class LocalGitDestinationSandboxTransfer(
    string sourceMirrorRoot, string sandboxRoot, bool enabled = false)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan GitDeadline = TimeSpan.FromSeconds(90);

    internal async Task<LocalGitSandboxTransferReceipt> TransferAsync(
        GitHubTrustedRemoteReadPlan plan,
        GitSourceSnapshotMirrorProof proof, Guid sandboxId,
        CancellationToken ct)
    {
        if (!enabled)
            throw new LocalGitSandboxTransferException(
                "SandboxTransferDisabled");
        if (sandboxId == Guid.Empty || plan is null ||
            sandboxId == plan.SourceMirrorId)
            throw new LocalGitSandboxTransferException(
                "SandboxTransferIdentityInvalid");
        try
        {
            GitSourceSnapshotMirrorIntegrity.RequirePlan(plan);
            // F-060 evidence must bind the original workpack, source refs
            // and full object-set digest. Never trust the supplied digest
            // without re-reading and fsck-verifying the physical source.
            if (proof is null || proof.PhysicalMirrorId != plan.SourceMirrorId ||
                proof.SourceSnapshotId != plan.SourceSnapshotId ||
                proof.RemotePushInvoked || proof.RemoteWritesAuthorized ||
                proof.MayAutomaticallyRetry || proof.MigrationComplete)
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferSourceProofInvalid");
            var expected = GitSourceSnapshotMirrorIntegrity.Seal(
                plan, proof.ObjectSetSha256, proof.VerifiedObjectCount,
                proof.VerifiedAtUtc);
            if (proof != expected)
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferSourceProofInvalid");
        }
        catch (GitSourceSnapshotMirrorException)
        {
            throw new LocalGitSandboxTransferException(
                "SandboxTransferSourceProofInvalid");
        }

        var sourceRoot = TrustedRoot(sourceMirrorRoot);
        var targetRoot = TrustedRoot(sandboxRoot);
        if (sourceRoot == targetRoot ||
            sourceRoot.StartsWith(targetRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            targetRoot.StartsWith(sourceRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new LocalGitSandboxTransferException(
                "SandboxTransferRootsOverlap");

        var sourcePath = Path.Combine(
            sourceRoot, plan.SourceMirrorId.ToString("N") + ".git");
        var targetPath = Path.Combine(
            targetRoot, sandboxId.ToString("N") + ".git");
        var scratchId = Guid.NewGuid();
        var scratchPath = Path.Combine(
            targetRoot, scratchId.ToString("N") + ".git");

        await Gate.WaitAsync(ct);
        try
        {
            await using var fileLock = await LockRootAsync(targetRoot, ct);
            // Recheck trusted roots after waiting on both process gates.
            _ = TrustedRoot(sourceMirrorRoot);
            _ = TrustedRoot(sandboxRoot);

            var sourceMeasured = await LocalGitObjectClosureAuditor
                .VerifyOneAsync(sourceRoot, plan.SourceMirrorId,
                    plan.SourceRefs, ct);
            if (sourceMeasured.Digest != proof.ObjectSetSha256 ||
                sourceMeasured.Count != proof.VerifiedObjectCount)
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferSourceChanged");

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                if (!Directory.Exists(targetPath))
                    throw new LocalGitSandboxTransferException(
                        "SandboxTransferTargetConflict");
                // Existing sandbox: verify it, never update or repair.
                await VerifyTargetAsync(targetRoot, sandboxId,
                    plan.SourceRefs, sourceMeasured, ct);
            }
            else
            {
                // A random GUID.git path is only a private staging area,
                // never a public Git remote or a user-selected target.
                if (Directory.Exists(scratchPath) || File.Exists(scratchPath))
                    throw new LocalGitSandboxTransferException(
                        "SandboxTransferStagingConflict");
                try
                {
                    Directory.CreateDirectory(scratchPath);
                    var format = plan.SourceRefs[0].ObjectId.Length == 40
                        ? "sha1" : "sha256";
                    await GitAsync(scratchPath, ct, "init", "--bare",
                        "--quiet", "--object-format=" + format);
                    // Explicit file-only protocol and sealed refspecs.
                    // This really transfers branches, tags, commits,
                    // trees and blobs between two local bare repos.
                    await GitAsync(scratchPath, ct,
                        "--git-dir=" + scratchPath, "fetch", "--quiet",
                        "--no-tags", "--no-write-fetch-head",
                        "--no-auto-maintenance", "--no-recurse-submodules",
                        sourcePath,
                        "refs/heads/*:refs/heads/*",
                        "refs/tags/*:refs/tags/*");
                    await VerifyTargetAsync(targetRoot, scratchId,
                        plan.SourceRefs, sourceMeasured, ct);
                    // Refuse any target created by another process rather
                    // than ever overwriting an existing sandbox.
                    if (Directory.Exists(targetPath) || File.Exists(targetPath))
                        throw new LocalGitSandboxTransferException(
                            "SandboxTransferTargetConflict");
                    Directory.Move(scratchPath, targetPath);
                    await VerifyTargetAsync(targetRoot, sandboxId,
                        plan.SourceRefs, sourceMeasured, ct);
                }
                finally
                {
                    // Only our own unpredictable staging directory may be
                    // deleted. A corrupted/verified final is never removed.
                    var scratch = new DirectoryInfo(scratchPath);
                    if (scratch.Exists && scratch.LinkTarget is null &&
                        (scratch.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        try { scratch.Delete(recursive: true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }

            // F-056 independently validates BOTH entire local Git object
            // closures after promotion, including annotated tags and
            // commit-parent ancestry. This proof is not a GitHub receipt.
            var audit = await new LocalGitObjectClosureAuditor(
                sourceRoot, targetRoot, enabled: true)
                .AuditAsync(new(plan.SourceMirrorId, sandboxId,
                    plan.SourceRefs, plan.SourceRefs), ct);
            if (audit.SourceObjectSetSha256 != proof.ObjectSetSha256 ||
                audit.TargetObjectSetSha256 != proof.ObjectSetSha256 ||
                audit.ReachableObjectCount != proof.VerifiedObjectCount)
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferClosureMismatch");
            var digest = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    new object[] {
                        "f071-local-sandbox-transport-receipt-v1",
                        plan.SourceSnapshotId.ToString("D"),
                        plan.SourceMirrorId.ToString("D"),
                        sandboxId.ToString("D"), proof.EvidenceSha256,
                        proof.VerificationReceiptSha256,
                        audit.EvidenceSha256, audit.GitObjectFormat,
                        audit.SourceObjectSetSha256, audit.ReachableObjectCount,
                        true, false, false, false
                    }))));
            return new(plan.SourceMirrorId, sandboxId,
                audit.GitObjectFormat, audit.SourceObjectSetSha256,
                audit.TargetObjectSetSha256, audit.ReachableObjectCount,
                audit.EvidenceSha256, digest, audit.VerifiedAtUtc,
                true, false, false, false);
        }
        catch (GitObjectClosureAuditException)
        {
            throw new LocalGitSandboxTransferException(
                "SandboxTransferClosureInvalid");
        }
        catch (IOException)
        {
            throw new LocalGitSandboxTransferException(
                "SandboxTransferLocalIoFailure");
        }
        catch (UnauthorizedAccessException)
        {
            throw new LocalGitSandboxTransferException(
                "SandboxTransferLocalIoFailure");
        }
        finally { Gate.Release(); }
    }

    private static async Task VerifyTargetAsync(
        string root, Guid id, IReadOnlyList<GitMirrorRef> refs,
        (int Count, string Digest) expected, CancellationToken ct)
    {
        var measured = await LocalGitObjectClosureAuditor.VerifyOneAsync(
            root, id, refs, ct);
        if (measured != expected)
            throw new LocalGitSandboxTransferException(
                "SandboxTransferClosureMismatch");
    }

    private static string TrustedRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new LocalGitSandboxTransferException(
                "SandboxTransferRootInvalid");
        var path = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar);
        for (var dir = new DirectoryInfo(path); dir is not null;
            dir = dir.Parent)
        {
            if (dir.LinkTarget is not null ||
                (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferSymlinkBlocked");
        }
        return path;
    }

    private static async Task<FileStream> LockRootAsync(
        string root, CancellationToken ct)
    {
        var path = Path.Combine(root, ".f071-local-sandbox.lock");
        if (File.Exists(path) &&
            (new FileInfo(path).LinkTarget is not null ||
             (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new LocalGitSandboxTransferException(
                "SandboxTransferSymlinkBlocked");
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            throw new LocalGitSandboxTransferException(
                "SandboxTransferLockUnavailable");
        }
        try
        {
            for (var n = 0; n < 180; n++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    stream.Lock(0, 1);
                    return stream;
                }
                catch (IOException) when (n < 179)
                {
                    await Task.Delay(100, ct);
                }
            }
            throw new LocalGitSandboxTransferException(
                "SandboxTransferLockUnavailable");
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task GitAsync(
        string directory, CancellationToken external, params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(external);
        timeout.CancelAfter(GitDeadline);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo {
            FileName = "/usr/bin/git", WorkingDirectory = directory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true
        };
        var env = process.StartInfo.Environment;
        env.Clear();
        env["PATH"] = "/usr/bin:/bin";
        env["HOME"] = "/nonexistent";
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_GLOBAL"] = "/dev/null";
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GIT_NO_REPLACE_OBJECTS"] = "1";
        env["GIT_NO_LAZY_FETCH"] = "1";
        env["GIT_OPTIONAL_LOCKS"] = "0";
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
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferGitUnavailable");
            started = true;
            var output = ReadLimitedAsync(process.StandardOutput, 8192,
                timeout.Token);
            var errors = ReadLimitedAsync(process.StandardError, 128 * 1024,
                timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await output;
            _ = await errors; // Never disclose raw local paths or Git stderr.
            if (process.ExitCode != 0)
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferGitCommandRejected");
        }
        catch (OperationCanceledException) when (!external.IsCancellationRequested)
        {
            throw new LocalGitSandboxTransferException(
                "SandboxTransferGitDeadline");
        }
        catch (Win32Exception)
        {
            throw new LocalGitSandboxTransferException(
                "SandboxTransferGitUnavailable");
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
        StreamReader reader, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (n == 0) return sb.ToString();
            if (n > max - sb.Length)
                throw new LocalGitSandboxTransferException(
                    "SandboxTransferGitOutputLimit");
            sb.Append(buffer, 0, n);
        }
    }
}
