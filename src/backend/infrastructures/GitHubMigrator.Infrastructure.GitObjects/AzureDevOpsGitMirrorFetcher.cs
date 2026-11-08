using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.GitObjects;

// Only this transport ever sees the PAT. It cannot accept arbitrary URLs;
// the URL is constructed from the immutable source snapshot by the caller.
public interface IAzureGitReadOnlyTransport
{
    Task FetchAsync(string bareDirectory, Uri azureUri,
        string personalAccessToken, CancellationToken ct);
}

public sealed class AzureDevOpsGitMirrorFetcher(
    string sourceRoot, IAzureGitReadOnlyTransport? transport = null)
    : IAzureGitMirrorFetcher
{
    public const long MaxMirrorBytes = 512L * 1024 * 1024;
    // A fast fail-closed admission gate protects CPU/disk from overlapping
    // authenticated requests on one API replica; per-job global fencing
    // needs the future leased remote-acquisition queue.
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);
    private static readonly Regex OrgPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$", RegexOptions.Compiled);
    private readonly IAzureGitReadOnlyTransport _transport =
        transport ?? new GitCliAzureReadOnlyTransport();

    public static Uri BuildTrustedAzureUri(
        string organization, string projectName, string repositoryName)
    {
        if (organization is null || !OrgPattern.IsMatch(organization) ||
            !ValidSegment(projectName) || !ValidSegment(repositoryName))
            throw new GitSourceBundleException("AzureGitSourceMappingInvalid");
        var uri = new Uri("https://dev.azure.com/" +
            Uri.EscapeDataString(organization) + "/" +
            Uri.EscapeDataString(projectName) + "/_git/" +
            Uri.EscapeDataString(repositoryName));
        if (uri.Host != "dev.azure.com" ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new GitSourceBundleException("AzureGitSourceMappingInvalid");
        return uri;
    }

    private static bool ValidSegment(string? name) =>
        name is { Length: >= 1 and <= 128 } &&
        name.Trim() == name && name is not ("." or "..") &&
        !name.Any(ch => char.IsControl(ch) ||
            ch is '/' or '\\' or '?' or '#' or '@' or ':' or '%');

    public async Task FetchAsync(AzureGitMirrorFetchRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await ProcessSlots.WaitAsync(0, ct))
            throw new GitSourceBundleException("AzureGitFetchCapacityExceeded");
        try { await FetchCoreAsync(request, ct); }
        finally { ProcessSlots.Release(); }
    }

    private async Task FetchCoreAsync(AzureGitMirrorFetchRequest request, CancellationToken ct)
    {
        if (request is null || request.Location is null ||
            request.Location.ConnectionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Location.RepositoryId) ||
            string.IsNullOrWhiteSpace(request.PersonalAccessToken) ||
            request.PersonalAccessToken.Length > 4096 ||
            request.PersonalAccessToken.Any(char.IsControl))
            throw new GitSourceBundleException("AzureGitSourceMappingInvalid");

        IReadOnlyList<GitMirrorRef> expected;
        try { expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(
            request.ExpectedRefs); }
        catch (GitMirrorRefReconciliationException)
        { throw new GitSourceBundleException("AzureGitExpectedRefsInvalid"); }
        if (expected.Count == 0)
            throw new GitSourceBundleException("EmptyBundleRefsUnsupported");
        var url = BuildTrustedAzureUri(request.Location.Organization,
            request.Location.ProjectName, request.Location.RepositoryName);

        // Root must be an absolute, operator provisioned, non-symlinked path.
        if (!Path.IsPathFullyQualified(sourceRoot) || !Directory.Exists(sourceRoot))
            throw new GitSourceBundleException("AzureGitSourceRootNotConfigured");
        CheckNoSymlinks(sourceRoot);
        var target = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
            sourceRoot, request.Location.ConnectionId,
            request.Location.RepositoryId);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        CheckNoSymlinks(parent);

        // Reuse a previously verified full bare mirror on retry. Its exact
        // refs + object graph are rechecked; never reset/mutate the original.
        if (Directory.Exists(target))
        {
            await VerifyMirrorAsync(target, expected, ct);
            return;
        }

        // Stage on the SAME filesystem; promotion is a single directory
        // rename. Failed/cancelled fetches are never visible as real mirrors.
        var staged = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await GitCliAzureReadOnlyTransport.RunLocalGitAsync(
                parent, null, ct, "init", "--bare", "--quiet",
                expected[0].ObjectId.Length == 64
                    ? "--object-format=sha256" : "--object-format=sha1",
                staged);
            await _transport.FetchAsync(staged, url,
                request.PersonalAccessToken, ct);
            await VerifyMirrorAsync(staged, expected, ct);
            if (Directory.Exists(target))
                throw new GitSourceBundleException("AzureGitMirrorAlreadyExists");
            Directory.Move(staged, target);
        }
        catch (OperationCanceledException) { throw; }
        catch (GitSourceBundleException) { throw; }
        catch (IOException)
        { throw new GitSourceBundleException("AzureGitLocalIoFailure"); }
        catch (UnauthorizedAccessException)
        { throw new GitSourceBundleException("AzureGitLocalIoFailure"); }
        finally
        {
            try { if (Directory.Exists(staged)) Directory.Delete(staged, true); }
            catch (IOException) { }
        }
    }

    private static async Task VerifyMirrorAsync(
        string bare, IReadOnlyList<GitMirrorRef> expected, CancellationToken ct)
    {
        CheckNoSymlinks(bare);
        if (!File.Exists(Path.Combine(bare, "HEAD")) ||
            !Directory.Exists(Path.Combine(bare, "objects")) ||
            !Directory.Exists(Path.Combine(bare, "refs")) ||
            File.Exists(Path.Combine(bare, "objects", "info", "alternates")))
            throw new GitSourceBundleException("AzureGitBareMirrorInvalid");
        foreach (var entry in Directory.EnumerateFileSystemEntries(
            bare, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                throw new GitSourceBundleException("AzureGitMirrorSymlinkBlocked");
        }
        var observed = await GitCliAzureReadOnlyTransport.RunLocalGitAsync(
            bare, null, ct, "--git-dir=" + bare, "for-each-ref",
            "--format=%(refname)%09%(objectname)");
        var actual = new List<GitMirrorRef>();
        foreach (var raw in observed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var idx = line.IndexOf('\t');
            if (idx <= 0 || idx >= line.Length - 1)
                throw new GitSourceBundleException("AzureGitRefOutputInvalid");
            actual.Add(new(line[..idx], line[(idx + 1)..]));
            if (actual.Count > GitMirrorRefReconciler.MaxRefsPerSide)
                throw new GitSourceBundleException("AzureGitRefLimitExceeded");
        }
        IReadOnlyList<GitMirrorRef> normalized;
        try { normalized = GitMirrorRefReconciler.CanonicalizeObservedRefs(actual); }
        catch (GitMirrorRefReconciliationException)
        { throw new GitSourceBundleException("AzureGitUnexpectedRef"); }
        if (!normalized.SequenceEqual(expected))
            throw new GitSourceBundleException("AzureGitSourceRefMismatch");
        var bytes = Directory.EnumerateFiles(bare, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        if (bytes > MaxMirrorBytes)
            throw new GitSourceBundleException("AzureGitMirrorSizeLimit");
        await GitCliAzureReadOnlyTransport.RunLocalGitAsync(
            bare, null, ct, "--git-dir=" + bare, "fsck",
            "--full", "--strict", "--no-reflogs", "--no-progress");
    }

    private static void CheckNoSymlinks(string path)
    {
        for (var cursor = new DirectoryInfo(Path.GetFullPath(path));
            cursor is not null; cursor = cursor.Parent)
            if (cursor.Exists && cursor.LinkTarget is not null)
                throw new GitSourceBundleException("AzureGitMirrorSymlinkBlocked");
    }
}

// Production transport: ONLY HTTPS dev.azure.com, no shell and no URL with
// embedded credentials. Basic PAT is carried only in a child environment
// variable (not command arguments, stdout, config files or the remote URL).
public sealed class GitCliAzureReadOnlyTransport : IAzureGitReadOnlyTransport
{
    public static readonly TimeSpan FetchDeadline = TimeSpan.FromSeconds(115);
    private const int MaxOutputChars = 12 * 1024 * 1024;

    public static ProcessStartInfo CreateFetchStartInfo(
        string bare, Uri url, string token)
    {
        if (url is null || url.Scheme != Uri.UriSchemeHttps ||
            url.Host != "dev.azure.com" || !url.IsDefaultPort ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Query) ||
            !string.IsNullOrEmpty(url.Fragment) ||
            string.IsNullOrWhiteSpace(token) || token.Length > 4096 ||
            token.Any(char.IsControl))
            throw new GitSourceBundleException("AzureGitTransportRejected");

        var start = NewStartInfo(bare);
        start.ArgumentList.Add("--git-dir=" + bare);
        start.ArgumentList.Add("fetch");
        start.ArgumentList.Add("--no-tags");
        start.ArgumentList.Add("--no-recurse-submodules");
        start.ArgumentList.Add("--no-write-fetch-head");
        start.ArgumentList.Add("--no-auto-maintenance");
        start.ArgumentList.Add(url.AbsoluteUri);
        // Fetch is read-only with respect to Azure DevOps and writes only
        // into a freshly created, local bare repository.
        start.ArgumentList.Add("+refs/heads/*:refs/heads/*");
        start.ArgumentList.Add("+refs/tags/*:refs/tags/*");

        var auth = "AUTHORIZATION: Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(":" + token));
        var keys = new[] {
            ("credential.helper", ""),
            ("http.followRedirects", "false"),
            ("http.sslVerify", "true"),
            ("protocol.allow", "never"),
            ("protocol.https.allow", "always"),
            ("http.extraHeader", auth)
        };
        start.Environment["GIT_CONFIG_COUNT"] = keys.Length.ToString();
        for (var i = 0; i < keys.Length; i++)
        {
            start.Environment["GIT_CONFIG_KEY_" + i] = keys[i].Item1;
            start.Environment["GIT_CONFIG_VALUE_" + i] = keys[i].Item2;
        }
        start.Environment["GIT_ALLOW_PROTOCOL"] = "https";
        return start;
    }

    public Task FetchAsync(
        string bareDirectory, Uri azureUri,
        string personalAccessToken, CancellationToken ct) =>
        RunAsync(CreateFetchStartInfo(
            bareDirectory, azureUri, personalAccessToken),
            bareDirectory, ct, FetchDeadline);

    internal static Task<string> RunLocalGitAsync(
        string cwd, string? unused, CancellationToken ct, params string[] args)
    {
        var start = NewStartInfo(cwd);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return RunAsync(start, null, ct, TimeSpan.FromSeconds(70));
    }

    private static ProcessStartInfo NewStartInfo(string cwd)
    {
        var start = new ProcessStartInfo {
            FileName = "/usr/bin/git", WorkingDirectory = cwd,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["HOME"] = "/nonexistent";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_CONFIG_COUNT"] = "2";
        start.Environment["GIT_CONFIG_KEY_0"] = "protocol.allow";
        start.Environment["GIT_CONFIG_VALUE_0"] = "never";
        start.Environment["GIT_CONFIG_KEY_1"] = "core.hooksPath";
        start.Environment["GIT_CONFIG_VALUE_1"] = "/dev/null";
        return start;
    }

    private static async Task<string> RunAsync(
        ProcessStartInfo start, string? monitorDirectory,
        CancellationToken ct, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start())
                throw new GitSourceBundleException("AzureGitProcessUnavailable");
            started = true;
            var stdout = DrainAsync(process.StandardOutput, deadline.Token);
            var stderr = DrainAsync(process.StandardError, deadline.Token);
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (stdout.IsFaulted) await stdout;
                if (stderr.IsFaulted) await stderr;
                if (monitorDirectory is not null &&
                    Directory.Exists(monitorDirectory))
                {
                    long total = 0;
                    foreach (var file in Directory.EnumerateFiles(
                        monitorDirectory, "*", SearchOption.AllDirectories))
                    {
                        total += new FileInfo(file).Length;
                        if (total > AzureDevOpsGitMirrorFetcher.MaxMirrorBytes)
                            throw new GitSourceBundleException("AzureGitMirrorSizeLimit");
                    }
                }
                await Task.Delay(250, deadline.Token);
            }
            await process.WaitForExitAsync(deadline.Token);
            var result = await stdout;
            _ = await stderr; // Do not expose Git stderr, even on failure.
            if (process.ExitCode != 0)
                throw new GitSourceBundleException("AzureGitFetchOrFsckFailed");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new GitSourceBundleException("AzureGitFetchDeadline"); }
        catch (Win32Exception)
        { throw new GitSourceBundleException("AzureGitProcessUnavailable"); }
        finally
        {
            if (started && !process.HasExited)
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader input, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var n = await input.ReadAsync(buffer.AsMemory(), ct);
            if (n == 0) return result.ToString();
            if (result.Length + n > MaxOutputChars)
                throw new GitSourceBundleException("AzureGitOutputLimit");
            result.Append(buffer, 0, n);
        }
    }
}
