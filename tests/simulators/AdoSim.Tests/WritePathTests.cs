using System.Net;
using System.Text.Json;

namespace AdoSim.Tests;

// Incremental changes: the commit API, git push, reset.
public sealed class WritePathTests
{
    private const string Project = "Orion Commerce";

    private static async Task<JsonElement> CommitAsync(SimHost sim, object body, string repo = "orion-api", HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await sim.PostCommitAsync(Project, repo, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Commit_api_creates_real_commits_with_increasing_deterministic_dates()
    {
        await using var sim = await SimHost.StartAsync("orion");
        var before = await sim.RefsAsync(Project, "orion-api");
        var first = await CommitAsync(sim, new { branch = "main", message = "add notes", files = new Dictionary<string, string> { ["docs/notes.md"] = "# notes\n", ["src/new/Äö.txt"] = "unicode ✓\n" }, delete = new[] { "README.md" } });
        var firstSha = first.Str("commitId");
        Assert.Equal("main", first.Str("branch"));
        Assert.NotEqual(before["refs/heads/main"], firstSha);
        var refs = first.GetProperty("refs").EnumerateArray().ToDictionary(r => r.Str("name"), r => r.Str("objectId"));
        Assert.Equal(firstSha, refs["refs/heads/main"]);
        Assert.Equal(before.Count, refs.Count);
        Assert.Equal(firstSha, (await sim.RefsAsync(Project, "orion-api"))["refs/heads/main"]);

        var notes = await sim.JsonAsync($"/Orion%20Commerce/_apis/git/repositories/orion-api/items?path=/docs/notes.md&$format=json&includeContent=true");
        Assert.Equal("# notes\n", notes.Str("content"));
        Assert.Equal(firstSha, notes.Str("commitId"));
        Assert.Equal("unicode ✓\n", (await sim.JsonAsync("/Orion%20Commerce/_apis/git/repositories/orion-api/items?path=/src/new/Äö.txt")).Str("content"));
        using var deleted = await sim.GetAsync("/Orion%20Commerce/_apis/git/repositories/orion-api/items?path=/README.md");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

        var second = await CommitAsync(sim, new { branch = "main", message = "second", files = new Dictionary<string, string> { ["docs/notes.md"] = "# notes v2\n" } });
        var detail = await sim.JsonAsync($"/Orion%20Commerce/_apis/git/repositories/orion-api/commits/{second.Str("commitId")}");
        Assert.Equal(firstSha, detail.GetProperty("parents")[0].GetString());
        var firstDate = DateTimeOffset.Parse((await sim.JsonAsync($"/Orion%20Commerce/_apis/git/repositories/orion-api/commits/{firstSha}")).GetProperty("committer").Str("date"));
        var secondDate = DateTimeOffset.Parse(detail.GetProperty("committer").Str("date"));
        Assert.True(secondDate > firstDate);
        Assert.Equal("Sim Author", detail.GetProperty("author").Str("name"));
        Assert.Equal("second", detail.Str("comment"));
        Assert.Equal((await sim.ExpectedAsync()).GetProperty("refs").GetInt32(), (await sim.AllRefsAsync()).Count);
    }

    [Fact]
    public async Task Commit_api_creates_branches_tags_and_populates_an_empty_repository()
    {
        await using var sim = await SimHost.StartAsync("orion");
        var main = (await sim.RefsAsync(Project, "orion-api"))["refs/heads/main"];
        var branch = await CommitAsync(sim, new { branch = "feature/new-thing", createBranchFrom = "main", message = "start", files = new Dictionary<string, string> { ["a.txt"] = "a" }, tag = "v9" });
        var refs = await sim.RefsAsync(Project, "orion-api");
        Assert.Equal(branch.Str("commitId"), refs["refs/heads/feature/new-thing"]);
        Assert.Equal(branch.Str("commitId"), refs["refs/tags/v9"]);
        Assert.Equal(main, refs["refs/heads/main"]);
        Assert.Equal(main, (await sim.JsonAsync($"/Orion%20Commerce/_apis/git/repositories/orion-api/commits/{branch.Str("commitId")}")).GetProperty("parents")[0].GetString());
        // branching from a tag works as well
        await CommitAsync(sim, new { branch = "from-tag", createBranchFrom = "v1.2.0", message = "x" });
        Assert.Equal(1, (await sim.JsonAsync("/Orion%20Commerce/_apis/git/repositories/orion-api/stats/branches?name=from-tag")).GetProperty("count").GetInt32());

        await CommitAsync(sim, new { branch = "main", message = "again", tag = "v9" }, expected: HttpStatusCode.Conflict);
        await CommitAsync(sim, new { branch = "missing-branch", message = "x" }, expected: HttpStatusCode.BadRequest);
        await CommitAsync(sim, new { branch = "x", createBranchFrom = "nope", message = "x" }, expected: HttpStatusCode.BadRequest);
        await CommitAsync(sim, new { branch = "main", message = "" }, expected: HttpStatusCode.BadRequest);
        await CommitAsync(sim, new { message = "no branch" }, expected: HttpStatusCode.BadRequest);
        await CommitAsync(sim, new { branch = "main", message = "x", files = new Dictionary<string, string> { ["../escape"] = "x" } }, expected: HttpStatusCode.BadRequest);
        await CommitAsync(sim, new { branch = "main", message = "x", files = new Dictionary<string, string> { [".git/config"] = "x" } }, expected: HttpStatusCode.BadRequest);
        await CommitAsync(sim, new { branch = "bad..name", message = "x" }, expected: HttpStatusCode.BadRequest);

        var docs = await CommitAsync(sim, new { branch = "main", message = "first commit", files = new Dictionary<string, string> { ["README.md"] = "# docs\n" } }, "orion-docs");
        Assert.Equal(docs.Str("commitId"), (await sim.RefsAsync(Project, "orion-docs"))["refs/heads/main"]);
        var dir = await sim.CloneAsync(Project, "orion-docs");
        Assert.Equal("# docs\n", await File.ReadAllTextAsync(Path.Combine(dir, "README.md")));
    }

    [Fact]
    public async Task Commit_api_is_authenticated_and_refuses_unknown_and_disabled_repositories()
    {
        await using var sim = await SimHost.StartAsync("orion");
        using (var anonymous = await sim.PostCommitAsync(Project, "orion-api", new { branch = "main", message = "x" }, authenticated: false))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var unknown = await sim.PostCommitAsync(Project, "nope", new { branch = "main", message = "x" }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var disabled = await sim.PostCommitAsync(Project, "orion-archive", new { branch = "main", message = "x" }))
            Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        using var garbage = await sim.Http.PostAsync($"{sim.Origin}/_sim/repos/{SimHost.Esc(Project)}/orion-api/commits", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        using var reset = await new HttpClient().PostAsync($"{sim.Origin}/_sim/reset", null);
        Assert.Equal(HttpStatusCode.Unauthorized, reset.StatusCode);
    }

    [Fact]
    public async Task A_real_git_push_round_trips_and_a_disabled_repository_refuses_it()
    {
        await using var sim = await SimHost.StartAsync("orion");
        var dir = await sim.CloneAsync(Project, "orion-lib");
        await Git.MustAsync(dir, null, "config", "user.email", "dev@example.test");
        await Git.MustAsync(dir, null, "config", "user.name", "Dev");
        await File.WriteAllTextAsync(Path.Combine(dir, "pushed.txt"), "from git push\n");
        await Git.MustAsync(dir, null, "add", "-A");
        await Git.MustAsync(dir, null, "commit", "-q", "-m", "pushed through smart http");
        var local = await Git.MustAsync(dir, null, "rev-parse", "HEAD");
        await Git.MustAsync(dir, null, "push", "-q", "origin", "HEAD:refs/heads/main", "HEAD:refs/heads/feature/pushed");
        await Git.MustAsync(dir, null, "tag", "pushed-tag");
        await Git.MustAsync(dir, null, "push", "-q", "origin", "pushed-tag");

        var refs = await sim.RefsAsync(Project, "orion-lib");
        Assert.Equal(local, refs["refs/heads/main"]);
        Assert.Equal(local, refs["refs/heads/feature/pushed"]);
        Assert.Equal(local, refs["refs/tags/pushed-tag"]);
        Assert.Equal("from git push\n", (await sim.JsonAsync("/Orion%20Commerce/_apis/git/repositories/orion-lib/items?path=/pushed.txt")).Str("content"));
        var second = await sim.CloneAsync(Project, "orion-lib");
        Assert.Equal(local, await Git.MustAsync(second, null, "rev-parse", "HEAD"));
        Assert.Equal((await sim.ExpectedAsync()).GetProperty("refs").GetInt32(), (await sim.AllRefsAsync()).Count);

        // history rewrites are accepted too (no branch protection in the simulator), deletes as well
        await Git.MustAsync(dir, null, "push", "-q", "origin", ":refs/heads/feature/pushed");
        Assert.DoesNotContain("refs/heads/feature/pushed", (await sim.RefsAsync(Project, "orion-lib")).Keys);

        var archive = await sim.CloneAsync(Project, "orion-archive");
        await Git.MustAsync(archive, null, "config", "user.email", "dev@example.test");
        await Git.MustAsync(archive, null, "config", "user.name", "Dev");
        await Git.MustAsync(archive, null, "commit", "-q", "--allow-empty", "-m", "nope");
        var rejected = await Git.RunAsync(archive, null, "push", "origin", "HEAD:refs/heads/nope");
        Assert.NotEqual(0, rejected.Exit);
        Assert.Contains("403", rejected.Err);
    }

    [Fact]
    public async Task Pushing_without_or_with_a_wrong_pat_fails()
    {
        await using var sim = await SimHost.StartAsync("orion");
        var dir = await sim.CloneAsync(Project, "orion-lib");
        await Git.MustAsync(dir, null, "config", "user.email", "dev@example.test");
        await Git.MustAsync(dir, null, "config", "user.name", "Dev");
        await Git.MustAsync(dir, null, "commit", "-q", "--allow-empty", "-m", "x");
        var wrong = await Git.RunAsync(dir, null, "push", sim.CloneUrl(Project, "orion-lib", "wrong-pat"), "HEAD:refs/heads/x");
        Assert.NotEqual(0, wrong.Exit);
        var anonymous = await Git.RunAsync(dir, null, "push", $"{sim.Origin}/tfs/DefaultCollection/Orion%20Commerce/_git/orion-lib", "HEAD:refs/heads/x");
        Assert.NotEqual(0, anonymous.Exit);
        Assert.DoesNotContain("refs/heads/x", (await sim.RefsAsync(Project, "orion-lib")).Keys);
    }

    [Fact]
    public async Task Reset_recreates_the_estate_from_the_seed()
    {
        await using var sim = await SimHost.StartAsync("orion");
        var pristine = await sim.AllRefsAsync();
        await CommitAsync(sim, new { branch = "main", message = "temporary", files = new Dictionary<string, string> { ["tmp.txt"] = "x" }, tag = "temp" });
        await CommitAsync(sim, new { branch = "main", message = "docs", files = new Dictionary<string, string> { ["README.md"] = "x" } }, "orion-docs");
        Assert.NotEqual(pristine, await sim.AllRefsAsync());

        using var reset = await sim.Http.PostAsync($"{sim.Origin}/_sim/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var expected = JsonDocument.Parse(await reset.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(pristine, await sim.AllRefsAsync());
        Assert.Equal(pristine.Count, expected.GetProperty("refs").GetInt32());
        Assert.Empty(await sim.RefsAsync(Project, "orion-docs"));
    }

    [Fact]
    public async Task The_same_call_sequence_produces_the_same_commit_ids_on_two_machines()
    {
        var results = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            await using var sim = await SimHost.StartAsync("orion");
            var a = await CommitAsync(sim, new { branch = "main", message = "one", files = new Dictionary<string, string> { ["x.txt"] = "1" } });
            var b = await CommitAsync(sim, new { branch = "release/2024.1", message = "two", files = new Dictionary<string, string> { ["y.txt"] = "2" }, tag = "t" });
            var c = await CommitAsync(sim, new { branch = "fresh", createBranchFrom = "main", message = "three", delete = new[] { "x.txt" } });
            results.Add(string.Join(",", a.Str("commitId"), b.Str("commitId"), c.Str("commitId")));
        }
        Assert.Equal(results[0], results[1]);
    }
}
