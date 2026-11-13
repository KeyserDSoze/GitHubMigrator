using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GhSim.Tests.GhSimFixture;

namespace GhSim.Tests;

public sealed class GhSimRestTests(GhSimFixture sim) : IClassFixture<GhSimFixture>
{
    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Requests_without_valid_credentials_are_rejected_like_github()
    {
        Assert.Equal(401, (await sim.Get("/user", token: null)).Status);
        var bad = await sim.Get("/user", token: "nope");
        Assert.Equal(401, bad.Status);
        Assert.Equal("Bad credentials", bad.Json.GetProperty("message").GetString());
        Assert.Equal("sim-admin", (await sim.Get("/user")).Json.GetProperty("login").GetString());
    }

    [Fact]
    public async Task Organization_membership_and_policy_are_visible_only_to_members()
    {
        var admin = await sim.Get("/user/memberships/orgs/orion-org");
        Assert.Equal("active", admin.Json.GetProperty("state").GetString());
        Assert.Equal("admin", admin.Json.GetProperty("role").GetString());
        Assert.Equal("member", (await sim.Get("/user/memberships/orgs/orion-org", "gh-sim-member-pat")).Json.GetProperty("role").GetString());
        Assert.Equal("member", (await sim.Get("/user/memberships/orgs/locked-org")).Json.GetProperty("role").GetString());
        Assert.Equal(404, (await sim.Get("/user/memberships/orgs/missing-org")).Status);

        Assert.True((await sim.Get("/orgs/orion-org")).Json.GetProperty("members_can_create_private_repositories").GetBoolean());
        Assert.False((await sim.Get("/orgs/locked-org")).Json.GetProperty("members_can_create_private_repositories").GetBoolean());
    }

    [Fact]
    public async Task Repository_creation_validates_names_conflicts_and_org_policy()
    {
        var name = Unique("created");
        var created = await sim.Post("/orgs/orion-org/repos", new { name, @private = true, description = "d" });
        Assert.Equal(201, created.Status);
        Assert.Equal($"orion-org/{name}", created.Json.GetProperty("full_name").GetString());
        Assert.Equal("main", created.Json.GetProperty("default_branch").GetString());
        Assert.False(created.Json.GetProperty("archived").GetBoolean());
        Assert.False(created.Json.GetProperty("disabled").GetBoolean());
        Assert.True(created.Json.GetProperty("permissions").GetProperty("push").GetBoolean());
        Assert.Equal("orion-org", created.Json.GetProperty("owner").GetProperty("login").GetString());

        Assert.Equal(422, (await sim.Post("/orgs/orion-org/repos", new { name = name.ToUpperInvariant() })).Status);
        Assert.Equal(422, (await sim.Post("/orgs/orion-org/repos", new { name = "bad/name" })).Status);
        Assert.Equal(422, (await sim.Post("/orgs/orion-org/repos", new { name = "repo.git" })).Status);
        var spaced = await sim.Post("/orgs/orion-org/repos", new { name = $"has space {Guid.NewGuid():N}"[..20] });
        Assert.Equal(201, spaced.Status);
        Assert.DoesNotContain(' ', spaced.Json.GetProperty("name").GetString()!);

        // members may not create private repositories where the organization forbids it
        Assert.Equal(403, (await sim.Post("/orgs/locked-org/repos", new { name = Unique("p"), @private = true })).Status);
        Assert.Equal(403, (await sim.Post("/orgs/orion-org/repos", new { name = Unique("ro") }, "gh-sim-readonly-pat")).Status);
        Assert.Equal(404, (await sim.Post("/orgs/missing-org/repos", new { name = "x" })).Status);
    }

    [Fact]
    public async Task Hidden_repositories_answer_404_and_archived_ones_refuse_writes()
    {
        Assert.Equal(200, (await sim.Get("/repos/orion-org/existing-repo")).Status);
        Assert.Equal(404, (await sim.Get("/repos/orion-org/does-not-exist")).Status);
        Assert.Equal(404, (await sim.Get("/repos/locked-org/anything")).Status);
        var archived = await sim.Get("/repos/orion-org/archived-repo");
        Assert.True(archived.Json.GetProperty("archived").GetBoolean());
        Assert.False(archived.Json.GetProperty("permissions").GetProperty("push").GetBoolean());
        var write = await sim.Put("/repos/orion-org/archived-repo/contents/x.txt", new { message = "m", content = Base64("x") });
        Assert.Equal(403, write.Status);
    }

    [Fact]
    public async Task Rate_limited_and_failing_responses_can_be_injected_and_requests_are_logged()
    {
        var name = Unique("fault");
        await sim.CreateRepoAsync(name);
        Assert.Equal(204, (await sim.SendAsync("DELETE", "/_sim/requests")).Status);
        Assert.Equal(201, (await sim.Post("/_sim/faults", new { pathRegex = $"/repos/orion-org/{name}$", status = 500, remaining = 1 })).Status);
        Assert.Equal(500, (await sim.Get($"/repos/orion-org/{name}")).Status);
        Assert.Equal(200, (await sim.Get($"/repos/orion-org/{name}")).Status);

        await sim.Post("/_sim/faults", new { pathRegex = "/user$", rateLimited = true, remaining = 1 });
        var limited = await sim.Get("/user");
        Assert.Equal(403, limited.Status);
        Assert.Equal("0", limited.Headers.GetValues("X-RateLimit-Remaining").Single());

        var log = (await sim.Get("/_sim/requests?path=/repos/orion-org/" + name)).Json;
        Assert.Equal([500, 200], log.EnumerateArray().Select(e => e.GetProperty("Status").GetInt32()).ToArray());
        await sim.Delete("/_sim/faults");
    }

    [Fact]
    public async Task Contents_api_creates_updates_conflicts_and_deletes_files_with_real_commits()
    {
        var slug = await sim.CreateRepoAsync(Unique("contents"));
        var first = await sim.Put($"/repos/{slug}/contents/docs/a.md", new { message = "add a", content = Base64("# a\n") });
        Assert.Equal(201, first.Status);
        var blob = first.Json.GetProperty("content").GetProperty("sha").GetString()!;
        Assert.Equal("add a", first.Json.GetProperty("commit").GetProperty("message").GetString());

        // an update must name the blob it replaces
        var missing = await sim.Put($"/repos/{slug}/contents/docs/a.md", new { message = "x", content = Base64("# b\n") });
        Assert.Equal(422, missing.Status);
        Assert.Equal(409, (await sim.Put($"/repos/{slug}/contents/docs/a.md", new { message = "x", content = Base64("# b\n"), sha = new string('a', 40) })).Status);
        var updated = await sim.Put($"/repos/{slug}/contents/docs/a.md", new { message = "edit a", content = Base64("# b\n"), sha = blob });
        Assert.Equal(200, updated.Status);

        var file = await sim.Get($"/repos/{slug}/contents/docs/a.md");
        Assert.Equal("base64", file.Json.GetProperty("encoding").GetString());
        Assert.Equal("# b\n", Encoding.UTF8.GetString(Convert.FromBase64String(file.Json.GetProperty("content").GetString()!.Replace("\n", ""))));
        Assert.Equal("dir", (await sim.Get($"/repos/{slug}/contents/")).Json[0].GetProperty("type").GetString());
        Assert.Equal("# b\n", (await sim.SendAsync("GET", $"/repos/{slug}/contents/docs/a.md", accept: "application/vnd.github.raw")).Text);

        var current = file.Json.GetProperty("sha").GetString();
        Assert.Equal(200, (await sim.SendAsync("DELETE", $"/repos/{slug}/contents/docs/a.md", new { message = "rm", sha = current })).Status);
        Assert.Equal(404, (await sim.Get($"/repos/{slug}/contents/docs/a.md")).Status);
        Assert.Equal(404, (await sim.Put($"/repos/{slug}/contents/a.txt", new { message = "m", content = Base64("x"), branch = "nope" })).Status);
    }

    [Fact]
    public async Task Pull_requests_validate_branches_merge_for_real_and_appear_as_issues()
    {
        var slug = await sim.CreateRepoAsync(Unique("pulls"));
        await sim.Put($"/repos/{slug}/contents/README.md", new { message = "init", content = Base64("# r\n") });
        var head = (await sim.Get($"/repos/{slug}/git/ref/heads/main")).Json.GetProperty("object").GetProperty("sha").GetString();
        Assert.Equal(201, (await sim.Post($"/repos/{slug}/git/refs", new { @ref = "refs/heads/work", sha = head })).Status);

        Assert.Equal(422, (await sim.Post($"/repos/{slug}/pulls", new { title = "t", head = "work", @base = "main" })).Status);
        await sim.Put($"/repos/{slug}/contents/CHANGES.md", new { message = "change", content = Base64("c\n"), branch = "work" });
        Assert.Equal(422, (await sim.Post($"/repos/{slug}/pulls", new { title = "t", head = "ghost", @base = "main" })).Status);

        var pull = await sim.Post($"/repos/{slug}/pulls", new { title = "Add changes", head = "work", @base = "main", body = "b" });
        Assert.Equal(201, pull.Status);
        Assert.Equal(1, pull.Json.GetProperty("number").GetInt32());
        Assert.Equal("open", pull.Json.GetProperty("state").GetString());
        Assert.Equal(422, (await sim.Post($"/repos/{slug}/pulls", new { title = "again", head = "work", @base = "main" })).Status);

        var asIssue = await sim.Get($"/repos/{slug}/issues/1");
        Assert.True(asIssue.Json.TryGetProperty("pull_request", out _));

        // the list is narrowed by head=owner:branch and base=branch, as on GitHub
        var owner = slug.Split('/')[0];
        Assert.Single((await sim.Get($"/repos/{slug}/pulls?state=open&head={owner}:work")).Json.EnumerateArray());
        Assert.Empty((await sim.Get($"/repos/{slug}/pulls?state=open&head={owner}:other")).Json.EnumerateArray());
        Assert.Empty((await sim.Get($"/repos/{slug}/pulls?state=open&head=someone-else:work")).Json.EnumerateArray());
        Assert.Single((await sim.Get($"/repos/{slug}/pulls?base=main")).Json.EnumerateArray());
        Assert.Empty((await sim.Get($"/repos/{slug}/pulls?base=develop")).Json.EnumerateArray());

        Assert.Equal(200, (await sim.Put($"/repos/{slug}/pulls/1/merge", new { merge_method = "merge" })).Status);
        Assert.True((await sim.Get($"/repos/{slug}/pulls/1")).Json.GetProperty("merged").GetBoolean());
        Assert.Equal(200, (await sim.Get($"/repos/{slug}/contents/CHANGES.md")).Status);
        Assert.Equal(405, (await sim.Put($"/repos/{slug}/pulls/1/merge", new { })).Status);
    }

    [Fact]
    public async Task Issues_support_creation_comments_state_and_stable_numbers()
    {
        var slug = await sim.CreateRepoAsync(Unique("issues"));
        var one = await sim.Post($"/repos/{slug}/issues", new { title = "Migrated bug", body = "<!-- azdo-workitem:x:y:105 -->", labels = new[] { "bug" } });
        Assert.Equal(201, one.Status);
        Assert.Equal(1, one.Json.GetProperty("number").GetInt32());
        Assert.Equal("bug", one.Json.GetProperty("labels")[0].GetProperty("name").GetString());
        Assert.Equal(2, (await sim.Post($"/repos/{slug}/issues", new { title = "second" })).Json.GetProperty("number").GetInt32());
        Assert.Equal(201, (await sim.Post($"/repos/{slug}/issues/1/comments", new { body = "hello" })).Status);
        var closed = await sim.SendAsync("PATCH", $"/repos/{slug}/issues/1", new { state = "closed" });
        Assert.Equal("closed", closed.Json.GetProperty("state").GetString());
        Assert.Equal(1, (await sim.Get($"/repos/{slug}/issues?state=open")).Json.GetArrayLength());
        Assert.Equal(2, (await sim.Get($"/repos/{slug}/issues?state=all")).Json.GetArrayLength());
        Assert.False((await sim.Get($"/repos/{slug}/issues/1")).Json.TryGetProperty("pull_request", out _));
    }

    [Fact]
    public async Task Actions_secrets_variables_environments_and_workflows_behave_like_github()
    {
        var slug = await sim.CreateRepoAsync(Unique("actions"));
        var key = (await sim.Get($"/repos/{slug}/actions/secrets/public-key")).Json;
        var keyId = key.GetProperty("key_id").GetString();
        Assert.Equal(32, Convert.FromBase64String(key.GetProperty("key").GetString()!).Length);

        // plaintext or wrongly sealed values never reach GitHub secrets
        Assert.Equal(422, (await sim.Put($"/repos/{slug}/actions/secrets/DEPLOY", new { key_id = keyId, encrypted_value = Base64("plain") })).Status);
        var sealedValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(80));
        Assert.Equal(422, (await sim.Put($"/repos/{slug}/actions/secrets/DEPLOY", new { key_id = "wrong", encrypted_value = sealedValue })).Status);
        Assert.Equal(422, (await sim.Put($"/repos/{slug}/actions/secrets/GITHUB_TOKEN", new { key_id = keyId, encrypted_value = sealedValue })).Status);
        Assert.Equal(201, (await sim.Put($"/repos/{slug}/actions/secrets/DEPLOY", new { key_id = keyId, encrypted_value = sealedValue })).Status);
        Assert.Equal(204, (await sim.Put($"/repos/{slug}/actions/secrets/DEPLOY", new { key_id = keyId, encrypted_value = sealedValue })).Status);
        var secrets = (await sim.Get($"/repos/{slug}/actions/secrets")).Json;
        Assert.Equal(1, secrets.GetProperty("total_count").GetInt32());
        Assert.False(secrets.GetProperty("secrets")[0].TryGetProperty("value", out _));

        Assert.Equal(201, (await sim.Post($"/repos/{slug}/actions/variables", new { name = "REGION", value = "weu" })).Status);
        Assert.Equal(409, (await sim.Post($"/repos/{slug}/actions/variables", new { name = "REGION", value = "x" })).Status);
        Assert.Equal(204, (await sim.SendAsync("PATCH", $"/repos/{slug}/actions/variables/REGION", new { value = "neu" })).Status);
        Assert.Equal("neu", (await sim.Get($"/repos/{slug}/actions/variables/REGION")).Json.GetProperty("value").GetString());
        Assert.Equal(204, (await sim.Delete($"/repos/{slug}/actions/variables/REGION")).Status);

        Assert.Equal(200, (await sim.Put($"/repos/{slug}/environments/prod", new { wait_timer = 5 })).Status);
        var repoId = (await sim.Get($"/repos/{slug}")).Json.GetProperty("id").GetInt64();
        Assert.Equal(201, (await sim.Post($"/repositories/{repoId}/environments/prod/variables", new { name = "STAGE", value = "p" })).Status);
        Assert.Equal(1, (await sim.Get($"/repos/{slug}/environments")).Json.GetProperty("total_count").GetInt32());

        // workflows are discovered from the default branch, titled by their name:
        var yaml = "name: Build and test\non: push\njobs:\n  b:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n";
        await sim.Put($"/repos/{slug}/contents/.github/workflows/ci.yml", new { message = "ci", content = Base64(yaml) });
        var workflows = (await sim.Get($"/repos/{slug}/actions/workflows")).Json;
        Assert.Equal(1, workflows.GetProperty("total_count").GetInt32());
        Assert.Equal("Build and test", workflows.GetProperty("workflows")[0].GetProperty("name").GetString());
        Assert.Equal(".github/workflows/ci.yml", workflows.GetProperty("workflows")[0].GetProperty("path").GetString());

        // a token without user rights (contents-only installation) cannot manage secrets: see GhSimAppTests
        Assert.Equal(403, (await sim.Get($"/repos/{slug}/actions/secrets", "gh-sim-readonly-pat")).Status);
    }

    [Fact]
    public async Task Pagination_uses_link_headers_so_clients_exercise_their_paging()
    {
        var slug = await sim.CreateRepoAsync(Unique("paging"));
        var key = (await sim.Get($"/repos/{slug}/actions/secrets/public-key")).Json.GetProperty("key_id").GetString();
        for (var i = 0; i < 5; i++)
            await sim.Put($"/repos/{slug}/actions/secrets/S{i}", new { key_id = key, encrypted_value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) });
        var page1 = await sim.Get($"/repos/{slug}/actions/secrets?per_page=2&page=1");
        Assert.Equal(5, page1.Json.GetProperty("total_count").GetInt32());
        Assert.Equal(2, page1.Json.GetProperty("secrets").GetArrayLength());
        Assert.Contains("rel=\"next\"", string.Join(",", page1.Headers.GetValues("Link")));
        var page3 = await sim.Get($"/repos/{slug}/actions/secrets?per_page=2&page=3");
        Assert.Equal(1, page3.Json.GetProperty("secrets").GetArrayLength());
        Assert.False(page3.Headers.Contains("Link"));
    }
}

public sealed class GhSimGitTests(GhSimFixture sim) : IClassFixture<GhSimFixture>
{
    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task An_atomic_push_of_branches_and_tags_round_trips_with_identical_object_ids()
    {
        var slug = await sim.CreateRepoAsync(Unique("push"));
        var source = NewLocalRepo();
        var push = Git(source, "push", "--atomic", sim.RemoteUrl(slug), "main", "feature/x", "refs/tags/light", "refs/tags/v1");
        Assert.True(push.Exit == 0, push.Output);

        var local = MustGit(source, "for-each-ref", "--format=%(refname) %(objecttype) %(objectname)")
            .Split('\n').OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var remote = new List<string>();
        foreach (var kind in new[] { "heads", "tags" })
            foreach (var r in (await sim.Get($"/repos/{slug}/git/matching-refs/{kind}")).Json.EnumerateArray())
                remote.Add($"{r.GetProperty("ref").GetString()} {r.GetProperty("object").GetProperty("type").GetString()} {r.GetProperty("object").GetProperty("sha").GetString()}");
        Assert.Equal(local, remote.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Contains(remote, r => r.StartsWith("refs/tags/v1 tag "));
        Assert.Contains(remote, r => r.StartsWith("refs/tags/light commit "));

        var mirror = Path.Combine(Path.GetTempPath(), "ghsim-mirror-" + Guid.NewGuid().ToString("N"));
        MustGit(null, "clone", "-q", "--mirror", sim.RemoteUrl(slug), mirror);
        MustGit(mirror, "fsck", "--strict");
        Assert.Equal(MustGit(source, "rev-parse", "main"), MustGit(mirror, "rev-parse", "main"));
    }

    [Fact]
    public async Task Tag_and_commit_object_endpoints_resolve_annotated_tag_chains()
    {
        var slug = await sim.CreateRepoAsync(Unique("objects"));
        var source = NewLocalRepo();
        MustGit(source, "push", "-q", "--atomic", sim.RemoteUrl(slug), "main", "refs/tags/v1");
        var tagSha = MustGit(source, "rev-parse", "v1");
        var commitSha = MustGit(source, "rev-parse", "v1^{commit}");
        var treeSha = MustGit(source, "rev-parse", "v1^{tree}");

        var tag = (await sim.Get($"/repos/{slug}/git/tags/{tagSha}")).Json;
        Assert.Equal(tagSha, tag.GetProperty("sha").GetString());
        Assert.Equal("commit", tag.GetProperty("object").GetProperty("type").GetString());
        Assert.Equal(commitSha, tag.GetProperty("object").GetProperty("sha").GetString());
        var commit = (await sim.Get($"/repos/{slug}/git/commits/{commitSha}")).Json;
        Assert.Equal(commitSha, commit.GetProperty("sha").GetString());
        Assert.Equal(treeSha, commit.GetProperty("tree").GetProperty("sha").GetString());
        Assert.Equal(404, (await sim.Get($"/repos/{slug}/git/tags/{commitSha}")).Status);
        Assert.Equal(commitSha, (await sim.Get($"/repos/{slug}/commits/{commitSha}")).Json.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Credentials_and_permissions_decide_between_401_403_and_404_for_git_too()
    {
        var slug = await sim.CreateRepoAsync(Unique("auth"));
        var source = NewLocalRepo();
        var anonymous = Git(source, "ls-remote", $"http://{new Uri(sim.Origin).Authority}/{slug}.git");
        Assert.NotEqual(0, anonymous.Exit);
        Assert.NotEqual(0, Git(source, "ls-remote", sim.RemoteUrl(slug, "bad-token")).Exit);

        var readonlyPush = Git(source, "push", sim.RemoteUrl(slug, "gh-sim-readonly-pat"), "main");
        Assert.NotEqual(0, readonlyPush.Exit);
        Assert.Contains("403", readonlyPush.Output);
        Assert.Equal(0, Git(source, "ls-remote", sim.RemoteUrl(slug, "gh-sim-readonly-pat")).Exit);

        var invisible = Git(source, "ls-remote", sim.RemoteUrl("locked-org/nothing"));
        Assert.Contains("not found", invisible.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task First_pushed_branch_becomes_default_when_the_default_branch_is_absent()
    {
        var slug = await sim.CreateRepoAsync(Unique("default"));
        var source = NewLocalRepo();
        MustGit(source, "branch", "-m", "main", "trunk");
        MustGit(source, "push", "-q", sim.RemoteUrl(slug), "trunk");
        Assert.Equal("trunk", (await sim.Get($"/repos/{slug}")).Json.GetProperty("default_branch").GetString());
    }

    [Fact]
    public async Task Rulesets_block_non_fast_forward_and_deletion_for_real_and_allow_new_branches()
    {
        var source = NewLocalRepo();
        var url = sim.RemoteUrl("orion-org/protected-repo");
        var seeded = (await sim.Get("/repos/orion-org/protected-repo/git/ref/heads/main")).Json.GetProperty("object").GetProperty("sha").GetString();

        var force = Git(source, "push", "--force", url, "main:main");
        Assert.NotEqual(0, force.Exit);
        Assert.Contains("GH013", force.Output);
        Assert.Contains("non-fast-forward", force.Output);

        var delete = Git(source, "push", url, ":main");
        Assert.NotEqual(0, delete.Exit);
        Assert.Contains("GH013", delete.Output);

        // nothing moved, and unrelated refs are still creatable
        Assert.Equal(seeded, (await sim.Get("/repos/orion-org/protected-repo/git/ref/heads/main")).Json.GetProperty("object").GetProperty("sha").GetString());
        var branchName = Unique("extra");
        Assert.Equal(0, Git(source, "push", url, $"main:refs/heads/{branchName}").Exit);

        // REST writes obey the same rules
        Assert.Equal(422, (await sim.Delete("/repos/orion-org/protected-repo/git/refs/heads/main")).Status);
        var rules = (await sim.Get("/repos/orion-org/protected-repo/rules/branches/main")).Json;
        Assert.Equal(["deletion", "non_fast_forward"], rules.EnumerateArray().Select(r => r.GetProperty("type").GetString()).ToArray());
        Assert.Equal(0, (await sim.Get("/repos/orion-org/protected-repo/rules/branches/other")).Json.GetArrayLength());
        Assert.Equal(1, (await sim.Get("/repos/orion-org/protected-repo/rulesets?includes_parents=true")).Json.GetArrayLength());
    }

    [Fact]
    public async Task Rulesets_added_at_runtime_are_enforced_on_the_next_push()
    {
        var slug = await sim.CreateRepoAsync(Unique("runtime-rule"));
        var source = NewLocalRepo();
        Assert.Equal(0, Git(source, "push", sim.RemoteUrl(slug), "main").Exit);
        var added = await sim.Post($"/_sim/repos/{slug}/rulesets", new { name = "no-new-tags", enforcement = "active", target = "tag", include = new[] { "~ALL" }, rules = new[] { "creation" } });
        Assert.Equal(201, added.Status);
        var tag = Git(source, "push", sim.RemoteUrl(slug), "refs/tags/light");
        Assert.NotEqual(0, tag.Exit);
        Assert.Contains("GH013", tag.Output);
        Assert.Equal(0, Git(source, "push", sim.RemoteUrl(slug), "main:refs/heads/free").Exit);
    }

    [Fact]
    public async Task Ref_endpoints_create_update_and_delete_with_fast_forward_checks()
    {
        var slug = await sim.CreateRepoAsync(Unique("refs"));
        var source = NewLocalRepo();
        MustGit(source, "push", "-q", sim.RemoteUrl(slug), "main");
        var tip = MustGit(source, "rev-parse", "main");
        var parent = MustGit(source, "rev-parse", "main~1");
        Assert.Equal(201, (await sim.Post($"/repos/{slug}/git/refs", new { @ref = "refs/heads/topic", sha = parent })).Status);
        Assert.Equal(422, (await sim.Post($"/repos/{slug}/git/refs", new { @ref = "refs/heads/topic", sha = parent })).Status);
        Assert.Equal(422, (await sim.Post($"/repos/{slug}/git/refs", new { @ref = "refs/heads/ghost", sha = new string('0', 40) })).Status);
        Assert.Equal(200, (await sim.SendAsync("PATCH", $"/repos/{slug}/git/refs/heads/topic", new { sha = tip })).Status);
        Assert.Equal(422, (await sim.SendAsync("PATCH", $"/repos/{slug}/git/refs/heads/topic", new { sha = parent })).Status);
        Assert.Equal(200, (await sim.SendAsync("PATCH", $"/repos/{slug}/git/refs/heads/topic", new { sha = parent, force = true })).Status);
        Assert.Equal(204, (await sim.Delete($"/repos/{slug}/git/refs/heads/topic")).Status);
        Assert.Equal(404, (await sim.Get($"/repos/{slug}/git/ref/heads/topic")).Status);
    }

    [Fact]
    public async Task A_workflow_pushed_with_git_is_listed_for_the_repository()
    {
        var slug = await sim.CreateRepoAsync(Unique("wf"));
        var source = NewLocalRepo();
        Directory.CreateDirectory(Path.Combine(source, ".github", "workflows"));
        File.WriteAllText(Path.Combine(source, ".github", "workflows", "azdo-11-ci.yml"), "name: Migrated pipeline 11\non: workflow_dispatch\njobs: {}\n");
        MustGit(source, "add", ".");
        MustGit(source, "commit", "-qm", "workflow");
        MustGit(source, "push", "-q", sim.RemoteUrl(slug), "main");
        var list = (await sim.Get($"/repos/{slug}/actions/workflows")).Json;
        Assert.Equal("Migrated pipeline 11", list.GetProperty("workflows")[0].GetProperty("name").GetString());
        var id = list.GetProperty("workflows")[0].GetProperty("id").GetInt64();
        Assert.Equal(200, (await sim.Get($"/repos/{slug}/actions/workflows/{id}")).Status);
        Assert.Equal(200, (await sim.Get($"/repos/{slug}/actions/workflows/azdo-11-ci.yml")).Status);
    }
}

public sealed class GhSimAppTests(GhSimFixture sim) : IClassFixture<GhSimFixture>
{
    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(RSA key, long appId, int lifetimeSeconds = 300)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        { iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds(), iss = appId.ToString() }));
        var signature = key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(signature);
    }

    private async Task<(RSA Key, long AppId, long InstallationId)> RegisterAppAsync(string contents = "write", string pullRequests = "none", string workflows = "none")
    {
        var key = RSA.Create(2048);
        var appId = Random.Shared.NextInt64(10_000, 900_000);
        var installationId = appId + 1;
        var registered = await sim.Post("/_sim/apps", new
        {
            id = appId, slug = "migrator-" + appId, publicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
            installations = new[] { new { id = installationId, org = "orion-org", repositorySelection = "selected", contents, pullRequests, workflows } }
        });
        Assert.Equal(201, registered.Status);
        return (key, appId, installationId);
    }

    [Fact]
    public async Task Installation_metadata_matches_what_the_migrator_verifies()
    {
        var (key, appId, installationId) = await RegisterAppAsync();
        var jwt = Jwt(key, appId);
        var installation = (await sim.Get($"/app/installations/{installationId}", jwt)).Json;
        Assert.Equal(installationId, installation.GetProperty("id").GetInt64());
        Assert.Equal(appId, installation.GetProperty("app_id").GetInt64());
        Assert.Equal("Organization", installation.GetProperty("target_type").GetString());
        Assert.Equal(installation.GetProperty("target_id").GetInt64(), installation.GetProperty("account").GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, installation.GetProperty("suspended_at").ValueKind);
        Assert.Equal("write", installation.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Equal(404, (await sim.Get($"/app/installations/{installationId + 99}", jwt)).Status);

        using var other = RSA.Create(2048);
        Assert.Equal(401, (await sim.Get($"/app/installations/{installationId}", Jwt(other, appId))).Status);
        Assert.Equal(401, (await sim.Get($"/app/installations/{installationId}", Jwt(key, appId, lifetimeSeconds: -10))).Status);
    }

    [Fact]
    public async Task A_minted_one_repository_token_can_push_only_to_that_repository()
    {
        var (key, appId, installationId) = await RegisterAppAsync();
        var allowed = await sim.CreateRepoAsync(Unique("app-allowed"));
        var other = await sim.CreateRepoAsync(Unique("app-other"));
        var allowedName = allowed.Split('/')[1];

        var minted = await sim.Post($"/app/installations/{installationId}/access_tokens",
            new { repositories = new[] { allowedName }, permissions = new { contents = "write" } }, Jwt(key, appId));
        Assert.Equal(201, minted.Status);
        var token = minted.Json.GetProperty("token").GetString()!;
        Assert.StartsWith("ghs_", token);
        Assert.Equal("selected", minted.Json.GetProperty("repository_selection").GetString());
        Assert.Equal(1, minted.Json.GetProperty("repositories").GetArrayLength());
        Assert.Equal(allowed, minted.Json.GetProperty("repositories")[0].GetProperty("full_name").GetString());
        var permissions = minted.Json.GetProperty("permissions");
        Assert.Equal("write", permissions.GetProperty("contents").GetString());
        Assert.Equal("read", permissions.GetProperty("metadata").GetString());
        var expiry = DateTimeOffset.Parse(minted.Json.GetProperty("expires_at").GetString()!);
        Assert.InRange((expiry - DateTimeOffset.UtcNow).TotalMinutes, 59, 61);

        Assert.Equal(200, (await sim.Get($"/repos/{allowed}", token)).Status);
        Assert.Equal(404, (await sim.Get($"/repos/{other}", token)).Status);
        var source = NewLocalRepo();
        Assert.Equal(0, Git(source, "push", sim.RemoteUrl(allowed, token), "main").Exit);
        Assert.NotEqual(0, Git(source, "push", sim.RemoteUrl(other, token), "main").Exit);
        // installation tokens carry contents rights only, never Actions secrets or admin
        Assert.Equal(403, (await sim.Get($"/repos/{allowed}/actions/secrets", token)).Status);
        Assert.Equal(403, (await sim.Get("/user", token)).Status);
    }

    [Fact]
    public async Task A_read_only_token_cannot_push_and_over_asking_is_refused()
    {
        var (key, appId, installationId) = await RegisterAppAsync(contents: "read");
        var slug = await sim.CreateRepoAsync(Unique("app-ro"));
        var name = slug.Split('/')[1];
        var jwt = Jwt(key, appId);

        Assert.Equal(422, (await sim.Post($"/app/installations/{installationId}/access_tokens",
            new { repositories = new[] { name }, permissions = new { contents = "write" } }, jwt)).Status);
        Assert.Equal(422, (await sim.Post($"/app/installations/{installationId}/access_tokens",
            new { repositories = new[] { name }, permissions = new { contents = "read", administration = "write" } }, jwt)).Status);
        Assert.Equal(422, (await sim.Post($"/app/installations/{installationId}/access_tokens",
            new { repositories = new[] { "no-such-repository" }, permissions = new { contents = "read" } }, jwt)).Status);

        var token = (await sim.Post($"/app/installations/{installationId}/access_tokens",
            new { repositories = new[] { name }, permissions = new { contents = "read" } }, jwt)).Json.GetProperty("token").GetString()!;
        var source = NewLocalRepo();
        var push = Git(source, "push", sim.RemoteUrl(slug, token), "main");
        Assert.NotEqual(0, push.Exit);
        Assert.Contains("403", push.Output);
        Assert.Equal(0, Git(source, "ls-remote", sim.RemoteUrl(slug, token)).Exit);
    }

    private async Task<string> TokenAsync(RSA key, long appId, long installationId, string repo, object permissions) =>
        (await sim.Post($"/app/installations/{installationId}/access_tokens", new { repositories = new[] { repo }, permissions }, Jwt(key, appId)))
            .Json.GetProperty("token").GetString()!;

    [Fact]
    public async Task Pull_requests_and_workflows_are_permissions_that_are_granted_then_asked_for_and_then_reported()
    {
        var (key, appId, installationId) = await RegisterAppAsync(pullRequests: "write", workflows: "write");
        var name = (await sim.CreateRepoAsync(Unique("perm"))).Split('/')[1];
        var jwt = Jwt(key, appId);

        var granted = (await sim.Get($"/app/installations/{installationId}", jwt)).Json.GetProperty("permissions");
        Assert.Equal(("write", "write", "write", "read"), (granted.GetProperty("contents").GetString(), granted.GetProperty("pull_requests").GetString(),
            granted.GetProperty("workflows").GetString(), granted.GetProperty("metadata").GetString()));

        var full = await sim.Post($"/app/installations/{installationId}/access_tokens",
            new { repositories = new[] { name }, permissions = new { contents = "write", pull_requests = "write", workflows = "write" } }, jwt);
        Assert.Equal(201, full.Status);
        var asked = full.Json.GetProperty("permissions");
        Assert.Equal(("write", "write", "write"), (asked.GetProperty("contents").GetString(), asked.GetProperty("pull_requests").GetString(), asked.GetProperty("workflows").GetString()));

        // a token has what it was asked for and nothing more, even from an installation that was granted more
        var narrow = await sim.Post($"/app/installations/{installationId}/access_tokens", new { repositories = new[] { name }, permissions = new { contents = "write" } }, jwt);
        Assert.False(narrow.Json.GetProperty("permissions").TryGetProperty("pull_requests", out _));
        Assert.False(narrow.Json.GetProperty("permissions").TryGetProperty("workflows", out _));

        // what the installation was not granted cannot be asked for; GitHub has no "read" level for workflows
        var (bareKey, bareApp, bareInstallation) = await RegisterAppAsync();
        var bare = Jwt(bareKey, bareApp);
        Assert.Equal(422, (await sim.Post($"/app/installations/{bareInstallation}/access_tokens", new { repositories = new[] { name }, permissions = new { contents = "write", pull_requests = "read" } }, bare)).Status);
        Assert.Equal(422, (await sim.Post($"/app/installations/{bareInstallation}/access_tokens", new { repositories = new[] { name }, permissions = new { contents = "write", workflows = "write" } }, bare)).Status);
        Assert.Equal(422, (await sim.Post($"/app/installations/{installationId}/access_tokens", new { repositories = new[] { name }, permissions = new { contents = "write", workflows = "read" } }, jwt)).Status);
        var (readKey, readApp, readInstallation) = await RegisterAppAsync(pullRequests: "read");
        Assert.Equal(422, (await sim.Post($"/app/installations/{readInstallation}/access_tokens", new { repositories = new[] { name }, permissions = new { contents = "write", pull_requests = "write" } }, Jwt(readKey, readApp))).Status);
        Assert.Equal(201, (await sim.Post($"/app/installations/{readInstallation}/access_tokens", new { repositories = new[] { name }, permissions = new { contents = "write", pull_requests = "read" } }, Jwt(readKey, readApp))).Status);
        Assert.False((await sim.Get($"/app/installations/{bareInstallation}", bare)).Json.GetProperty("permissions").TryGetProperty("pull_requests", out _));
    }

    [Fact]
    public async Task A_file_under_github_workflows_needs_the_workflows_permission_or_the_workflow_scope()
    {
        var (key, appId, installationId) = await RegisterAppAsync(workflows: "write");
        var slug = await sim.CreateRepoAsync(Unique("wf"));
        var name = slug.Split('/')[1];
        await sim.Put($"/repos/{slug}/contents/README.md", new { message = "init", content = Base64("# r\n") });
        var plain = await TokenAsync(key, appId, installationId, name, new { contents = "write" });
        var withWorkflows = await TokenAsync(key, appId, installationId, name, new { contents = "write", workflows = "write" });
        var file = new { message = "add", content = Base64("name: x\non:\n  workflow_dispatch:\njobs: {}\n") };

        var refused = await sim.Put($"/repos/{slug}/contents/.github/workflows/ci.yml", file, plain);
        Assert.Equal(403, refused.Status);
        Assert.Contains("workflows", refused.Json.GetProperty("message").GetString());
        Assert.Equal(404, (await sim.Get($"/repos/{slug}/contents/.github/workflows/ci.yml")).Status); // nothing was written
        // everything else is still allowed with the same token, workflow-looking names included
        Assert.Equal(201, (await sim.Put($"/repos/{slug}/contents/docs/ci.yml", file, plain)).Status);
        Assert.Equal(201, (await sim.Put($"/repos/{slug}/contents/.github/ci-notes.md", file, plain)).Status);
        Assert.Equal(201, (await sim.Put($"/repos/{slug}/contents/.github/workflows-notes/readme.md", file, plain)).Status);

        Assert.Equal(201, (await sim.Put($"/repos/{slug}/contents/.github/workflows/ci.yml", file, withWorkflows)).Status);
        var current = (await sim.Get($"/repos/{slug}/contents/.github/workflows/ci.yml")).Json.GetProperty("sha").GetString();
        Assert.Equal(403, (await sim.Put($"/repos/{slug}/contents/.github/workflows/ci.yml", new { message = "edit", content = Base64("name: y\n"), sha = current }, plain)).Status);
        Assert.Equal(403, (await sim.SendAsync("DELETE", $"/repos/{slug}/contents/.github/workflows/ci.yml", new { message = "rm", sha = current }, plain)).Status);
        Assert.Equal(200, (await sim.SendAsync("DELETE", $"/repos/{slug}/contents/.github/workflows/ci.yml", new { message = "rm", sha = current }, withWorkflows)).Status);

        // a classic token without the workflow scope is refused the same way, a user who has it is not
        var noScope = await sim.Put($"/repos/{slug}/contents/.github/workflows/other.yml", file, "gh-sim-no-workflow-pat");
        Assert.Equal(403, noScope.Status);
        Assert.Contains("scope", noScope.Json.GetProperty("message").GetString());
        Assert.Equal(201, (await sim.Put($"/repos/{slug}/contents/docs/other.yml", file, "gh-sim-no-workflow-pat")).Status);
        Assert.Equal(201, (await sim.Put($"/repos/{slug}/contents/.github/workflows/other.yml", file)).Status);
    }

    [Fact]
    public async Task Pull_requests_need_the_pull_requests_permission_for_an_installation_token()
    {
        var (key, appId, installationId) = await RegisterAppAsync(pullRequests: "write");
        var slug = await sim.CreateRepoAsync(Unique("prperm"));
        var name = slug.Split('/')[1];
        await sim.Put($"/repos/{slug}/contents/README.md", new { message = "init", content = Base64("# r\n") });
        var tip = (await sim.Get($"/repos/{slug}/git/ref/heads/main")).Json.GetProperty("object").GetProperty("sha").GetString();
        var contentsOnly = await TokenAsync(key, appId, installationId, name, new { contents = "write" });
        var reader = await TokenAsync(key, appId, installationId, name, new { contents = "write", pull_requests = "read" });
        var writer = await TokenAsync(key, appId, installationId, name, new { contents = "write", pull_requests = "write" });

        // the branch and its commit need contents only
        Assert.Equal(201, (await sim.Post($"/repos/{slug}/git/refs", new { @ref = "refs/heads/work", sha = tip }, contentsOnly)).Status);
        Assert.Equal(201, (await sim.Put($"/repos/{slug}/contents/CHANGES.md", new { message = "change", content = Base64("c\n"), branch = "work" }, contentsOnly)).Status);
        var pull = new { title = "Add changes", head = "work", @base = "main" };

        Assert.Equal(403, (await sim.Post($"/repos/{slug}/pulls", pull, contentsOnly)).Status);
        Assert.Equal(403, (await sim.Get($"/repos/{slug}/pulls", contentsOnly)).Status);
        Assert.Equal(403, (await sim.Post($"/repos/{slug}/pulls", pull, reader)).Status);
        Assert.Equal(200, (await sim.Get($"/repos/{slug}/pulls", reader)).Status);

        var opened = await sim.Post($"/repos/{slug}/pulls", pull, writer);
        Assert.Equal(201, opened.Status);
        Assert.Equal(200, (await sim.Get($"/repos/{slug}/pulls/1", reader)).Status);
        Assert.Equal(403, (await sim.Get($"/repos/{slug}/pulls/1", contentsOnly)).Status);
        Assert.Equal(403, (await sim.SendAsync("PATCH", $"/repos/{slug}/pulls/1", new { title = "x" }, reader)).Status);
        Assert.Equal(200, (await sim.SendAsync("PATCH", $"/repos/{slug}/pulls/1", new { title = "Renamed" }, writer)).Status);
        Assert.Equal(403, (await sim.Put($"/repos/{slug}/pulls/1/merge", new { merge_method = "merge" }, reader)).Status);
        // a user's token has what the user has: the existing behaviour is unchanged
        Assert.Equal(200, (await sim.Get($"/repos/{slug}/pulls")).Status);
    }
}

public sealed class GhSimLifecycleTests
{
    private static string OriginOf(Microsoft.AspNetCore.Builder.WebApplication app) =>
        app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();

    [Fact]
    public async Task Seeded_repositories_have_reproducible_object_ids_and_state_survives_restart_until_the_seed_changes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ghsim-life-" + Guid.NewGuid().ToString("N"));
        string firstSha;
        try
        {
            var seed = GhSeed.Load(null);
            var first = await GhSimApp.StartAsync(new GhSimOptions(seed, dir, "http://127.0.0.1:0"));
            var origin = OriginOf(first);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", "gh-sim-pat");
            firstSha = JsonDocument.Parse(await http.GetStringAsync(origin + "/api/v3/repos/orion-org/existing-repo/git/ref/heads/main"))
                .RootElement.GetProperty("object").GetProperty("sha").GetString()!;
            await http.PostAsync(origin + "/api/v3/orgs/orion-org/repos", new StringContent("{\"name\":\"survivor\"}", Encoding.UTF8, "application/json"));
            await first.StopAsync(); await first.DisposeAsync();

            // restart on the same volume: same seed => state kept
            var second = await GhSimApp.StartAsync(new GhSimOptions(seed, dir, "http://127.0.0.1:0"));
            origin = OriginOf(second);
            Assert.Equal(200, (int)(await http.GetAsync(origin + "/api/v3/repos/orion-org/survivor")).StatusCode);
            await second.StopAsync(); await second.DisposeAsync();

            // a different seed rebuilds the estate instead of silently reusing stale repositories
            var changed = seed with { Repos = [.. seed.Repos!, new SeedRepo("orion-org", "new-in-seed")] };
            var third = await GhSimApp.StartAsync(new GhSimOptions(changed, dir, "http://127.0.0.1:0"));
            origin = OriginOf(third);
            Assert.Equal(404, (int)(await http.GetAsync(origin + "/api/v3/repos/orion-org/survivor")).StatusCode);
            Assert.Equal(200, (int)(await http.GetAsync(origin + "/api/v3/repos/orion-org/new-in-seed")).StatusCode);
            var sameSha = JsonDocument.Parse(await http.GetStringAsync(origin + "/api/v3/repos/orion-org/existing-repo/git/ref/heads/main"))
                .RootElement.GetProperty("object").GetProperty("sha").GetString();
            Assert.Equal(firstSha, sameSha);

            // reset restores the seeded estate in place
            Assert.Equal(204, (int)(await http.PostAsync(origin + "/_sim/reset", null)).StatusCode);
            Assert.Equal(200, (int)(await http.GetAsync(origin + "/api/v3/repos/orion-org/new-in-seed")).StatusCode);
            await third.StopAsync(); await third.DisposeAsync();
        }
        finally { GhStore.ForceDelete(dir); }
    }
}
