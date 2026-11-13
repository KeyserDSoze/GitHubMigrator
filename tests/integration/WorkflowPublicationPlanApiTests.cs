using System.Net;
using System.Text.Json;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The whole chain on the real API: a model (LlmSim) writes a workflow, a second person approves it, and the publication plan is made
// against the GitHub simulator over real HTTP. The plan reads the destination and writes nothing there.
public sealed class WorkflowPublicationPlanApiTests() : ProposalApiTestBase(singleReviewer: false, githubSim: true)
{
    private const string WorkflowPath = ".github/workflows/azdo-42-release-safe.yml";

    private async Task<JsonElement> ApprovedAsync()
    {
        var conversion = await ReadyProposalAsync();
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Reviewed." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return conversion;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PlanAsync(JsonElement conversion, string repository, string? baseBranch = null, Guid? destination = null,
        Who who = Who.Superadmin)
    {
        using var response = await SendAsync(HttpMethod.Post, Url(conversion, "/publication-plan"), who,
            new { destinationConnectionId = destination ?? Api.DestinationConnectionId, repository, baseBranch });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    // a repository with a main branch, made directly on the simulator
    private async Task<string> RepoAsync(string prefix)
    {
        var name = await GitHub!.CreateRepoAsync(prefix);
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{name}/contents/README.md", new { message = "init", content = Convert.ToBase64String("# r\n"u8.ToArray()) });
        return name;
    }

    private async Task<string> TipAsync(string repo, string branch = "main") =>
        (await GitHub!.SendAsync("GET", $"/repos/orion-org/{repo}/branches/{branch}")).GetProperty("commit").GetProperty("sha").GetString()!;

    // what the destination was asked since the log was last cleared (the simulator's own control requests are not the product's)
    private async Task<(string Method, string Path)[]> DestinationCallsAsync() =>
        (await GitHub!.SendAsync("GET", "/_sim/requests?limit=500")).EnumerateArray()
            .Select(e => (Method: e.GetProperty("Method").GetString()!, Path: e.GetProperty("Path").GetString()!)).Where(c => !c.Path.StartsWith("/_sim", StringComparison.Ordinal)).ToArray();

    private static string[] Codes(JsonElement plan) => plan.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).ToArray();

    [RequiresAnalysersFact]
    public async Task An_approved_workflow_gets_a_plan_against_the_destination_and_nothing_is_written_there()
    {
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("plan");
        var tip = await TipAsync(repo);
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        var (status, plan) = await PlanAsync(conversion, repo);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(plan.GetProperty("ready").GetBoolean());
        Assert.False(plan.GetProperty("writesPerformed").GetBoolean());
        Assert.Empty(Codes(plan));
        Assert.Equal(("orion-org", repo, "main", tip), (plan.GetProperty("organization").GetString(), plan.GetProperty("repository").GetString(),
            plan.GetProperty("baseBranch").GetString(), plan.GetProperty("baseSha").GetString()));
        var sha = conversion.GetProperty("workflowSha256").GetString()!;
        Assert.Equal(WorkflowPath, plan.GetProperty("workflowPath").GetString());
        Assert.Equal("migrate/azdo-42-" + sha[..8], plan.GetProperty("headBranch").GetString());
        Assert.Equal(sha, plan.GetProperty("workflowSha256").GetString());
        Assert.Equal(conversion.GetProperty("workflowYaml").GetString(), plan.GetProperty("workflowYaml").GetString());
        Assert.Matches("^[0-9a-f]{64}$", plan.GetProperty("planSha256").GetString());
        var body = plan.GetProperty("pullRequestBody").GetString()!;
        Assert.Contains(sha, body);
        Assert.Contains($"`{Api.SuperadminName}`", body); // who approved
        Assert.Contains($"`{Api.AdminName}`", body);       // who proposed
        Assert.Contains("`DEPLOY_PASSWORD`", body);
        Assert.Equal(7, plan.GetProperty("steps").GetArrayLength());
        Assert.DoesNotContain(GhSimFixture.Pat, plan.ToString());

        // GETs only, and only about that repository
        var log = await DestinationCallsAsync();
        Assert.NotEmpty(log);
        Assert.All(log, c =>
        {
            Assert.Equal("GET", c.Method);
            Assert.Contains($"/repos/orion-org/{repo}", c.Path);
        });
        var branches = (await GitHub.SendAsync("GET", $"/repos/orion-org/{repo}/branches")).EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["main"], branches);

        // the same plan again is the same plan; a new commit on the base branch is a different one
        var (_, again) = await PlanAsync(conversion, repo);
        Assert.Equal(plan.GetProperty("planSha256").GetString(), again.GetProperty("planSha256").GetString());
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{repo}/contents/CHANGES.md", new { message = "more", content = Convert.ToBase64String("c\n"u8.ToArray()) });
        var (_, moved) = await PlanAsync(conversion, repo);
        Assert.NotEqual(plan.GetProperty("planSha256").GetString(), moved.GetProperty("planSha256").GetString());
        Assert.NotEqual(tip, moved.GetProperty("baseSha").GetString());
    }

    [RequiresAnalysersFact]
    public async Task Everything_in_the_way_is_reported_by_what_the_destination_actually_holds()
    {
        var conversion = await ApprovedAsync();

        Assert.Equal(["RepositoryArchived"], Codes((await PlanAsync(conversion, "archived-repo")).Body));
        var (_, gone) = await PlanAsync(conversion, "no-such-repo");
        Assert.Equal(["RepositoryNotFound"], Codes(gone));
        Assert.False(gone.GetProperty("ready").GetBoolean());
        var plain = await RepoAsync("conflicts");
        Assert.Equal(["BaseBranchNotFound"], Codes((await PlanAsync(conversion, plain, "does-not-exist")).Body));

        // a different file already sits at the path
        var occupied = await RepoAsync("occupied");
        await GitHub!.SendAsync("PUT", $"/repos/orion-org/{occupied}/contents/{WorkflowPath}", new { message = "x", content = Convert.ToBase64String("name: other\n"u8.ToArray()) });
        var (_, taken) = await PlanAsync(conversion, occupied);
        Assert.Equal(["WorkflowPathOccupied"], Codes(taken));
        Assert.False(taken.GetProperty("ready").GetBoolean());

        // exactly this workflow is already there (even with Windows line endings): nothing to publish
        var published = await RepoAsync("published");
        var text = conversion.GetProperty("workflowYaml").GetString()!;
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{published}/contents/{WorkflowPath}", new { message = "x", content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n"))) });
        var (_, done) = await PlanAsync(conversion, published);
        Assert.Equal([("AlreadyPublished", "Info")], done.GetProperty("findings").EnumerateArray().Select(f => (f.GetProperty("code").GetString()!, f.GetProperty("severity").GetString()!)));
        Assert.False(done.GetProperty("ready").GetBoolean());

        // the branch is taken, and then a pull request is open from it
        var branchy = await RepoAsync("branchy");
        var (_, first) = await PlanAsync(conversion, branchy);
        var head = first.GetProperty("headBranch").GetString()!;
        await GitHub.SendAsync("POST", $"/repos/orion-org/{branchy}/git/refs", new { @ref = "refs/heads/" + head, sha = await TipAsync(branchy) });
        Assert.Equal(["HeadBranchExists"], Codes((await PlanAsync(conversion, branchy)).Body));
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{branchy}/contents/{WorkflowPath}", new { message = "wip", branch = head, content = Convert.ToBase64String("name: wip\n"u8.ToArray()) });
        await GitHub.SendAsync("POST", $"/repos/orion-org/{branchy}/pulls", new { title = "wip", head, @base = "main" });
        var (_, withPull) = await PlanAsync(conversion, branchy);
        Assert.Equal(["HeadBranchExists", "OpenPullRequestExists"], Codes(withPull));
        Assert.Contains("#1", withPull.GetProperty("findings")[1].GetProperty("message").GetString());
        Assert.False(withPull.GetProperty("ready").GetBoolean());

        // another repository's open pull requests do not count
        var other = await RepoAsync("other");
        Assert.Empty(Codes((await PlanAsync(conversion, other)).Body));
    }

    [RequiresAnalysersFact]
    public async Task A_proposal_nobody_approved_or_that_was_revoked_is_not_planned_and_the_destination_is_not_contacted()
    {
        var pending = await ReadyProposalAsync();
        await GitHub!.SendAsync("DELETE", "/_sim/requests");
        var (status, body) = await PlanAsync(pending, "existing-repo");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("ProposalNotApproved", body.GetProperty("error").GetString());

        var approved = await ApprovedAsync();
        using var revoked = await SendAsync(HttpMethod.Post, Url(approved, "/revoke"), Who.Admin, new { note = "withdrawn on purpose" });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var (afterRevoke, revokedBody) = await PlanAsync(approved, "existing-repo");
        Assert.Equal(HttpStatusCode.Conflict, afterRevoke);
        Assert.Equal("ProposalNotApproved", revokedBody.GetProperty("error").GetString());

        var calls = (await DestinationCallsAsync()).Where(c => c.Path.Contains("/repos/orion-org/existing-repo")).ToArray();
        Assert.Empty(calls);
    }

    [RequiresAnalysersFact]
    public async Task Bad_input_unknown_ids_and_access_are_refused_before_any_destination_read()
    {
        var conversion = await ApprovedAsync();
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        Assert.Equal("InvalidRepository", (await PlanAsync(conversion, "bad repo!")).Body.GetProperty("error").GetString());
        Assert.Equal("InvalidRepository", (await PlanAsync(conversion, "")).Body.GetProperty("error").GetString());
        Assert.Equal("InvalidBranch", (await PlanAsync(conversion, "existing-repo", "../etc")).Body.GetProperty("error").GetString());
        var (unknownDestination, unknownBody) = await PlanAsync(conversion, "existing-repo", destination: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, unknownDestination);
        Assert.Equal("DestinationNotFound", unknownBody.GetProperty("error").GetString());
        var (sourceAsDestination, sourceBody) = await PlanAsync(conversion, "existing-repo", destination: Api.SourceConnectionId);
        Assert.Equal(HttpStatusCode.BadRequest, sourceAsDestination);
        Assert.Equal("UnsupportedDestination", sourceBody.GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await PlanAsync(conversion, "existing-repo", who: Who.Anonymous)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PlanAsync(conversion, "existing-repo", who: Who.User)).Status);
        using var unknown = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{Guid.NewGuid()}/publication-plan", Who.Admin,
            new { destinationConnectionId = Api.DestinationConnectionId, repository = "existing-repo" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        Assert.Empty((await DestinationCallsAsync()).Where(c => c.Path.Contains("/repos/orion-org/")));
    }
}
