using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-057: read-only GitHub smart-HTTPS ls-remote and fetch into an unpredictable
// disposable bare mirror, followed by F-056's independent full-object proof.
// NEVER invokes push, upload-pack client write, checkout or user URL.
public sealed class GitHubReadOnlyObjectMirrorRunner : IGitHubReadOnlyObjectMirrorRunner
{
    private const int MaxOutput = 16 * 1024 * 1024;
    private const int MaxErrorOutput = 128 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private readonly string sourceRoot;
    private readonly string scratchRoot;
    private readonly GitHubReadOnlyObjectMirrorOptions options;
    private readonly TimeProvider clock;

    // Test-local origin is deliberately INTERNAL and inaccessible to normal
    // application composition. Production always derives github.com HTTPS.
    private readonly string? testLocalOrigin;

    public GitHubReadOnlyObjectMirrorRunner(
        string sourceRoot, string scratchRoot,
        GitHubReadOnlyObjectMirrorOptions options,
        TimeProvider? clock = null)
    {
        this.sourceRoot = sourceRoot;
        this.scratchRoot = scratchRoot;
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
    }

    internal GitHubReadOnlyObjectMirrorRunner(
        string sourceRoot, string scratchRoot,
        GitHubReadOnlyObjectMirrorOptions options,
        string testLocalOrigin, TimeProvider? clock = null)
        : this(sourceRoot, scratchRoot, options, clock)
    {
        if (!Path.IsPathFullyQualified(testLocalOrigin))
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorTestOriginInvalid");
        this.testLocalOrigin = testLocalOrigin;
    }

    public async Task<GitHubReadOnlyObjectMirrorReceipt> VerifyAsync(
        GitHubReadOnlyObjectMirrorRequest request,
        string installationToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var realUrl = GitHubReadOnlyObjectMirrorIntegrity.RemoteUrl(
            request, options, now);
        if (string.IsNullOrEmpty(installationToken) ||
            installationToken.Length > 1024 ||
            installationToken.Any(char.IsControl))
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorCredentialInvalid");

        CheckTrustedRoot(sourceRoot);
        CheckTrustedRoot(scratchRoot);

        var expectedRemote = request.ExpectedRemoteRefs.AsMirrorRefs();
        var source = request.ExpectedSourceRefs;
        var format = source[0].ObjectId.Length == 40 ? "sha1" : "sha256";
        var remote = testLocalOrigin ?? realUrl;
        var mirrorId = Guid.NewGuid();
        var path = Path.Combine(Path.GetFullPath(scratchRoot),
            mirrorId.ToString("N") + ".git");
        var localOnly = testLocalOrigin is not null;

        await Gate.WaitAsync(ct);
        try
        {
            CheckTrustedRoot(sourceRoot);
            CheckTrustedRoot(scratchRoot);
            Directory.CreateDirectory(path);
            await GitAsync(path, remote, installationToken, localOnly, ct,
                "init", "--bare", "--quiet", "--object-format=" + format);

            var before = await ObserveRemoteAsync(path, remote,
                installationToken, localOnly, ct);
            if (!before.SequenceEqual(expectedRemote))
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorRemoteRefsChanged");

            // All refs are copied into this private bare repository only.
            // No '+' forced remote refspecs, no HTTP PUT/POST, no git push.
            await GitAsync(path, remote, installationToken, localOnly, ct,
                "fetch", "--quiet", "--no-tags", "--no-write-fetch-head",
                "--no-recurse-submodules", "--no-auto-maintenance", remote,
                "refs/heads/*:refs/heads/*", "refs/tags/*:refs/tags/*");

            var afterFetch = await ObserveRemoteAsync(path, remote,
                installationToken, localOnly, ct);
            if (!afterFetch.SequenceEqual(before))
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorRemoteRefsChanged");

            var physical = await PhysicalTypedRefsAsync(path, remote,
                installationToken, localOnly, ct);
            if (!physical.SequenceEqual(request.ExpectedRemoteRefs.Refs))
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorPhysicalRefsMismatch");

            // F-056 checks the entire SOURCE and freshly downloaded TARGET
            // including commit-parent ancestry, full trees, blobs and tags.
            var auditor = new LocalGitObjectClosureAuditor(
                sourceRoot, scratchRoot, enabled: true, clock);
            var closure = await auditor.AuditAsync(new(
                request.SourceSnapshotId, mirrorId, source, expectedRemote), ct);

            // The provider can change while we read. The last full Git
            // ls-remote pass must agree with the original observation.
            var final = await ObserveRemoteAsync(path, remote,
                installationToken, localOnly, ct);
            if (!final.SequenceEqual(before))
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorRemoteRefsChanged");

            // Bound the same token/snapshot to the end of verification:
            // a long fetch cannot silently convert a stale read to proof.
            return GitHubReadOnlyObjectMirrorIntegrity.Seal(request,
                new(mirrorId, physical.Select(x =>
                    new GitMirrorRef(x.Name, x.ObjectId)).ToArray(), closure),
                options, clock.GetUtcNow());
        }
        catch (GitHubReadOnlyObjectMirrorException) { throw; }
        catch (GitObjectClosureAuditException ex)
        {
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorOfflineClosureRejected:" + ex.Code);
        }
        catch (IOException)
        {
            throw new GitHubReadOnlyObjectMirrorException("RemoteMirrorLocalIoFailed");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitHubReadOnlyObjectMirrorException("RemoteMirrorLocalIoFailed");
        }
        finally
        {
            try
            {
                // Only this process-generated unpredictable mirror is deleted.
                // The trusted scratch root never contains a user-chosen path.
                if (Directory.Exists(path) &&
                    new DirectoryInfo(path).LinkTarget is null)
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Gate.Release();
        }
    }

    private static void CheckTrustedRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorRootInvalid");
        for (var p = new DirectoryInfo(Path.GetFullPath(root));
            p is not null; p = p.Parent)
            if (p.LinkTarget is not null ||
                (p.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorSymlinkBlocked");
    }

    private static async Task<IReadOnlyList<GitMirrorRef>> ObserveRemoteAsync(
        string gitDir, string remote, string token, bool localOnly,
        CancellationToken ct)
    {
        var output = await GitAsync(gitDir, remote, token, localOnly, ct,
            "ls-remote", "--refs", remote, "refs/heads/*", "refs/tags/*");
        var items = new List<GitMirrorRef>();
        foreach (var row in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = row.TrimEnd('\r').Split('\t');
            if (bits.Length != 2 ||
                items.Count >= GitObjectClosureAuditIntegrity.MaxRefs)
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorRefOutputInvalid");
            items.Add(new(bits[1], bits[0]));
        }
        try { return GitMirrorRefReconciler.CanonicalizeObservedRefs(items); }
        catch (GitMirrorRefReconciliationException)
        {
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorRefOutputInvalid");
        }
    }

    private static async Task<IReadOnlyList<GitHubObservedRef>> PhysicalTypedRefsAsync(
        string gitDir, string remote, string token, bool localOnly,
        CancellationToken ct)
    {
        var output = await GitAsync(gitDir, remote, token, localOnly, ct,
            "for-each-ref",
            "--format=%(refname)%09%(objectname)%09%(objecttype)");
        var items = new List<GitHubObservedRef>();
        foreach (var row in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = row.TrimEnd('\r').Split('\t');
            if (bits.Length != 3 ||
                items.Count >= GitObjectClosureAuditIntegrity.MaxRefs)
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorPhysicalRefsInvalid");
            items.Add(new(bits[0], bits[1], bits[2]));
        }
        return items;
    }

    private static async Task<string> GitAsync(
        string gitDir, string remote, string token, bool localOnly,
        CancellationToken external, params string[] args)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(external);
        deadline.CancelAfter(Deadline);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo {
            FileName = "/usr/bin/git", WorkingDirectory = gitDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
            UseShellExecute = false
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
        env["GIT_ALLOW_PROTOCOL"] = localOnly ? "file" : "https";
        // Disable credential helpers and redirects: the ephemeral GitHub
        // installation token must not leak to another host.
        env["GIT_CONFIG_COUNT"] = localOnly ? "4" : "5";
        env["GIT_CONFIG_KEY_0"] = "credential.helper";
        env["GIT_CONFIG_VALUE_0"] = "";
        env["GIT_CONFIG_KEY_1"] = "core.hooksPath";
        env["GIT_CONFIG_VALUE_1"] = "/dev/null";
        env["GIT_CONFIG_KEY_2"] = "http.followRedirects";
        env["GIT_CONFIG_VALUE_2"] = "false";
        env["GIT_CONFIG_KEY_3"] = "protocol.allow";
        env["GIT_CONFIG_VALUE_3"] = "never";
        if (!localOnly)
        {
            env["GIT_CONFIG_KEY_4"] = "http.https://github.com/.extraheader";
            env["GIT_CONFIG_VALUE_4"] = "AUTHORIZATION: basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "x-access-token:" + token));
        }
        if (args[0] != "init")
            process.StartInfo.ArgumentList.Add("--git-dir=" + gitDir);
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);

        var started = false;
        try
        {
            if (!process.Start())
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorGitUnavailable");
            started = true;
            var standard = ReadLimitedAsync(process.StandardOutput,
                MaxOutput, deadline.Token);
            var errors = ReadLimitedAsync(process.StandardError,
                MaxErrorOutput, deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var output = await standard;
            _ = await errors; // Do not expose Git stderr / auth headers.
            if (process.ExitCode != 0)
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorGitReadRejected");
            return output;
        }
        catch (OperationCanceledException) when (!external.IsCancellationRequested)
        {
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorGitDeadline");
        }
        catch (Win32Exception)
        {
            throw new GitHubReadOnlyObjectMirrorException(
                "RemoteMirrorGitUnavailable");
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
        var chars = new char[8192];
        while (true)
        {
            var n = await reader.ReadAsync(chars.AsMemory(), ct);
            if (n == 0) return sb.ToString();
            if (n > max - sb.Length)
                throw new GitHubReadOnlyObjectMirrorException(
                    "RemoteMirrorGitOutputLimit");
            sb.Append(chars, 0, n);
        }
    }
}
