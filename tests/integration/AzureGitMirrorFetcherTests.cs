using System.Diagnostics;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Production builds only a fixed dev.azure.com URL. A test transport
// explicitly substitutes a temporary local Git repository, so these tests
// transfer REAL Git objects without accessing external credentials/network.
public sealed class AzureGitMirrorFetcherTests
{
    [Fact]
    public async Task Fixed_host_fetches_real_branch_and_tag_objects_then_reuses_verified_mirror()
    {
        using var env = new GitFixture();
        var expected = env.MakeSource();
        var transport = new LocalFixtureTransport(env.UpstreamBare);
        var adapter = new AzureDevOpsGitMirrorFetcher(env.SourceRoot, transport);
        var source = env.Request(expected);
        await adapter.FetchAsync(source, default);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("https", transport.LastUri!.Scheme);
        Assert.Equal("dev.azure.com", transport.LastUri.Host);
        Assert.Contains("/az-org/Project%20Alpha/_git/Repo%20Alpha",
            transport.LastUri.AbsoluteUri);
        Assert.Equal("source-secret-never-log", transport.LastToken);

        var mirror = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
            env.SourceRoot, env.ConnectionId, env.RepositoryId);
        Assert.True(File.Exists(Path.Combine(mirror, "HEAD")));
        Assert.Equal(expected[0].ObjectId, RunGit("--git-dir=" + mirror,
            "rev-parse", "refs/heads/main").Trim());
        Assert.Equal(expected[1].ObjectId, RunGit("--git-dir=" + mirror,
            "rev-parse", "refs/tags/v1").Trim());
        Assert.Equal("hello from Azure\n", RunGit("--git-dir=" + mirror,
            "show", "refs/heads/main:README.md"));
        var config = File.ReadAllText(Path.Combine(mirror, "config"));
        Assert.DoesNotContain("source-secret-never-log", config);
        Assert.DoesNotContain("dev.azure.com", config);
        Assert.DoesNotContain("extraHeader", config, StringComparison.OrdinalIgnoreCase);

        // Successful retry re-verifies local refs + all reachable objects;
        // it does not request Azure again or overwrite the committed mirror.
        await adapter.FetchAsync(source, default);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(Directory.GetDirectories(
            Path.GetDirectoryName(mirror)!, "*.tmp-*"));
    }

    [Fact]
    public async Task Unexpected_refs_and_oid_drift_fail_before_mirror_promotion()
    {
        using var env = new GitFixture();
        var expected = env.MakeSource();
        var transport = new LocalFixtureTransport(env.UpstreamBare);
        var adapter = new AzureDevOpsGitMirrorFetcher(env.SourceRoot, transport);
        var fake = expected.Select(x => x with {
            ObjectId = new string('f', 40)
        }).ToArray();
        var ex = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            adapter.FetchAsync(env.Request(fake), default));
        Assert.Equal("AzureGitSourceRefMismatch", ex.Code);
        Assert.False(Directory.Exists(LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
            env.SourceRoot, env.ConnectionId, env.RepositoryId)));

        RunGit("--git-dir=" + env.UpstreamBare,
            "update-ref", "refs/heads/secret-branch", expected[0].ObjectId);
        ex = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            adapter.FetchAsync(env.Request(expected), default));
        Assert.Equal("AzureGitSourceRefMismatch", ex.Code);
        Assert.False(Directory.Exists(LocalGitSourceBundleAcquirer.LocalMirrorDirectory(
            env.SourceRoot, env.ConnectionId, env.RepositoryId)));
    }

    [Fact]
    public async Task Invalid_source_paths_and_expired_cancellation_do_not_contact_transport()
    {
        using var env = new GitFixture();
        var expected = env.MakeSource();
        var transport = new LocalFixtureTransport(env.UpstreamBare);
        var adapter = new AzureDevOpsGitMirrorFetcher(env.SourceRoot, transport);
        var wrong = env.Request(expected) with {
            Location = env.Location with { ProjectName = "../malicious" }
        };
        var ex = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            adapter.FetchAsync(wrong, default));
        Assert.Equal("AzureGitSourceMappingInvalid", ex.Code);
        Assert.Equal(0, transport.Calls);

        var link = Path.Combine(env.TempRoot, "linked-source");
        Directory.CreateSymbolicLink(link, env.SourceRoot);
        var unsafeAdapter = new AzureDevOpsGitMirrorFetcher(link, transport);
        ex = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            unsafeAdapter.FetchAsync(env.Request(expected), default));
        Assert.Equal("AzureGitMirrorSymlinkBlocked", ex.Code);
        Assert.Equal(0, transport.Calls);

        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.FetchAsync(env.Request(expected), aborted.Token));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void Git_transport_never_places_secret_in_argv_or_remote_url()
    {
        const string token = "the-PAT-must-not-appear-in-command-line";
        var root = Path.Combine(Path.GetTempPath(), "fixed-git-dir");
        var url = AzureDevOpsGitMirrorFetcher.BuildTrustedAzureUri(
            "az-org", "Project Alpha", "Repo Alpha");
        var psi = GitCliAzureReadOnlyTransport.CreateFetchStartInfo(
            root, url, token);
        Assert.Equal("/usr/bin/git", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal("https", psi.Environment["GIT_ALLOW_PROTOCOL"]);
        Assert.Equal("false", psi.Environment["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("true", psi.Environment["GIT_CONFIG_VALUE_2"]);
        Assert.Equal("never", psi.Environment["GIT_CONFIG_VALUE_3"]);
        Assert.Equal("always", psi.Environment["GIT_CONFIG_VALUE_4"]);
        Assert.Contains("AUTHORIZATION: Basic ",
            psi.Environment["GIT_CONFIG_VALUE_5"]);
        Assert.DoesNotContain(token, string.Join(" ", psi.ArgumentList));
        Assert.DoesNotContain(token, url.AbsoluteUri);
        Assert.DoesNotContain("push", psi.ArgumentList);

        foreach (var invalid in new[] {
            "https://evil.dev.azure.com/myorg/p/_git/r",
            "https://dev.azure.com.evil.invalid/myorg/p/_git/r",
            "http://dev.azure.com/myorg/p/_git/r",
            "https://user:token@dev.azure.com/myorg/p/_git/r",
            "https://dev.azure.com:8443/myorg/p/_git/r",
            "https://dev.azure.com/myorg/p/_git/r?x=1"
        })
        {
            var ex = Assert.Throws<GitSourceBundleException>(() =>
                GitCliAzureReadOnlyTransport.CreateFetchStartInfo(
                    root, new Uri(invalid), token));
            Assert.Equal("AzureGitTransportRejected", ex.Code);
        }
    }

    private sealed class LocalFixtureTransport(string upstream)
        : IAzureGitReadOnlyTransport
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? LastToken { get; private set; }
        public Task FetchAsync(
            string bareDirectory, Uri azureUri,
            string personalAccessToken, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            LastUri = azureUri;
            LastToken = personalAccessToken;
            RunGit("--git-dir=" + bareDirectory, "fetch",
                "--no-tags", "--no-write-fetch-head", upstream,
                "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*");
            return Task.CompletedTask;
        }
    }

    private sealed class GitFixture : IDisposable
    {
        public string TempRoot { get; } =
            Path.Combine(Path.GetTempPath(), "f036-git-" + Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(TempRoot, "source-mirrors");
        public string UpstreamBare => Path.Combine(TempRoot, "remote.git");
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public string RepositoryId => "repo-source-id";
        public AzureGitSourceLocation Location =>
            new(ConnectionId, "az-org", "Project Alpha", "Repo Alpha", RepositoryId);

        public GitFixture()
        {
            Directory.CreateDirectory(TempRoot);
            Directory.CreateDirectory(SourceRoot);
        }

        public IReadOnlyList<GitMirrorRef> MakeSource()
        {
            var work = Path.Combine(TempRoot, "work");
            RunGit("init", "-q", work);
            RunGit("-C", work, "config", "user.email", "source@example.invalid");
            RunGit("-C", work, "config", "user.name", "Source Test");
            File.WriteAllText(Path.Combine(work, "README.md"), "hello from Azure\n");
            RunGit("-C", work, "add", "README.md");
            RunGit("-C", work, "commit", "-q", "-m", "first");
            RunGit("-C", work, "branch", "-M", "main");
            RunGit("-C", work, "tag", "v1");
            RunGit("clone", "--bare", "-q", work, UpstreamBare);
            var commit = RunGit("-C", work, "rev-parse", "HEAD").Trim();
            return [new("refs/heads/main", commit), new("refs/tags/v1", commit)];
        }

        public AzureGitMirrorFetchRequest Request(IReadOnlyList<GitMirrorRef> expected) =>
            new(Location, "source-secret-never-log", expected);

        public void Dispose()
        {
            try { Directory.Delete(TempRoot, true); }
            catch (IOException) { }
        }
    }

    private static string RunGit(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = "/usr/bin/git";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("git unavailable");
        var stdout = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000) || process.ExitCode != 0)
            throw new InvalidOperationException("test Git fixture failed");
        return stdout;
    }
}
