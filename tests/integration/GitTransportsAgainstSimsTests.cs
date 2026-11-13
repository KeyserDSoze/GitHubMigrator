using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.GitHub;
using GitHubMigrator.Infrastructure.GitObjects;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The real git transports (Azure DevOps fetch, GitHub create-only push, GitHub read-only closure mirror)
// against the two simulators over real HTTP. No transport is mocked.
public static class GitTestSupport
{
    public static string Git(params string[] args)
    {
        using var process = new Process();
        process.StartInfo.FileName = "/usr/bin/git";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {output}{error.Result}");
        return output;
    }

    public static IReadOnlyList<GitMirrorRef> MirrorRefs(string gitDir) =>
        GitMirrorRefReconciler.CanonicalizeObservedRefs(
            Git("--git-dir=" + gitDir, "for-each-ref", "--format=%(refname)%09%(objectname)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.TrimEnd('\r').Split('\t'))
                .Select(x => new GitMirrorRef(x[0], x[1])).ToArray());

    public static string NewTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteQuietly(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class AzureDevOpsFetchAgainstAdoSimTests(AdoSimFixture sim) : IClassFixture<AdoSimFixture>, IDisposable
{
    private static readonly ServerEndpointPolicy Allow = new(["127.0.0.1"], allowHttp: true);
    private readonly List<string> _directories = [];
    private readonly HttpClient _http = NewClient();

    private static HttpClient NewClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + AdoSimFixture.Pat)));
        return http;
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var directory in _directories) GitTestSupport.DeleteQuietly(directory);
    }

    private string Temp(string prefix)
    {
        var path = GitTestSupport.NewTempDirectory(prefix);
        _directories.Add(path);
        return path;
    }

    private async Task<(AzureGitSourceLocation Location, IReadOnlyList<GitMirrorRef> Expected)> LocateAsync(
        string project, string repository)
    {
        var root = $"{sim.ServerRoot}/DefaultCollection/{Uri.EscapeDataString(project)}/_apis/git/repositories";
        using var list = JsonDocument.Parse(await _http.GetStringAsync($"{root}?api-version=7.0"));
        var id = list.RootElement.GetProperty("value").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == repository).GetProperty("id").GetString()!;
        using var refs = JsonDocument.Parse(await _http.GetStringAsync($"{root}/{id}/refs?api-version=7.0"));
        var expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(refs.RootElement.GetProperty("value").EnumerateArray()
            .Select(r => new GitMirrorRef(r.GetProperty("name").GetString()!, r.GetProperty("objectId").GetString()!)).ToArray());
        var location = new AzureGitSourceLocation(Guid.NewGuid(), "DefaultCollection", project, repository, id,
            ProviderKeys.AzureDevOpsServer, sim.ServerRoot);
        return (location, expected);
    }

    private static AzureDevOpsGitMirrorFetcher Fetcher(string sourceRoot, ServerEndpointPolicy? server) =>
        new(sourceRoot, null, new AzureGitRemotePolicy(null, server));

    [Fact]
    public async Task A_real_git_fetch_from_an_azure_devops_server_produces_a_verified_exact_mirror()
    {
        var (location, expected) = await LocateAsync("Alpha Platform", "alpha-api");
        var sourceRoot = Temp("ado-fetch-");
        await Fetcher(sourceRoot, Allow).FetchAsync(
            new AzureGitMirrorFetchRequest(location, AdoSimFixture.Pat, expected), default);

        var mirror = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(sourceRoot, location.ConnectionId, location.RepositoryId);
        Assert.Equal(expected, GitTestSupport.MirrorRefs(mirror));
        Assert.Contains(expected, r => r.Name == "refs/heads/feature/auth/login");
        Assert.Contains(expected, r => r.Name == "refs/tags/v2.0.0");
        // a second request reuses (and re-verifies) the mirror instead of fetching again
        await Fetcher(sourceRoot, Allow).FetchAsync(
            new AzureGitMirrorFetchRequest(location, AdoSimFixture.Pat, expected), default);
    }

    [Theory]
    [InlineData("cloud-only")]
    [InlineData("host-not-allow-listed")]
    [InlineData("http-not-allowed")]
    [InlineData("missing-server-root")]
    public async Task The_fetch_refuses_remotes_the_egress_policy_does_not_allow(string scenario)
    {
        var (location, expected) = await LocateAsync("Alpha Platform", "alpha-api");
        var sourceRoot = Temp("ado-fetch-refused-");
        var policy = scenario switch
        {
            "cloud-only" => null,
            "host-not-allow-listed" => new ServerEndpointPolicy(["ado.corp.test"], allowHttp: true),
            "http-not-allowed" => new ServerEndpointPolicy(["127.0.0.1"], allowHttp: false),
            _ => Allow
        };
        if (scenario == "missing-server-root") location = location with { ServerRoot = null };
        var error = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            Fetcher(sourceRoot, policy).FetchAsync(new AzureGitMirrorFetchRequest(location, AdoSimFixture.Pat, expected), default));
        Assert.Equal("AzureGitSourceMappingInvalid", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(sourceRoot));
    }

    [Fact]
    public async Task A_wrong_credential_or_a_changed_source_never_leaves_a_mirror_behind()
    {
        var (location, expected) = await LocateAsync("Alpha Platform", "alpha-web");
        var sourceRoot = Temp("ado-fetch-bad-");
        var denied = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            Fetcher(sourceRoot, Allow).FetchAsync(new AzureGitMirrorFetchRequest(location, "wrong-pat", expected), default));
        Assert.Equal("AzureGitFetchOrFsckFailed", denied.Code);

        // a commit pushed after the refs were sealed: the mirror no longer matches the proof
        using var commit = await _http.PostAsync(
            $"{sim.Origin}/_sim/repos/{Uri.EscapeDataString("Alpha Platform")}/alpha-web/commits",
            new StringContent(JsonSerializer.Serialize(new { branch = "master", message = "late change", files = new Dictionary<string, string> { ["late.txt"] = "x" } }),
                Encoding.UTF8, "application/json"));
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var drift = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
            Fetcher(sourceRoot, Allow).FetchAsync(new AzureGitMirrorFetchRequest(location, AdoSimFixture.Pat, expected), default));
        Assert.Equal("AzureGitSourceRefMismatch", drift.Code);
        var promoted = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(sourceRoot, location.ConnectionId, location.RepositoryId);
        Assert.False(Directory.Exists(promoted));
    }
}

public sealed class MigrationPathAgainstSimsTests(AdoSimFixture ado, GhSimFixture gh)
    : IClassFixture<AdoSimFixture>, IClassFixture<GhSimFixture>, IDisposable
{
    private static readonly ServerEndpointPolicy Allow = new(["127.0.0.1"], allowHttp: true);
    private readonly string _root = GitTestSupport.NewTempDirectory("migration-path-");

    public void Dispose() => GitTestSupport.DeleteQuietly(_root);

    private static string Sha(char c) => new(c, 64);

    // Source: alpha-api on the Azure DevOps simulator. Target: a fresh repository on the GitHub simulator.
    // Components: AzureDevOpsGitMirrorFetcher -> LocalGitSourceBundleAcquirer -> GitHubAppScopedWriteCredentialInspector
    // (real JWT + installation token) -> GitHubSandboxCreateOnlyPushRunner -> GitHubReadOnlyObjectMirrorRunner.
    [Fact]
    public async Task A_repository_travels_from_azure_devops_server_to_github_through_the_guarded_components()
    {
        // 1. fetch the source mirror over HTTP from the Azure DevOps Server simulator
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + AdoSimFixture.Pat)));
        var repoRoot = $"{ado.ServerRoot}/DefaultCollection/{Uri.EscapeDataString("Alpha Platform")}/_apis/git/repositories";
        using var list = JsonDocument.Parse(await http.GetStringAsync($"{repoRoot}?api-version=7.0"));
        var sourceRepositoryId = list.RootElement.GetProperty("value").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "alpha-api").GetProperty("id").GetString()!;
        using var refsJson = JsonDocument.Parse(await http.GetStringAsync($"{repoRoot}/{sourceRepositoryId}/refs?api-version=7.0"));
        var sealedRefs = GitMirrorRefReconciler.CanonicalizeObservedRefs(refsJson.RootElement.GetProperty("value").EnumerateArray()
            .Select(r => new GitMirrorRef(r.GetProperty("name").GetString()!, r.GetProperty("objectId").GetString()!)).ToArray());

        var connectionId = Guid.NewGuid();
        var sourceRoot = Path.Combine(_root, "source-root");
        Directory.CreateDirectory(sourceRoot);
        await new AzureDevOpsGitMirrorFetcher(sourceRoot, null, new AzureGitRemotePolicy(null, Allow)).FetchAsync(
            new AzureGitMirrorFetchRequest(new(connectionId, "DefaultCollection", "Alpha Platform", "alpha-api",
                sourceRepositoryId, ProviderKeys.AzureDevOpsServer, ado.ServerRoot), AdoSimFixture.Pat, sealedRefs), default);
        var source = LocalGitSourceBundleAcquirer.LocalMirrorDirectory(sourceRoot, connectionId, sourceRepositoryId);
        var all = GitTestSupport.MirrorRefs(source);
        Assert.Equal(sealedRefs, all);

        // 2. the GitHub target: empty repository on the simulator, baseline = the default branch only
        var repo = "alpha-api-" + Guid.NewGuid().ToString("N")[..8];
        await gh.SendAsync("POST", "/orgs/orion-org/repos", new { name = repo, @private = true });
        var repositoryId = (await gh.SendAsync("GET", $"/repos/orion-org/{repo}")).GetProperty("id").GetInt64();
        GitTestSupport.Git("--git-dir=" + source, "push", "--quiet", gh.RemoteUrl(repo), "refs/heads/main:refs/heads/main");
        var creates = all.Where(x => x.Name != "refs/heads/main").ToArray();
        Assert.True(creates.Length >= 8);

        var observer = new GitHubRefObservationReader(new HttpClient(), TimeProvider.System, gh.Endpoints);
        var baseline = await observer.ReadAsync("orion-org", repo, GhSimFixture.Pat, default);
        Assert.Equal(["refs/heads/main"], baseline.Refs.Select(r => r.Name));
        var baselineSha = GitHubRemoteRefFenceIntegrity.Fingerprint(baseline.Refs);

        // 3. the source bundle (F-035) and the sealed create-only proof objects
        var workpackId = Guid.NewGuid();
        var bundleRoot = Path.Combine(_root, "bundles");
        Directory.CreateDirectory(bundleRoot);
        var artifact = await new LocalGitSourceBundleAcquirer(sourceRoot, bundleRoot).AcquireAsync(
            new(workpackId, connectionId, sourceRepositoryId, Sha('3'), Sha('2'), all), default);
        var packet = new GitHubCreateOnlyPacket(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            workpackId, repositoryId, "orion-org", repo,
            Sha('1'), artifact.BundleSha256, Sha('3'), Sha('4'), Sha('5'), Sha('6'), Sha('7'), Sha('8'), Sha('a'),
            GitImportRehearsalIntegrity.SealRefs("f044-create-only-candidate-refs-v1", creates),
            Sha('9'), creates.Length, 1, 0, GitHubCreateOnlyPacketIntegrity.State, false, false, false, false);
        var bundle = new GitSourceBundleView(workpackId, packet.PlanId, Guid.NewGuid(), sourceRepositoryId, Sha('2'),
            packet.ReconciliationSha256, artifact.BundleFileName, artifact.BundleSha256, artifact.BundleBytes,
            artifact.RefCount, artifact.AcquiredAtUtc, true, false);
        var draft = new GitHubRemoteSandboxPreSendBarrierReceipt(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            repositoryId, Guid.NewGuid(), Sha('b'), Sha('c'), Sha('d'), packet.EvidenceSha256, packet.CandidateSetSha256,
            Sha('e'), DateTimeOffset.UtcNow, "", GitHubRemoteSandboxPreSendBarrierIntegrity.Recorded, false, false, false, false);
        var marker = draft with { EvidenceSha256 = GitHubRemoteSandboxPreSendBarrierIntegrity.Digest(draft) };
        var input = new GitHubSandboxPushInput(new(packet, creates), bundle, marker, baselineSha, baseline);

        // 4. a real GitHub App installation token (JWT RS256 checked by the simulator) feeds the guarded push
        using var key = RSA.Create(2048);
        var appId = Random.Shared.NextInt64(10_000, 900_000);
        var installationId = appId + 1;
        await gh.SendAsync("POST", "/_sim/apps", new
        {
            id = appId, slug = "migrator", publicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
            installations = new[] { new { id = installationId, org = "orion-org", repositorySelection = "selected", contents = "write" } }
        });
        var scratch = Path.Combine(_root, "push-scratch");
        Directory.CreateDirectory(scratch);
        var pushRunner = new GitHubSandboxCreateOnlyPushRunner(bundleRoot, scratch,
            new GitHubSandboxOneShotOptions(true, repositoryId, "orion-org", repo), gh.Endpoints);
        var inspector = new GitHubAppScopedWriteCredentialInspector(new HttpClient(), TimeProvider.System, gh.Endpoints);
        await inspector.WithVerifiedTokenAsync(appId, installationId, key.ExportPkcs8PrivateKeyPem(), "orion-org", repo, repositoryId,
            async (token, ct) =>
            {
                await pushRunner.PushOnceAsync(input, token, ct);
                return default!;
            }, default);
        Assert.Empty(Directory.GetFileSystemEntries(scratch));

        // 5. the target now holds exactly the source refs (annotated tags stay tag objects) and is a valid repository
        var mirror = Path.Combine(_root, "target-mirror.git");
        GitTestSupport.Git("clone", "--mirror", "--quiet", gh.RemoteUrl(repo), mirror);
        GitTestSupport.Git("--git-dir=" + mirror, "fsck", "--full", "--strict", "--no-reflogs", "--no-progress");
        Assert.Equal(all, GitTestSupport.MirrorRefs(mirror));
        var typed = await observer.ReadAsync("orion-org", repo, GhSimFixture.Pat, default);
        Assert.Contains(typed.Refs, r => r.Name == "refs/tags/v2.0.0" && r.ObjectType == "tag");

        // 6. independent read-only verification of every reachable object through the configured endpoints
        var closureSource = Path.Combine(_root, "closure-source");
        var closureScratch = Path.Combine(_root, "closure-scratch");
        Directory.CreateDirectory(closureSource);
        Directory.CreateDirectory(closureScratch);
        var sourceMirrorId = Guid.NewGuid();
        GitTestSupport.Git("clone", "--mirror", "--quiet", source, Path.Combine(closureSource, sourceMirrorId.ToString("N") + ".git"));
        var closure = await new GitHubReadOnlyObjectMirrorRunner(closureSource, closureScratch,
                new GitHubReadOnlyObjectMirrorOptions(true, repositoryId, "orion-org/" + repo), TimeProvider.System, gh.Endpoints)
            .VerifyAsync(new GitHubReadOnlyObjectMirrorRequest(sourceMirrorId, repositoryId, "orion-org", repo, all, typed,
                DateTimeOffset.UtcNow.AddMinutes(30)), GhSimFixture.Pat, default);
        Assert.True(closure.GitHubFetchPerformed);
        Assert.False(closure.RemotePushInvoked);
        Assert.Equal(closure.SourceObjectSetSha256, closure.RemoteObjectSetSha256);
        Assert.Empty(Directory.GetFileSystemEntries(closureScratch));

        // 7. a replay of the same proof is refused (at-most-once)
        var replay = await Assert.ThrowsAsync<GitHubSandboxOneShotException>(() => pushRunner.PushOnceAsync(input, "ephemeral-replay-token", default));
        Assert.Equal("SandboxPushAlreadyAttempted", replay.Code);
    }

    [Fact]
    public async Task A_repository_rule_on_the_target_rejects_the_whole_atomic_push_and_nothing_is_created()
    {
        var work = Path.Combine(_root, "work");
        GitTestSupport.Git("init", "-q", "-b", "main", work);
        File.WriteAllText(Path.Combine(work, "a.txt"), "a");
        GitTestSupport.Git("-C", work, "add", ".");
        GitTestSupport.Git("-C", work, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-qm", "c1");
        GitTestSupport.Git("-C", work, "branch", "feature");
        GitTestSupport.Git("-C", work, "-c", "user.name=t", "-c", "user.email=t@example.test", "tag", "-a", "v1", "-m", "r");

        var repo = "rule-" + Guid.NewGuid().ToString("N")[..8];
        await gh.SendAsync("POST", "/orgs/orion-org/repos", new { name = repo, @private = true });
        GitTestSupport.Git("-C", work, "push", "--quiet", gh.RemoteUrl(repo), "main");
        await gh.SendAsync("POST", $"/_sim/repos/orion-org/{repo}/rulesets",
            new { name = "no-tags", enforcement = "active", target = "tag", include = new[] { "~ALL" }, rules = new[] { "creation" } });

        // the same atomic command the guarded runner issues: one rejected ref rejects all of them
        using var push = new Process();
        push.StartInfo.FileName = "/usr/bin/git";
        push.StartInfo.RedirectStandardError = true;
        push.StartInfo.RedirectStandardOutput = true;
        push.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        foreach (var arg in new[] { "-C", work, "push", "--atomic", gh.RemoteUrl(repo), "refs/heads/feature", "refs/tags/v1" })
            push.StartInfo.ArgumentList.Add(arg);
        push.Start();
        var error = push.StandardError.ReadToEnd();
        push.WaitForExit();
        Assert.NotEqual(0, push.ExitCode);
        Assert.Contains("GH013", error);

        var refs = (await gh.SendAsync("GET", $"/repos/orion-org/{repo}/git/matching-refs/heads")).EnumerateArray()
            .Select(r => r.GetProperty("ref").GetString()).ToArray();
        Assert.Equal(["refs/heads/main"], refs);
        Assert.Equal(0, (await gh.SendAsync("GET", $"/repos/orion-org/{repo}/git/matching-refs/tags")).GetArrayLength());
    }
}
