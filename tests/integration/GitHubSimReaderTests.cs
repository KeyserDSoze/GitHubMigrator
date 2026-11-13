using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhSim;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.GitHub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The GitHub simulator (tools/GhSim) served in-process. The product's own GitHub readers are pointed
// at it through GitHubEndpoints, exactly as a deployment points them at GitHub Enterprise Server.
public sealed class GhSimFixture : IAsyncLifetime
{
    public const string Pat = "gh-sim-pat";
    private string _dir = "";
    public WebApplication App { get; private set; } = null!;
    public string Origin { get; private set; } = "";
    public GitHubEndpoints Endpoints => new(Origin + "/api/v3", Origin);
    public HttpClient Http { get; } = new();

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ghsim-" + Guid.NewGuid().ToString("N"));
        App = await GhSimApp.StartAsync(new GhSimOptions(GhSeed.Load(null), _dir, "http://127.0.0.1:0"));
        Origin = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        GhStore.ForceDelete(_dir);
    }

    public async Task<JsonElement> SendAsync(string method, string path, object? body = null, string token = Pat)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), (path.StartsWith("/_sim") ? Origin : Origin + "/api/v3") + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode < 300, $"{method} {path} -> {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<string> CreateRepoAsync(string prefix)
    {
        var name = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        await SendAsync("POST", "/orgs/orion-org/repos", new { name, @private = true });
        return name;
    }

    public string RemoteUrl(string repo, string token = Pat) =>
        $"http://x-access-token:{token}@{new Uri(Origin).Authority}/orion-org/{repo}.git";

    public static string Git(string? workDir, params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workDir ?? Path.GetTempPath() };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_AUTHOR_NAME"] = info.Environment["GIT_COMMITTER_NAME"] = "Test";
        info.Environment["GIT_AUTHOR_EMAIL"] = info.Environment["GIT_COMMITTER_EMAIL"] = "test@example.test";
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {output}{error.Result}");
        return output.Trim();
    }

    // main with two commits, a feature branch, a lightweight tag and an annotated tag.
    public string PushSampleRepository(string repo, string? token = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ghsim-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
        Git(dir, "add", ".");
        Git(dir, "commit", "-qm", "c1");
        File.AppendAllText(Path.Combine(dir, "a.txt"), "two\n");
        Git(dir, "commit", "-qam", "c2");
        Git(dir, "branch", "feature/x");
        Git(dir, "tag", "light");
        Git(dir, "tag", "-a", "v1", "-m", "release v1");
        Git(dir, "push", "-q", "--atomic", RemoteUrl(repo, token ?? Pat), "main", "feature/x", "refs/tags/light", "refs/tags/v1");
        return dir;
    }
}

public sealed class GitHubReadersAgainstSimTests(GhSimFixture sim) : IClassFixture<GhSimFixture>
{
    private HttpClient NewClient() => new();
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public async Task Connection_validator_verifies_a_token_against_the_configured_endpoints()
    {
        var validator = new GitHubConnectionValidator(NewClient(), sim.Endpoints);
        var ok = await validator.ValidateAsync("orion-org", GhSimFixture.Pat, CancellationToken.None);
        Assert.True(ok.IsValid);
        Assert.Equal("sim-admin", ok.AuthenticatedAs);
        Assert.Equal("Unauthorized", (await validator.ValidateAsync("orion-org", "wrong", CancellationToken.None)).Code);
        Assert.Equal("OrganizationNotFound", (await validator.ValidateAsync("missing-org", GhSimFixture.Pat, CancellationToken.None)).Code);
    }

    [Fact]
    public async Task Destination_access_reader_confirms_membership_role_and_org_policy()
    {
        var reader = new GitHubDestinationAccessReader(NewClient(), sim.Endpoints);
        var admin = await reader.ReadAsync("orion-org", "sim-admin", GhSimFixture.Pat, CancellationToken.None);
        Assert.Equal(("Active", "Owner", "AllowedForMembers"), (admin.MembershipStatus, admin.MembershipRole, admin.PrivateRepositoryPolicy));
        var locked = await reader.ReadAsync("locked-org", "sim-admin", GhSimFixture.Pat, CancellationToken.None);
        Assert.Equal(("Active", "Member", "RestrictedForMembers"), (locked.MembershipStatus, locked.MembershipRole, locked.PrivateRepositoryPolicy));
        Assert.Equal("IdentityMismatch", (await reader.ReadAsync("orion-org", "someone-else", GhSimFixture.Pat, CancellationToken.None)).MembershipStatus);
        Assert.Equal("NotConfirmed", (await reader.ReadAsync("missing-org", "sim-admin", GhSimFixture.Pat, CancellationToken.None)).MembershipStatus);
        Assert.Equal("Unauthorized", (await reader.ReadAsync("orion-org", "sim-admin", "wrong", CancellationToken.None)).MembershipStatus);
    }

    [Fact]
    public async Task Target_probe_and_import_readers_observe_repositories_and_default_branch_policy()
    {
        var probe = new GitHubTargetRepositoryProbe(NewClient(), sim.Endpoints);
        Assert.Equal("Exists", await probe.ProbeAsync("orion-org", "existing-repo", GhSimFixture.Pat, CancellationToken.None));
        Assert.Equal("NotVisibleOrMissing", await probe.ProbeAsync("orion-org", "nope-" + Guid.NewGuid().ToString("N")[..6], GhSimFixture.Pat, CancellationToken.None));

        var repository = new GitHubImportRepositoryReader(NewClient(), Clock, sim.Endpoints);
        var visible = await repository.ObserveAsync("orion-org", "existing-repo", GhSimFixture.Pat, CancellationToken.None);
        Assert.Equal(("Visible", "ReportedAllowed", false, false), (visible.Status, visible.ReportedPush, visible.Archived, visible.Disabled));
        Assert.True((await repository.ObserveAsync("orion-org", "archived-repo", GhSimFixture.Pat, CancellationToken.None)).Archived);

        var policy = new GitHubImportPolicyReader(NewClient(), Clock, sim.Endpoints);
        var plain = await policy.InspectAsync("orion-org", "existing-repo", GhSimFixture.Pat, CancellationToken.None);
        Assert.Equal(("Observed", 0, 0), (plain.RulesetStatus, plain.RulesetCount, plain.ActiveRulesets));
        var protectedRepo = await policy.InspectAsync("orion-org", "protected-repo", GhSimFixture.Pat, CancellationToken.None);
        Assert.Equal(("Observed", 1, 1), (protectedRepo.RulesetStatus, protectedRepo.RulesetCount, protectedRepo.ActiveRulesets));
    }

    [Fact]
    public async Task Configuration_reader_lists_actions_secret_and_variable_names_only()
    {
        var names = await new GitHubConfigurationReader(NewClient(), sim.Endpoints)
            .GetNamesAsync("orion-org", "configured-repo", GhSimFixture.Pat, CancellationToken.None);
        Assert.Equal(["DEPLOY_PASSWORD"], names.Secrets);
        Assert.Equal(["REGION"], names.Variables);
    }

    [Fact]
    public async Task Ref_observation_reads_heads_and_annotated_tag_objects_after_a_real_push()
    {
        var repo = await sim.CreateRepoAsync("refs");
        var source = sim.PushSampleRepository(repo);
        var observation = await new GitHubRefObservationReader(NewClient(), Clock, sim.Endpoints)
            .ReadAsync("orion-org", repo, GhSimFixture.Pat, CancellationToken.None);
        var byName = observation.Refs.ToDictionary(r => r.Name);
        Assert.Equal(4, byName.Count);
        Assert.Equal(("commit", GhSimFixture.Git(source, "rev-parse", "main")), (byName["refs/heads/main"].ObjectType, byName["refs/heads/main"].ObjectId));
        Assert.Equal(("tag", GhSimFixture.Git(source, "rev-parse", "v1")), (byName["refs/tags/v1"].ObjectType, byName["refs/tags/v1"].ObjectId));
        Assert.Equal("commit", byName["refs/tags/light"].ObjectType);
    }

    [Fact]
    public async Task Remote_tip_reader_walks_annotated_tag_chains_to_commit_and_tree_ids()
    {
        var repo = await sim.CreateRepoAsync("tips");
        var source = sim.PushSampleRepository(repo);
        var candidates = new[]
        {
            new GitMirrorRef("refs/heads/main", GhSimFixture.Git(source, "rev-parse", "main")),
            new GitMirrorRef("refs/tags/v1", GhSimFixture.Git(source, "rev-parse", "v1"))
        };
        var snapshot = await new GitHubRemoteTipObjectReader(NewClient(), Clock, sim.Endpoints)
            .ReadAsync("orion-org", repo, GhSimFixture.Pat, candidates, CancellationToken.None);
        var tag = snapshot.TipReadback.Tips.Single(t => t.RefName == "refs/tags/v1");
        Assert.Equal(("tag", 1), (tag.RefObjectType, tag.TagObjectsTraversed));
        Assert.Equal(GhSimFixture.Git(source, "rev-parse", "v1^{commit}"), tag.CommitObjectId);
        Assert.Equal(GhSimFixture.Git(source, "rev-parse", "v1^{tree}"), tag.CommitTreeId);
        Assert.Equal(0, snapshot.TipReadback.Tips.Single(t => t.RefName == "refs/heads/main").TagObjectsTraversed);
    }

    [Fact]
    public async Task Traceability_reader_tells_issues_commits_and_pull_requests_apart()
    {
        var repo = await sim.CreateRepoAsync("trace");
        var source = sim.PushSampleRepository(repo);
        await sim.SendAsync("POST", $"/repos/orion-org/{repo}/issues", new { title = "an issue" });
        GhSimFixture.Git(source, "checkout", "-q", "-b", "work");
        File.WriteAllText(Path.Combine(source, "b.txt"), "b");
        GhSimFixture.Git(source, "add", ".");
        GhSimFixture.Git(source, "commit", "-qm", "work");
        GhSimFixture.Git(source, "push", "-q", sim.RemoteUrl(repo), "work");
        await sim.SendAsync("POST", $"/repos/orion-org/{repo}/pulls", new { title = "a pr", head = "work", @base = "main" });
        var reader = new GitHubTraceabilityReader(NewClient(), sim.Endpoints);
        Assert.True(await reader.IssueExistsAsync("orion-org", repo, 1, GhSimFixture.Pat, CancellationToken.None));
        // a pull request shares the number space with issues but is not an issue
        Assert.False(await reader.IssueExistsAsync("orion-org", repo, 2, GhSimFixture.Pat, CancellationToken.None));
        Assert.True(await reader.PullRequestExistsAsync("orion-org", repo, 2, GhSimFixture.Pat, CancellationToken.None));
        Assert.False(await reader.PullRequestExistsAsync("orion-org", repo, 1, GhSimFixture.Pat, CancellationToken.None));
        Assert.True(await reader.CommitExistsAsync("orion-org", repo, GhSimFixture.Git(source, "rev-parse", "main"), GhSimFixture.Pat, CancellationToken.None));
        Assert.False(await reader.IssueExistsAsync("orion-org", repo, 99, GhSimFixture.Pat, CancellationToken.None));
        // GitHub answers 422 "No commit found for SHA" (not 404) for a commit that was not migrated yet
        Assert.False(await reader.CommitExistsAsync("orion-org", repo, new string('a', 40), GhSimFixture.Pat, CancellationToken.None));
        // any other 422 stays unknown, never "absent"
        await sim.SendAsync("POST", "/_sim/faults", new { pathRegex = "/commits/", status = 422, message = "Validation Failed", remaining = 1 });
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            reader.CommitExistsAsync("orion-org", repo, new string('a', 40), GhSimFixture.Pat, CancellationToken.None));
    }

    [Fact]
    public async Task Candidate_policy_reader_reports_rules_per_branch_and_tag()
    {
        var reader = new GitHubCandidatePolicyReader(NewClient(), Clock, sim.Endpoints);
        var repoId = (await sim.SendAsync("GET", "/repos/orion-org/protected-repo")).GetProperty("id").GetInt64();
        var candidates = new[]
        {
            new GitMirrorRef("refs/heads/main", new string('a', 40)),
            new GitMirrorRef("refs/heads/other", new string('b', 40)),
            new GitMirrorRef("refs/tags/v1", new string('c', 40))
        };
        var result = await reader.ReadAsync("orion-org", "protected-repo", repoId, GhSimFixture.Pat, candidates, CancellationToken.None);
        Assert.Equal("Observed", result.RulesetCatalogStatus);
        var states = result.Items.Select(i => (i.Kind, i.Status)).ToArray();
        Assert.Equal([
            ("Branch", GitHubCandidatePolicyStates.RulesRequireReview),
            ("Branch", GitHubCandidatePolicyStates.NoActiveRulesReported),
            ("Tag", GitHubCandidatePolicyStates.NoActiveRulesReported)], states);
    }
}

public sealed class GitHubAppAgainstSimTests(GhSimFixture sim) : IClassFixture<GhSimFixture>
{
    [Fact]
    public async Task The_github_app_inspector_mints_verifies_and_hands_a_usable_one_repository_token()
    {
        using var key = RSA.Create(2048);
        var appId = Random.Shared.NextInt64(10_000, 900_000);
        var installationId = appId + 1;
        await sim.SendAsync("POST", "/_sim/apps", new
        {
            id = appId, slug = "migrator", publicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
            installations = new[] { new { id = installationId, org = "orion-org", repositorySelection = "selected", contents = "write" } }
        });
        var repo = await sim.CreateRepoAsync("app");
        var repoId = (await sim.SendAsync("GET", $"/repos/orion-org/{repo}")).GetProperty("id").GetInt64();
        var inspector = new GitHubAppScopedWriteCredentialInspector(new HttpClient(), TimeProvider.System, sim.Endpoints);
        var pem = key.ExportPkcs8PrivateKeyPem();

        // F-043: mint-and-discard attestation
        var observation = await inspector.InspectAsync(appId, installationId, pem, "orion-org", repo, CancellationToken.None);
        Assert.Equal((installationId, repoId), (observation.InstallationId, observation.RepositoryId));
        Assert.InRange((observation.TokenExpiresAtUtc - DateTimeOffset.UtcNow).TotalMinutes, 50, 61);

        // F-083: the token is available only inside the callback and really authorizes a git push
        var source = Path.Combine(Path.GetTempPath(), "ghsim-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        GhSimFixture.Git(source, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(source, "x"), "x");
        GhSimFixture.Git(source, "add", ".");
        GhSimFixture.Git(source, "commit", "-qm", "x");
        var outcome = await inspector.WithVerifiedTokenAsync(appId, installationId, pem, "orion-org", repo, repoId,
            (token, _) =>
            {
                GhSimFixture.Git(source, "push", "-q", "--atomic", sim.RemoteUrl(repo, token), "main");
                return Task.FromResult(default(GitHubSandboxOneShotOutcome)!);
            }, CancellationToken.None);
        Assert.Equal(GhSimFixture.Git(source, "rev-parse", "main"),
            (await sim.SendAsync("GET", $"/repos/orion-org/{repo}/git/ref/heads/main")).GetProperty("object").GetProperty("sha").GetString());

        // a wrong repository id (e.g. a swapped repository) must be refused by the product's own check
        await Assert.ThrowsAsync<GitHubSandboxOneShotException>(() => inspector.WithVerifiedTokenAsync(
            appId, installationId, pem, "orion-org", repo, repoId + 1, (_, _) => Task.FromResult(default(GitHubSandboxOneShotOutcome)!), CancellationToken.None));
    }
}
