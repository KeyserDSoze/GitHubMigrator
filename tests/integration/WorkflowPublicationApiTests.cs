using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// The only place the product writes to a destination: a branch, one workflow file and a pull request, on the real API (PostgreSQL ledger, JWT,
// encrypted signing key, production DI) against the GitHub simulator over real HTTP, after a model wrote the workflow and a second person approved it.
public sealed class WorkflowPublicationApiTests : PublicationApiTestBase
{
    [RequiresAnalysersFact]
    public async Task An_approved_and_confirmed_plan_becomes_a_branch_a_file_and_a_pull_request_that_match_it_and_nothing_else()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-ok");
        var baseTip = await TipAsync(repo);
        var (_, plan) = await PlanAsync(conversion, repo);
        var planHash = plan.GetProperty("planSha256").GetString()!;
        var head = plan.GetProperty("headBranch").GetString()!;
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        var (status, published) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Published", published.GetProperty("state").GetString());
        Assert.Equal(1, published.GetProperty("pullNumber").GetInt32());
        Assert.Matches("^[0-9a-f]{40}$", published.GetProperty("commitSha").GetString());
        Assert.Equal(planHash, published.GetProperty("planSha256").GetString());
        Assert.Equal(Api.SuperadminName, published.GetProperty("requestedBy").GetString());
        Assert.False(published.GetProperty("writesPossible").GetBoolean());

        // what exists at the destination is what was planned, byte for byte
        var commit = published.GetProperty("commitSha").GetString()!;
        Assert.Equal(commit, await TipAsync(repo, head));
        var parents = (await GitHub.SendAsync("GET", $"/repos/orion-org/{repo}/git/commits/{commit}")).GetProperty("parents").EnumerateArray().Select(p => p.GetProperty("sha").GetString()).ToArray();
        Assert.Equal([baseTip], parents);
        var file = await GitHub.SendAsync("GET", $"/repos/orion-org/{repo}/contents/{WorkflowPath}?ref={head}");
        Assert.Equal(plan.GetProperty("workflowYaml").GetString(), Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!.Replace("\n", ""))));
        var pull = await GitHub.SendAsync("GET", $"/repos/orion-org/{repo}/pulls/1");
        Assert.Equal(("open", false, head, "main"), (pull.GetProperty("state").GetString(), pull.GetProperty("merged").GetBoolean(), pull.GetProperty("head").GetProperty("ref").GetString(), pull.GetProperty("base").GetProperty("ref").GetString()));
        Assert.Equal(plan.GetProperty("pullRequestTitle").GetString(), pull.GetProperty("title").GetString());
        Assert.Equal(plan.GetProperty("pullRequestBody").GetString(), pull.GetProperty("body").GetString());
        // the base branch was not touched and nothing was merged
        Assert.Equal(baseTip, await TipAsync(repo));

        // exactly three writes to the repository: the branch, the file, the pull request (and the token requests, which are not repository writes)
        var writes = await WritesAsync(repo);
        Assert.Equal([("POST", $"/api/v3/repos/orion-org/{repo}/git/refs"), ("PUT", $"/api/v3/repos/orion-org/{repo}/contents/{WorkflowPath}"), ("POST", $"/api/v3/repos/orion-org/{repo}/pulls")],
            writes);

        // the ledger and the listing say the same
        using var listed = await SendAsync(HttpMethod.Get, Url(conversion, "/publications"), Who.Admin);
        var entry = (await JsonOf(listed)).EnumerateArray().Single();
        Assert.Equal(("Published", 1), (entry.GetProperty("state").GetString(), entry.GetProperty("pullNumber").GetInt32()));
        var row = Assert.Single(await RowsAsync(conversion));
        Assert.Equal(("Published", planHash, commit), (row.State, row.PlanSha256, row.CommitSha));

        // the same plan is not published twice: it is no longer clear to publish (the branch and a pull request exist now)
        var (again, refusal) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal("PublicationPlanNotReady", refusal.GetProperty("error").GetString());
        Assert.Contains("HeadBranchExists", refusal.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, (await WritesAsync(repo)).Length);
    }

    [RequiresAnalysersFact]
    public async Task Nothing_is_written_unless_every_guard_holds()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-two");
        var planHash = await ReadyPlanHashAsync(conversion, repo);
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        // a reviewer who is not a superadmin, a missing or malformed confirmation, a plan the person did not see
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(conversion, repo, planHash, Who.Admin)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PublishAsync(conversion, repo, planHash, Who.Anonymous)).Status);
        var (noConfirm, noConfirmBody) = await PublishAsync(conversion, repo, null);
        Assert.Equal((HttpStatusCode.BadRequest, "PlanConfirmationRequired"), (noConfirm, noConfirmBody.GetProperty("error").GetString()));
        Assert.Equal("PlanConfirmationRequired", (await PublishAsync(conversion, repo, "not-a-hash")).Body.GetProperty("error").GetString());
        var (stale, staleBody) = await PublishAsync(conversion, repo, new string('0', 64));
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanChanged"), (stale, staleBody.GetProperty("error").GetString()));

        // a repository the operator did not allow-list
        var other = await RepoAsync("not-allowed");
        var otherHash = await ReadyPlanHashAsync(conversion, other);
        var (notAllowed, notAllowedBody) = await PublishAsync(conversion, other, otherHash);
        Assert.Equal((HttpStatusCode.Forbidden, "PublicationRepositoryNotAllowed"), (notAllowed, notAllowedBody.GetProperty("error").GetString()));

        // a proposal nobody approved
        var pending = await ReadyProposalAsync();
        var (unapproved, unapprovedBody) = await PublishAsync(pending, repo, planHash);
        Assert.Equal((HttpStatusCode.Conflict, "ProposalNotApproved"), (unapproved, unapprovedBody.GetProperty("error").GetString()));

        // the base branch moved after the plan was made: the confirmation no longer names what is true
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{repo}/contents/CHANGES.md", new { message = "more", content = Convert.ToBase64String("c\n"u8.ToArray()) });
        var (moved, movedBody) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanChanged"), (moved, movedBody.GetProperty("error").GetString()));

        Assert.Empty(await WritesAsync(repo));
        Assert.Empty(await WritesAsync(other));
        Assert.Empty(await RowsAsync(conversion));
        Assert.Empty(await RowsAsync(pending));
    }

    [RequiresAnalysersFact]
    public async Task A_refusal_before_any_write_leaves_the_same_plan_free_to_be_tried_again()
    {
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-late");
        await RegisterAppAsync(workflows: "none"); // the installation was not given the permission to write workflow files
        var planHash = await ReadyPlanHashAsync(conversion, repo);
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        var (status, failure) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("PublicationInstallationMismatch", failure.GetProperty("error").GetString());
        var record = failure.GetProperty("publication");
        Assert.Equal(("Failed", "Started", false), (record.GetProperty("state").GetString(), record.GetProperty("failedAtState").GetString(), record.GetProperty("writesPossible").GetBoolean()));
        Assert.Empty(await WritesAsync(repo));

        // the operator fixes the installation; the same plan can be published, and the failed attempt stays on record
        await RegisterAppAsync();
        var (retry, published) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal((HttpStatusCode.OK, "Published"), (retry, published.GetProperty("state").GetString()));
        using var listed = await SendAsync(HttpMethod.Get, Url(conversion, "/publications"), Who.Superadmin);
        Assert.Equal(["Published", "Failed"], (await JsonOf(listed)).EnumerateArray().Select(e => e.GetProperty("state").GetString()));
    }

    [RequiresAnalysersFact]
    public async Task A_write_that_fails_halfway_is_recorded_as_possibly_written_and_is_never_repeated_or_continued()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-fail");
        var planHash = await ReadyPlanHashAsync(conversion, repo);
        // the file write fails after the branch exists
        await GitHub!.SendAsync("POST", "/_sim/faults", new { pathRegex = @"/contents/\.github/workflows/", method = "PUT", status = 500, message = "boom" });
        await GitHub.SendAsync("DELETE", "/_sim/requests");

        var (status, failure) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var record = failure.GetProperty("publication");
        Assert.Equal(("Failed", "FileCommitting", true), (record.GetProperty("state").GetString(), record.GetProperty("failedAtState").GetString(), record.GetProperty("writesPossible").GetBoolean()));
        Assert.Equal("PublicationFileFailed", failure.GetProperty("error").GetString());

        // exactly one attempt at each write, no pull request, nothing repeated
        var writes = await WritesAsync(repo);
        Assert.Equal(["POST /api/v3/repos/orion-org/pub-fail/git/refs", "PUT /api/v3/repos/orion-org/pub-fail/contents/" + WorkflowPath], writes.Select(w => w.Method + " " + w.Path));
        Assert.Empty((await GitHub.SendAsync("GET", $"/repos/orion-org/{repo}/pulls?state=all")).EnumerateArray());

        // the destination now has a branch: the plan says so, and the product will not publish over it
        var (_, replan) = await PlanAsync(conversion, repo);
        Assert.Contains("HeadBranchExists", replan.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()));
        var (again, refusal) = await PublishAsync(conversion, repo, replan.GetProperty("planSha256").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanNotReady"), (again, refusal.GetProperty("error").GetString()));
        Assert.Equal(2, (await WritesAsync(repo)).Length);
        var row = Assert.Single(await RowsAsync(conversion));
        Assert.Equal(("Failed", "FileCommitting", true, "PublicationFileFailed"), (row.State, row.FailedAtState, row.WritesPossible, row.ErrorCode));
    }

    [RequiresAnalysersFact]
    public async Task Two_simultaneous_publications_of_one_plan_write_once()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-race");
        var planHash = await ReadyPlanHashAsync(conversion, repo);
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        var results = await Task.WhenAll(PublishAsync(conversion, repo, planHash), PublishAsync(conversion, repo, planHash));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.Status).Order());
        Assert.Equal(3, (await WritesAsync(repo)).Length);
        Assert.Single((await RowsAsync(conversion)).Where(p => p.State == "Published"));
        Assert.Single((await GitHub.SendAsync("GET", $"/repos/orion-org/{repo}/pulls?state=all")).EnumerateArray());
    }

    // What the service enforces, the database enforces too: these statements bypass the service on purpose.
    [RequiresAnalysersFact]
    public async Task The_database_refuses_to_rewrite_a_publication_skip_a_step_or_keep_two_live_attempts_of_one_plan()
    {
        var conversion = await ApprovedAsync();
        var proposal = conversion.GetProperty("proposalId").GetGuid();
        var sha = conversion.GetProperty("workflowSha256").GetString()!;
        var actor = Guid.NewGuid();
        string Plan(char c) => new(c, 64);

        async Task<string> RefusedAsync(string sql, params object[] parameters)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => InDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, parameters)));
            return ex.ToString();
        }
        Task<int> InsertAsync(Guid id, char plan, string state = "Started", string extra = "", string extraValues = "") => InDbAsync(db => db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc{extra}) " +
            $"VALUES ({{0}}, {{1}}, {{2}}, {{3}}, 'orion-org', 'pub-ok', 'main', {{4}}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-release-safe.yml', '{state}', false, {{5}}, 'tester', now(), now(){extraValues})",
            id, proposal, Plan(plan), sha, new string('c', 40), actor));

        // a row has a shape: a path and a branch of the expected form, a commit and a pull request when published, a reason when failed
        var live = Guid.NewGuid();
        Assert.Equal(1, await InsertAsync(live, 'a'));
        Assert.Contains("ck_workflow_publications_shape", await RefusedAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-ok', 'main', {4}, 'migrate/azdo-42-abcd1234', 'README.md', 'Started', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('b'), sha, new string('c', 40), actor));
        Assert.Contains("ck_workflow_publications_shape", await RefusedAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-ok', 'main', {4}, 'main', '.github/workflows/azdo-42-x.yml', 'Started', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('b'), sha, new string('c', 40), actor));
        Assert.Contains("ck_workflow_publications_shape", await RefusedAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-ok', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-x.yml', 'Published', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('b'), sha, new string('c', 40), actor));
        Assert.Contains("ck_workflow_publications_shape", await RefusedAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-ok', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-x.yml', 'Failed', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('b'), sha, new string('c', 40), actor));
        Assert.Contains("ck_workflow_publications_shape", await RefusedAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, 'short', {3}, 'orion-org', 'pub-ok', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-x.yml', 'Started', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('b'), sha, new string('c', 40), actor));

        // one live attempt per plan; a failure that happened before any write frees it, a failure after one does not
        Assert.Contains("ux_workflow_publications_live", await RefusedAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-ok', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-x.yml', 'Started', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('a'), sha, new string('c', 40), actor));
        Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE workflow_publications SET state = 'Failed', failed_at_state = 'Started', error_code = 'PublicationInstallationMismatch' WHERE id = {0}", live)));
        var second = Guid.NewGuid();
        Assert.Equal(1, await InsertAsync(second, 'a')); // freed
        Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE workflow_publications SET state = 'BranchCreating' WHERE id = {0}", second)));
        Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE workflow_publications SET state = 'BranchCreated' WHERE id = {0}", second)));
        // a failure after the branch was created cannot claim that nothing was written
        Assert.Contains("ck_workflow_publications_shape", await RefusedAsync("UPDATE workflow_publications SET state = 'Failed', failed_at_state = 'BranchCreated', error_code = 'x', writes_possible = false WHERE id = {0}", second));
        Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE workflow_publications SET state = 'Failed', failed_at_state = 'BranchCreated', error_code = 'PublicationFileAnswerLost', writes_possible = true WHERE id = {0}", second)));
        Assert.Contains("ux_workflow_publications_live", await RefusedAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-ok', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-x.yml', 'Started', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('a'), sha, new string('c', 40), actor));

        // a publication moves one step at a time and never backwards; what it recorded cannot be rewritten
        var walker = Guid.NewGuid();
        Assert.Equal(1, await InsertAsync(walker, 'd'));
        Assert.Contains("transition not allowed", await RefusedAsync("UPDATE workflow_publications SET state = 'BranchCreated' WHERE id = {0}", walker));          // skipped BranchCreating
        Assert.Contains("transition not allowed", await RefusedAsync("UPDATE workflow_publications SET state = 'Published' WHERE id = {0}", walker));
        Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE workflow_publications SET state = 'BranchCreating' WHERE id = {0}", walker)));
        Assert.Contains("transition not allowed", await RefusedAsync("UPDATE workflow_publications SET state = 'Started' WHERE id = {0}", walker));                 // backwards
        Assert.Contains("must name the state", await RefusedAsync("UPDATE workflow_publications SET state = 'Failed', failed_at_state = 'Started', error_code = 'x' WHERE id = {0}", walker));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET base_sha = {1} WHERE id = {0}", walker, new string('9', 40)));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET plan_sha256 = {1} WHERE id = {0}", walker, Plan('e')));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET requested_by = 'someone-else' WHERE id = {0}", walker));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET repository = 'other' WHERE id = {0}", walker));
        foreach (var step in new[] { ("BranchCreated", ""), ("FileCommitting", ""), ("FileCommitted", ", commit_sha = '" + new string('e', 40) + "'") })
            Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync($"UPDATE workflow_publications SET state = '{step.Item1}'{step.Item2} WHERE id = {{0}}", walker)));
        Assert.Contains("results cannot be changed", await RefusedAsync("UPDATE workflow_publications SET commit_sha = {1} WHERE id = {0}", walker, new string('f', 40)));
        Assert.Contains("results cannot be changed", await RefusedAsync("UPDATE workflow_publications SET state = 'PullRequestOpening', commit_sha = NULL WHERE id = {0}", walker)); // a recorded commit cannot be erased
        foreach (var step in new[] { ("PullRequestOpening", ""), ("PullRequestOpened", ", pull_number = 7"), ("Published", "") })
            Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync($"UPDATE workflow_publications SET state = '{step.Item1}'{step.Item2} WHERE id = {{0}}", walker)));
        Assert.Contains("is final", await RefusedAsync("UPDATE workflow_publications SET state = 'Failed', failed_at_state = 'Published', error_code = 'x', writes_possible = true WHERE id = {0}", walker));
        Assert.Contains("is final", await RefusedAsync("UPDATE workflow_publications SET pull_number = 8 WHERE id = {0}", walker));
        Assert.Contains("audit record", await RefusedAsync("DELETE FROM workflow_publications WHERE id = {0}", walker));
        Assert.Contains("audit record", await RefusedAsync("DELETE FROM workflow_publications WHERE id = {0}", live));
        // and the proposal a publication belongs to cannot disappear from under it
        Assert.Contains("are not deleted", await RefusedAsync("DELETE FROM workflow_proposals WHERE id = {0}", proposal));
        Assert.Equal(3, (await RowsAsync(conversion)).Count);
    }

    // A final attempt keeps its plan blocked. The one change the database accepts on it is the release marks, empty to filled, once, with everything else exactly as it was.
    [RequiresAnalysersFact]
    public async Task The_database_accepts_one_release_of_a_final_attempt_and_nothing_else_and_a_released_attempt_stops_blocking_its_plan()
    {
        var conversion = await ApprovedAsync();
        var proposal = conversion.GetProperty("proposalId").GetGuid();
        var sha = conversion.GetProperty("workflowSha256").GetString()!;
        var actor = Guid.NewGuid();
        string Plan(char c) => new(c, 64);

        async Task<string> RefusedAsync(string sql, params object[] parameters) =>
            (await Assert.ThrowsAnyAsync<Exception>(() => InDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, parameters)))).ToString();
        Task<int> ExecAsync(string sql, params object[] parameters) => InDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, parameters));
        Task<int> InsertAsync(Guid id, char plan, string state, bool writes, string extra = "", string extraValues = "") => ExecAsync(
            $"INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc{extra}) " +
            $"VALUES ({{0}}, {{1}}, {{2}}, {{3}}, 'orion-org', 'pub-rel', 'main', {{4}}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-release-safe.yml', '{state}', {(writes ? "true" : "false")}, {{5}}, 'tester', now(), now(){extraValues})",
            id, proposal, Plan(plan), sha, new string('c', 40), actor);
        Task<int> ReleaseSqlAsync(Guid id, string note = "Cleaned the destination.") => ExecAsync(
            "UPDATE workflow_publications SET released_at_utc = now(), released_by_id = {1}, released_by = 'admin', release_note = {2}, release_evidence = '{{\"headBranchExists\":false}}'::jsonb WHERE id = {0}", id, actor, note);
        var blocked = "ux_workflow_publications_live";

        // the index knows about releases (an index an earlier version built is rebuilt)
        var definition = await InDbAsync(db => db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_workflow_publications_live'").FirstAsync());
        Assert.Contains("released_at_utc", definition);

        // an attempt that failed after a write may have happened keeps its plan blocked
        var failed = Guid.NewGuid();
        Assert.Equal(1, await InsertAsync(failed, 'a', "Failed", true, ", error_code, failed_at_state", ", 'PublicationFileFailed', 'FileCommitting'"));
        Assert.Contains(blocked, await RefusedAsync("INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-rel', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-release-safe.yml', 'Started', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('a'), sha, new string('c', 40), actor));

        // the marks come all together or not at all, with a real note
        Assert.Contains("ck_workflow_publications_release", await RefusedAsync("UPDATE workflow_publications SET released_at_utc = now() WHERE id = {0}", failed));
        Assert.Contains("ck_workflow_publications_release", await RefusedAsync("UPDATE workflow_publications SET released_at_utc = now(), released_by_id = {1}, released_by = 'admin', release_note = 'ok' , release_evidence = '{{}}'::jsonb WHERE id = {0}", failed, actor));
        Assert.Contains("ck_workflow_publications_release", await RefusedAsync("UPDATE workflow_publications SET released_at_utc = now(), released_by_id = {1}, released_by = 'admin', release_note = 'Cleaned it.' WHERE id = {0}", failed, actor)); // no evidence
        // a release changes nothing else: not the state, not the reason, not whether a write was possible, not the time of the last change
        foreach (var other in new[] { "state = 'Published'", "error_code = 'Other'", "writes_possible = false", "updated_at_utc = now() + interval '1 hour'", "failed_at_state = 'Started'" })
            Assert.Contains("is final", await RefusedAsync($"UPDATE workflow_publications SET {other}, released_at_utc = now(), released_by_id = {{1}}, released_by = 'admin', release_note = 'Cleaned it.', release_evidence = '{{{{}}}}'::jsonb WHERE id = {{0}}", failed, actor));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET base_sha = {1}, released_at_utc = now(), released_by_id = {2}, released_by = 'admin', release_note = 'Cleaned it.', release_evidence = '{{}}'::jsonb WHERE id = {0}", failed, new string('9', 40), actor));

        // an attempt that has not finished is not released by the database either, nor is one that finishes and is released in one step
        var running = Guid.NewGuid();
        Assert.Equal(1, await InsertAsync(running, 'b', "Started", false));
        Assert.Contains("only a final", await RefusedAsync("UPDATE workflow_publications SET released_at_utc = now(), released_by_id = {1}, released_by = 'admin', release_note = 'Cleaned it.', release_evidence = '{{}}'::jsonb WHERE id = {0}", running, actor));
        Assert.Contains("only a final", await RefusedAsync("UPDATE workflow_publications SET state = 'Failed', failed_at_state = 'Started', error_code = 'PublicationAbandoned', writes_possible = true, released_at_utc = now(), released_by_id = {1}, released_by = 'admin', release_note = 'Cleaned it.', release_evidence = '{{}}'::jsonb WHERE id = {0}", running, actor));
        // abandoning is a failure like any other, and is released afterwards
        Assert.Equal(1, await ExecAsync("UPDATE workflow_publications SET state = 'Failed', failed_at_state = 'Started', error_code = 'PublicationAbandoned', writes_possible = true WHERE id = {0}", running));
        Assert.Equal(1, await ReleaseSqlAsync(running));

        // the one transition: empty marks to filled, on a final attempt
        Assert.Equal(1, await ReleaseSqlAsync(failed));
        var row = (await RowsAsync(conversion)).Single(r => r.Id == failed);
        Assert.Equal(("Failed", "PublicationFileFailed", true, "admin", "Cleaned the destination."), (row.State, row.ErrorCode, row.WritesPossible, row.ReleasedBy, row.ReleaseNote));
        Assert.NotNull(row.ReleasedAtUtc);
        Assert.Contains("headBranchExists", row.ReleaseEvidence);

        // and the plan is free: the same plan can be started again
        var again = Guid.NewGuid();
        Assert.Equal(1, await InsertAsync(again, 'a', "Started", false));

        // a release is decided once and cannot be undone or rewritten
        Assert.Contains("is final", await ReleaseRefusedAsync(failed));
        Assert.Contains("is final", await RefusedAsync("UPDATE workflow_publications SET release_note = 'Something else.' WHERE id = {0}", failed));
        Assert.Contains("is final", await RefusedAsync("UPDATE workflow_publications SET released_at_utc = NULL, released_by_id = NULL, released_by = NULL, release_note = NULL, release_evidence = NULL WHERE id = {0}", failed));
        Assert.Contains("audit record", await RefusedAsync("DELETE FROM workflow_publications WHERE id = {0}", failed));

        // a published attempt can be released too (its pull request closed unmerged, its branch gone), under the same rules
        var published = Guid.NewGuid();
        Assert.Equal(1, await InsertAsync(published, 'c', "Started", false));
        foreach (var step in new[] { ("BranchCreating", ""), ("BranchCreated", ""), ("FileCommitting", ""), ("FileCommitted", ", commit_sha = '" + new string('e', 40) + "'"), ("PullRequestOpening", ""), ("PullRequestOpened", ", pull_number = 7"), ("Published", "") })
            Assert.Equal(1, await ExecAsync($"UPDATE workflow_publications SET state = '{step.Item1}'{step.Item2} WHERE id = {{0}}", published));
        Assert.Contains(blocked, await RefusedAsync("INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-rel', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-release-safe.yml', 'Started', false, {5}, 't', now(), now())", Guid.NewGuid(), proposal, Plan('c'), sha, new string('c', 40), actor));
        Assert.Equal(1, await ReleaseSqlAsync(published));
        Assert.Equal(1, await InsertAsync(Guid.NewGuid(), 'c', "Started", false));

        Task<string> ReleaseRefusedAsync(Guid id) => RefusedAsync("UPDATE workflow_publications SET released_at_utc = now(), released_by_id = {1}, released_by = 'admin', release_note = 'Again.', release_evidence = '{{}}'::jsonb WHERE id = {0}", id, actor);
    }

    [RequiresAnalysersFact]
    public async Task The_status_and_the_credential_check_tell_an_operator_whether_publishing_could_start_without_writing()
    {
        await RegisterAppAsync();
        await RepoAsync("pub-ok");
        using var status = await SendAsync(HttpMethod.Get, "/api/workloads/pipelines/publication/status", Who.Admin);
        var seen = await JsonOf(status);
        Assert.True(seen.GetProperty("enabled").GetBoolean());
        Assert.Contains("orion-org/pub-ok", seen.GetProperty("allowedRepositories").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(PublicationKeyReference, seen.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/workloads/pipelines/publication/status", Who.User)).StatusCode);

        await GitHub!.SendAsync("DELETE", "/_sim/requests");
        var (ok, check) = await SendJsonAsync("/api/workloads/pipelines/publication/credential-check", new { repository = "orion-org/pub-ok" });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(["contents:write", "metadata:read", "pull_requests:write", "workflows:write"], check.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(await WritesAsync("pub-ok"));
        Assert.DoesNotContain("ghs_", check.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, (await SendJsonAsync("/api/workloads/pipelines/publication/credential-check", new { repository = "orion-org/not-allowed" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendJsonAsync("/api/workloads/pipelines/publication/credential-check", new { repository = "orion-org/pub-ok" }, Who.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync("/api/workloads/pipelines/publication/credential-check", new { repository = "no-slash" })).Status);

        // an installation that lacks a permission is reported, not used
        await RegisterAppAsync(pullRequests: "none");
        var (bad, badBody) = await SendJsonAsync("/api/workloads/pipelines/publication/credential-check", new { repository = "orion-org/pub-ok" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "PublicationInstallationMismatch"), (bad, badBody.GetProperty("error").GetString()));
    }
}

// Off by default: with nothing configured the product cannot write, says so, and still plans.
public sealed class WorkflowPublicationDisabledApiTests() : ProposalApiTestBase(singleReviewer: false, githubSim: true)
{
    [RequiresAnalysersFact]
    public async Task Without_the_operators_opt_in_nothing_can_be_published_and_the_plan_still_works()
    {
        var conversion = await ReadyProposalAsync();
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion) });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var status = await SendAsync(HttpMethod.Get, "/api/workloads/pipelines/publication/status", Who.Admin);
        var seen = await JsonOf(status);
        Assert.False(seen.GetProperty("enabled").GetBoolean());
        Assert.Empty(seen.GetProperty("allowedRepositories").EnumerateArray());

        await GitHub!.SendAsync("POST", "/orgs/orion-org/repos", new { name = "off" });
        await GitHub.SendAsync("PUT", "/repos/orion-org/off/contents/README.md", new { message = "init", content = Convert.ToBase64String("# r\n"u8.ToArray()) });
        using var plan = await SendAsync(HttpMethod.Post, Url(conversion, "/publication-plan"), Who.Superadmin, new { destinationConnectionId = Api.DestinationConnectionId, repository = "off" });
        var planned = await JsonOf(plan);
        Assert.True(planned.GetProperty("ready").GetBoolean());
        await GitHub.SendAsync("DELETE", "/_sim/requests");

        using var publish = await SendAsync(HttpMethod.Post, Url(conversion, "/publish"), Who.Superadmin,
            new { destinationConnectionId = Api.DestinationConnectionId, repository = "off", planSha256 = planned.GetProperty("planSha256").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
        Assert.Equal("PublicationDisabled", (await JsonOf(publish)).GetProperty("error").GetString());
        var calls = (await GitHub.SendAsync("GET", "/_sim/requests?limit=500")).EnumerateArray().Where(e => e.GetProperty("Path").GetString()!.Contains("/repos/orion-org/off")).ToArray();
        Assert.Empty(calls);
        using var check = await SendAsync(HttpMethod.Post, "/api/workloads/pipelines/publication/credential-check", Who.Superadmin, new { repository = "orion-org/off" });
        Assert.Equal(HttpStatusCode.Forbidden, check.StatusCode);
    }
}
