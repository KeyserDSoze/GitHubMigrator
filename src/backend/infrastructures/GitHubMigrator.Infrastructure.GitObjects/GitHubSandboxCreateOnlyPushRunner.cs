using System.Diagnostics;
using System.Text;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// F-083: real Git HTTPS create-only sender for a SINGLE explicitly
// allowlisted GitHub.com sandbox repository. Not registered in production
// DI, no endpoint, no worker. The internal F-083 service must commit F-082
// before calling this adapter. NEVER call the adapter on its own.
public sealed class GitHubSandboxCreateOnlyPushRunner(
    string bundleRoot, string scratchRoot, GitHubSandboxOneShotOptions options)
    : IGitHubSandboxOneShotGitWriter
{
    // A test-only local bare origin is inaccessible to production DI.
    // It runs the SAME --atomic and empty-lease refspecs as GitHub.
    private readonly string? testLocalOrigin;

    internal GitHubSandboxCreateOnlyPushRunner(
        string bundleRoot, string scratchRoot, GitHubSandboxOneShotOptions options,
        string testLocalOrigin)
        : this(bundleRoot, scratchRoot, options)
    {
        if (!Path.IsPathFullyQualified(testLocalOrigin) ||
            !Directory.Exists(testLocalOrigin))
            throw new GitHubSandboxOneShotException("SandboxPushTestOriginInvalid");
        this.testLocalOrigin = testLocalOrigin;
    }

    private const int MaxOutput = 512 * 1024;
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(50);
    // Same-process duplicate invocation is blocked too; PostgreSQL F-082
    // remains the authoritative cross-process at-most-once barrier.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid, byte> UsedBarriers = new();

    public async Task PushOnceAsync(GitHubSandboxPushInput input,
        string oneRepositoryInstallationToken, CancellationToken ct)
    {
        GitHubSandboxPushIntegrity.Verify(input, options);
        if (string.IsNullOrWhiteSpace(oneRepositoryInstallationToken) ||
            oneRepositoryInstallationToken.Length is < 10 or > 4096 ||
            oneRepositoryInstallationToken.Any(char.IsControl))
            throw Refused("SandboxPushTokenInvalid");
        if (UsedBarriers.Count >= 10_000)
            throw Refused("SandboxPushProcessBudgetExceeded");
        if (!UsedBarriers.TryAdd(input.Barrier.Id, 0))
            throw Refused("SandboxPushAlreadyAttempted");

        // Never reset the UsedBarriers marker even on failure: the remote
        // might have accepted objects/refs before any local exception.
        var root = ValidateRoot(scratchRoot);
        var store = new LocalGitBundleArtifactStore(bundleRoot);
        var physical = await store.VerifyAsync(input.Bundle, ct);
        if (!physical.IsCurrent ||
            physical.BundleSha256 != input.Bundle.BundleSha256 ||
            physical.BundleBytes != input.Bundle.BundleBytes)
            throw Refused("SandboxPushBundleChanged");

        await Gate.WaitAsync(ct);
        var workspace = Path.Combine(root,
            "f083-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);
            RequireNoLinks(workspace);
            var local = Path.Combine(workspace, "verified-source.git");
            var bundlePath = Path.Combine(store.Root,
                input.Bundle.BundleFileName);
            await GitAsync(workspace, null, ct,
                "clone", "--mirror", "--quiet", bundlePath, local);
            await GitAsync(workspace, null, ct,
                "--git-dir=" + local, "fsck", "--full", "--strict",
                "--no-reflogs", "--no-progress");

            var refsText = await GitAsync(workspace, null, ct,
                "--git-dir=" + local, "for-each-ref",
                "--format=%(refname)%09%(objectname)");
            var actual = new List<GitMirrorRef>();
            foreach (var line in refsText.Split('\n',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length != 2)
                    throw Refused("SandboxPushSourceRefsInvalid");
                actual.Add(new(parts[0], parts[1]));
            }
            var canonical = GitMirrorRefReconciler.CanonicalizeObservedRefs(actual);
            if (canonical.Count != input.Bundle.RefCount ||
                input.Source.Candidates.Any(candidate =>
                    !canonical.Contains(candidate)))
                throw Refused("SandboxPushSourceRefsChanged");

            var verifiedAgain = await store.VerifyAsync(input.Bundle, ct);
            if (!verifiedAgain.IsCurrent ||
                verifiedAgain.BundleSha256 != physical.BundleSha256 ||
                verifiedAgain.BundleBytes != physical.BundleBytes)
                throw Refused("SandboxPushBundleChanged");

            // One fixed GitHub Cloud host, org/repo from server allowlist.
            // Tokens are NEVER interpolated into command-line arguments,
            // remote URL, logfile, exception message or Git config files.
            var remote = testLocalOrigin ?? ("https://github.com/" +
                options.AllowedOrganization + "/" + options.AllowedRepository +
                ".git");
            string? auth = testLocalOrigin is null ?
                "AUTHORIZATION: basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(
                        "x-access-token:" + oneRepositoryInstallationToken)) :
                null;

            // F-084: a provider GET before F-082 is NOT atomic with push.
            // Check all heads AND annotated-tag OIDs again via actual Git
            // upload-pack immediately before the atomic Git receive-pack.
            // Even drift on unrelated target-only refs must fail closed.
            var liveRefs = await GitAsync(workspace, auth, ct,
                "--git-dir=" + local, "ls-remote", "--refs", remote,
                "refs/heads/*", "refs/tags/*");
            var observed = new List<GitMirrorRef>();
            foreach (var line in liveRefs.Split('\n',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length != 2)
                    throw Refused("SandboxPushRemoteRefOutputInvalid");
                observed.Add(new(parts[1], parts[0]));
            }
            IReadOnlyList<GitMirrorRef> canonicalRemote;
            try
            {
                canonicalRemote =
                    GitMirrorRefReconciler.CanonicalizeObservedRefs(observed);
            }
            catch (GitMirrorRefReconciliationException)
            {
                throw Refused("SandboxPushRemoteRefOutputInvalid");
            }
            var expected = input.BaselineRefs.Refs.Select(x =>
                new GitMirrorRef(x.Name, x.ObjectId)).ToArray();
            if (!canonicalRemote.SequenceEqual(expected))
                throw Refused("SandboxPushRemoteBaselineChanged");

            var args = new List<string> {
                "--git-dir=" + local, "push", "--porcelain", "--atomic"
            };
            foreach (var candidate in input.Source.Candidates)
                args.Add("--force-with-lease=" + candidate.Name + ":");
            args.Add(remote);
            foreach (var candidate in input.Source.Candidates)
                args.Add(candidate.ObjectId + ":" + candidate.Name);

            // GitHub may reject --atomic or a branch rule; no fallback to
            // non-atomic or force push is allowed, and no retry is attempted.
            // Even zero exit is NOT proof of full object closure.
            var output = await GitAsync(workspace, auth, ct, args.ToArray());
            if (output.Split('\n').Any(x =>
                x.StartsWith("=", StringComparison.Ordinal) ||
                x.StartsWith("!", StringComparison.Ordinal)))
                throw Refused("SandboxPushOutcomeAmbiguous");
        }
        finally
        {
            try { if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Gate.Release();
        }
    }

    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw Refused("SandboxPushScratchNotConfigured");
        var full = Path.GetFullPath(root);
        RequireNoLinks(full);
        return full;
    }

    private static void RequireNoLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path));
            current is not null; current = current.Parent)
        {
            if (current.Exists &&
                current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw Refused("SandboxPushSymlinkBlocked");
        }
    }

    private async Task<string> GitAsync(
        string working, string? authHeader, CancellationToken ct,
        params string[] args)
    {
        if (!File.Exists("/usr/bin/git"))
            throw Refused("SandboxPushGitUnavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CommandTimeout);
        using var process = new Process {
            StartInfo = new ProcessStartInfo {
                FileName = "/usr/bin/git",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = working
            }
        };
        var env = process.StartInfo.Environment;
        env.Clear();
        env["PATH"] = "/usr/bin:/bin";
        env["HOME"] = "/nonexistent";
        env["GIT_CONFIG_GLOBAL"] = "/dev/null";
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GIT_NO_REPLACE_OBJECTS"] = "1";
        env["GIT_ALLOW_PROTOCOL"] = authHeader is null ? "file" : "https";
        env["GIT_CONFIG_COUNT"] = authHeader is null ? "4" : "7";
        env["GIT_CONFIG_KEY_0"] = "core.hooksPath";
        env["GIT_CONFIG_VALUE_0"] = "/dev/null";
        env["GIT_CONFIG_KEY_1"] = "protocol.allow";
        env["GIT_CONFIG_VALUE_1"] = "never";
        env["GIT_CONFIG_KEY_2"] = authHeader is null
            ? "protocol.file.allow" : "protocol.https.allow";
        env["GIT_CONFIG_VALUE_2"] = "always";
        env["GIT_CONFIG_KEY_3"] = "http.followRedirects";
        env["GIT_CONFIG_VALUE_3"] = "false";
        if (authHeader is not null)
        {
            env["GIT_CONFIG_KEY_4"] = "http.sslVerify";
            env["GIT_CONFIG_VALUE_4"] = "true";
            env["GIT_CONFIG_KEY_5"] = "credential.helper";
            env["GIT_CONFIG_VALUE_5"] = "";
            env["GIT_CONFIG_KEY_6"] = "http.https://github.com/" +
                options.AllowedOrganization + "/" + options.AllowedRepository +
                ".git.extraHeader";
            env["GIT_CONFIG_VALUE_6"] = authHeader;
        }
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        var started = false;
        try
        {
            if (!process.Start())
                throw Refused("SandboxPushGitUnavailable");
            started = true;
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            _ = await stderr; // Never return raw Git stderr or credentials.
            if (process.ExitCode != 0)
                throw Refused("SandboxPushOutcomeAmbiguous");
            return output;
        }
        catch (OperationCanceledException)
        {
            throw Refused("SandboxPushOutcomeAmbiguous");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw Refused("SandboxPushGitUnavailable");
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

    private static async Task<string> ReadBoundedAsync(
        StreamReader input, CancellationToken ct)
    {
        var output = new char[4096];
        var collected = new StringBuilder();
        while (true)
        {
            var read = await input.ReadAsync(output.AsMemory(), ct);
            if (read == 0) break;
            if (collected.Length + read > MaxOutput)
                throw Refused("SandboxPushOutputLimit");
            collected.Append(output, 0, read);
        }
        return collected.ToString();
    }

    private static GitHubSandboxOneShotException Refused(string code) =>
        new(code);
}
