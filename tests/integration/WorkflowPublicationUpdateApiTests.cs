using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Replacing a workflow this product published earlier, and checking an attempt against the destination, on the real API (PostgreSQL ledger, JWT, production DI)
// against the GitHub simulator over real HTTP. A conversion is published and merged first; then its Azure triggers are derived, approved and published as a
// replacement of that file, on a new branch, never on the base branch.
public sealed class WorkflowPublicationUpdateApiTests : PublicationApiTestBase
{
    private const string Repo = "pub-upd";

    private static string DeriveUrl(Guid id) => $"/api/workloads/pipelines/proposals/{id}/derive-triggers";
    private static string Decode(JsonElement file) => Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!.Replace("\n", "")));
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // An approved conversion published into the repository and merged: the base branch has the file exactly as the conversion's text.
    private async Task<(JsonElement Conversion, string OldText, string OldBlob)> PublishedAndMergedAsync(string repo = Repo)
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        await RepoAsync(repo);
        var (status, published) = await PublishAsync(conversion, repo, await ReadyPlanHashAsync(conversion, repo));
        Assert.Equal(HttpStatusCode.OK, status);
        await GitHub!.SendAsync("PUT", $"/repos/orion-org/{repo}/pulls/{published.GetProperty("pullNumber").GetInt32()}/merge", new { merge_method = "squash" });
        var file = await GitHub.SendAsync("GET", $"/repos/orion-org/{repo}/contents/{WorkflowPath}?ref=main");
        Assert.Equal(conversion.GetProperty("workflowYaml").GetString()!.TrimEnd(), Decode(file).TrimEnd());
        return (conversion, Decode(file), file.GetProperty("sha").GetString()!);
    }

    // The Azure triggers of the approved conversion as a proposal of their own, approved by a second person. The handle has what the shared helpers read.
    private async Task<JsonElement> ApprovedTriggersAsync(JsonElement conversion)
    {
        using var derived = await SendAsync(HttpMethod.Post, DeriveUrl(conversion.GetProperty("proposalId").GetGuid()), Who.Admin);
        Assert.Equal(HttpStatusCode.OK, derived.StatusCode);
        var detail = await JsonOf(derived);
        var id = detail.GetProperty("id").GetGuid();
        using var approved = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{id}/approve", Who.Superadmin,
            new { workflowSha256 = detail.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(detail), note = "Reviewed the triggers." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return JsonSerializer.SerializeToElement(new { proposalId = id, workflowSha256 = detail.GetProperty("workflowSha256").GetString(), workflowYaml = detail.GetProperty("workflowYaml").GetString() });
    }

    private static IEnumerable<string?> Codes(JsonElement body) => body.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString());

    [RequiresAnalysersFact]
    public async Task The_published_and_merged_workflow_is_replaced_by_the_version_with_triggers_on_a_new_branch_and_the_base_branch_is_not_touched()
    {
        var (conversion, oldText, oldBlob) = await PublishedAndMergedAsync();
        var parentSha = conversion.GetProperty("workflowSha256").GetString()!;
        var triggers = await ApprovedTriggersAsync(conversion);
        var baseTip = await TipAsync(Repo);

        var (planStatus, plan) = await PlanAsync(triggers, Repo);
        Assert.Equal(HttpStatusCode.OK, planStatus);
        Assert.Equal(("Update", parentSha, true), (plan.GetProperty("mode").GetString(), plan.GetProperty("replacesWorkflowSha256").GetString(), plan.GetProperty("ready").GetBoolean()));
        Assert.Contains("WorkflowWillBeReplaced", Codes(plan));
        Assert.DoesNotContain(plan.GetProperty("findings").EnumerateArray(), f => f.GetProperty("severity").GetString() == "Blocker");
        Assert.Contains(plan.GetProperty("steps").EnumerateArray(), s => s.GetProperty("code").GetString() == "Replace");
        Assert.StartsWith("Add automatic triggers to the GitHub Actions workflow", plan.GetProperty("pullRequestTitle").GetString());
        Assert.Contains($"SHA-256 `{parentSha}`", plan.GetProperty("pullRequestBody").GetString());
        var head = plan.GetProperty("headBranch").GetString()!;
        var planHash = plan.GetProperty("planSha256").GetString()!;
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        var (status, published) = await PublishAsync(triggers, Repo, planHash);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("Published", "Update", parentSha, 2), (published.GetProperty("state").GetString(), published.GetProperty("mode").GetString(),
            published.GetProperty("replacedWorkflowSha256").GetString(), published.GetProperty("pullNumber").GetInt32()));

        // what exists: the new text on the new branch, the old text still on main, nothing merged, the commit sits on the base it was planned against
        var commit = published.GetProperty("commitSha").GetString()!;
        Assert.Equal(commit, await TipAsync(Repo, head));
        Assert.Equal(baseTip, await TipAsync(Repo));
        var onHead = await GitHub.SendAsync("GET", $"/repos/orion-org/{Repo}/contents/{WorkflowPath}?ref={head}");
        Assert.Equal(plan.GetProperty("workflowYaml").GetString(), Decode(onHead));
        Assert.Contains("  push:", Decode(onHead));
        Assert.NotEqual(oldBlob, onHead.GetProperty("sha").GetString());
        var onMain = await GitHub.SendAsync("GET", $"/repos/orion-org/{Repo}/contents/{WorkflowPath}?ref=main");
        Assert.Equal((oldText, oldBlob), (Decode(onMain), onMain.GetProperty("sha").GetString()));
        var parents = (await GitHub.SendAsync("GET", $"/repos/orion-org/{Repo}/git/commits/{commit}")).GetProperty("parents").EnumerateArray().Select(p => p.GetProperty("sha").GetString()).ToArray();
        Assert.Equal([baseTip], parents);
        var pull = await GitHub.SendAsync("GET", $"/repos/orion-org/{Repo}/pulls/2");
        Assert.Equal(("open", false, head, "main"), (pull.GetProperty("state").GetString(), pull.GetProperty("merged").GetBoolean(), pull.GetProperty("head").GetProperty("ref").GetString(),
            pull.GetProperty("base").GetProperty("ref").GetString()));
        Assert.Equal(plan.GetProperty("pullRequestBody").GetString(), pull.GetProperty("body").GetString());
        // three writes, none of them to the base branch: the branch, the file (on that branch), the pull request
        Assert.Equal([("POST", $"/api/v3/repos/orion-org/{Repo}/git/refs"), ("PUT", $"/api/v3/repos/orion-org/{Repo}/contents/{WorkflowPath}"), ("POST", $"/api/v3/repos/orion-org/{Repo}/pulls")],
            await WritesAsync(Repo));

        // the ledger keeps what was replaced: the blob and the text
        var row = Assert.Single(await RowsAsync(triggers));
        Assert.Equal(("Update", oldBlob, parentSha, triggers.GetProperty("workflowSha256").GetString()), (row.Mode, row.ReplacedBlobSha, row.ReplacedWorkflowSha256, row.WorkflowSha256));
        using var listed = await SendAsync(HttpMethod.Get, Url(triggers, "/publications"), Who.Admin);
        var entry = (await JsonOf(listed)).EnumerateArray().Single();
        Assert.Equal(("Update", parentSha), (entry.GetProperty("mode").GetString(), entry.GetProperty("replacedWorkflowSha256").GetString()));

        // the same plan is not published twice
        var (again, refusal) = await PublishAsync(triggers, Repo, planHash);
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanNotReady"), (again, refusal.GetProperty("error").GetString()));
        Assert.Contains("HeadBranchExists", refusal.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, (await WritesAsync(Repo)).Length);

        // merged by a person, the base has the approved text and there is nothing left to publish
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{Repo}/pulls/2/merge", new { merge_method = "squash" });
        var (_, after) = await PlanAsync(triggers, Repo);
        Assert.False(after.GetProperty("ready").GetBoolean());
        Assert.Contains("AlreadyPublished", Codes(after));
        Assert.Equal("Create", after.GetProperty("mode").GetString());
    }

    [RequiresAnalysersFact]
    public async Task The_conversions_pull_request_blocks_the_triggers_plan_while_it_is_open_and_the_plan_follows_what_happens_to_it()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        await RepoAsync(Repo);
        var (published, parentPull) = await PublishAsync(conversion, Repo, await ReadyPlanHashAsync(conversion, Repo));
        Assert.Equal(HttpStatusCode.OK, published);
        var number = parentPull.GetProperty("pullNumber").GetInt32();
        var triggers = await ApprovedTriggersAsync(conversion);
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        // still open: the file is not on the base branch, so the path looks free, but the same file added on another branch would conflict with the open pull request
        var (_, blocked) = await PlanAsync(triggers, Repo);
        Assert.False(blocked.GetProperty("ready").GetBoolean());
        var finding = blocked.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("code").GetString() == "EarlierPullRequestOpen");
        Assert.Equal("Blocker", finding.GetProperty("severity").GetString());
        Assert.Contains($"#{number}", finding.GetProperty("message").GetString());
        Assert.Contains("The conversion this proposal was derived from", finding.GetProperty("message").GetString());
        Assert.Equal("Create", blocked.GetProperty("mode").GetString());

        // the product refuses to publish it, and writes nothing
        var (status, refusal) = await PublishAsync(triggers, Repo, blocked.GetProperty("planSha256").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanNotReady"), (status, refusal.GetProperty("error").GetString()));
        Assert.Contains("EarlierPullRequestOpen", refusal.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(await WritesAsync(Repo));
        Assert.Empty(await RowsAsync(triggers));

        // closed without merging: nothing of it is on the base branch, nothing conflicts, the path is free
        await GitHub.SendAsync("PATCH", $"/repos/orion-org/{Repo}/pulls/{number}", new { state = "closed" });
        var (_, closed) = await PlanAsync(triggers, Repo);
        Assert.True(closed.GetProperty("ready").GetBoolean(), closed.ToString());
        Assert.DoesNotContain("EarlierPullRequestOpen", Codes(closed));
        Assert.Equal("Create", closed.GetProperty("mode").GetString());

        // reopened: blocked again; the plan follows the destination, not a note made once
        await GitHub.SendAsync("PATCH", $"/repos/orion-org/{Repo}/pulls/{number}", new { state = "open" });
        var (_, reopened) = await PlanAsync(triggers, Repo);
        Assert.False(reopened.GetProperty("ready").GetBoolean());
        Assert.Contains("EarlierPullRequestOpen", Codes(reopened));

        // merged: the file is on the base branch exactly as approved, so the triggers replace it, and nothing stands in the way
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{Repo}/pulls/{number}/merge", new { merge_method = "squash" });
        await GitHub.SendAsync("DELETE", "/_sim/requests"); // what a person did is not the product's: from here on only planning is measured
        var (_, merged) = await PlanAsync(triggers, Repo);
        Assert.True(merged.GetProperty("ready").GetBoolean(), merged.ToString());
        Assert.Equal("Update", merged.GetProperty("mode").GetString());
        Assert.DoesNotContain("EarlierPullRequestOpen", Codes(merged));
        Assert.Empty(await WritesAsync(Repo)); // planning only ever read
    }

    [RequiresAnalysersFact]
    public async Task A_workflow_somebody_edited_after_it_was_merged_is_theirs_and_is_not_replaced()
    {
        var (conversion, oldText, oldBlob) = await PublishedAndMergedAsync();
        await GitHub!.SendAsync("PUT", $"/repos/orion-org/{Repo}/contents/{WorkflowPath}", new { message = "tweak the workflow", content = B64(oldText + "# a person added this\n"), sha = oldBlob, branch = "main" });
        var triggers = await ApprovedTriggersAsync(conversion);
        await GitHub.SendAsync("DELETE", "/_sim/requests");

        var (_, plan) = await PlanAsync(triggers, Repo);
        Assert.False(plan.GetProperty("ready").GetBoolean());
        Assert.Contains("WorkflowPathOccupied", Codes(plan));
        Assert.Equal(("Create", JsonValueKind.Null), (plan.GetProperty("mode").GetString(), plan.GetProperty("replacesWorkflowSha256").ValueKind));
        Assert.DoesNotContain("WorkflowWillBeReplaced", Codes(plan));

        var (status, refusal) = await PublishAsync(triggers, Repo, plan.GetProperty("planSha256").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanNotReady"), (status, refusal.GetProperty("error").GetString()));
        Assert.Contains("WorkflowPathOccupied", refusal.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(await WritesAsync(Repo));
        Assert.Empty(await RowsAsync(triggers));
    }

    [RequiresAnalysersFact]
    public async Task A_workflow_edited_between_the_plan_and_the_confirmation_makes_the_confirmation_stale_and_nothing_is_written()
    {
        var (conversion, oldText, oldBlob) = await PublishedAndMergedAsync();
        var triggers = await ApprovedTriggersAsync(conversion);
        var (_, plan) = await PlanAsync(triggers, Repo);
        Assert.Equal("Update", plan.GetProperty("mode").GetString());
        await GitHub!.SendAsync("PUT", $"/repos/orion-org/{Repo}/contents/{WorkflowPath}", new { message = "edit after the plan was shown", content = B64(oldText + "# late edit\n"), sha = oldBlob, branch = "main" });
        await GitHub.SendAsync("DELETE", "/_sim/requests");

        var (status, refusal) = await PublishAsync(triggers, Repo, plan.GetProperty("planSha256").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanChanged"), (status, refusal.GetProperty("error").GetString()));
        Assert.Empty(await WritesAsync(Repo));
        Assert.Empty(await RowsAsync(triggers));
    }

    [RequiresAnalysersFact]
    public async Task Once_the_conversion_approval_is_withdrawn_the_file_it_put_there_is_no_longer_named_as_replaceable()
    {
        var (conversion, _, _) = await PublishedAndMergedAsync();
        var triggers = await ApprovedTriggersAsync(conversion);
        using var revoked = await SendAsync(HttpMethod.Post, Url(conversion, "/revoke"), Who.Admin, new { note = "The manual-only workflow was withdrawn." });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var (_, plan) = await PlanAsync(triggers, Repo);
        Assert.False(plan.GetProperty("ready").GetBoolean());
        Assert.Contains("WorkflowPathOccupied", Codes(plan));
        Assert.Equal("Create", plan.GetProperty("mode").GetString());
    }

    [RequiresAnalysersFact]
    public async Task A_replacement_whose_file_write_fails_is_recorded_as_possibly_written_and_the_reconciliation_shows_the_branch_that_was_left()
    {
        var (conversion, _, oldBlob) = await PublishedAndMergedAsync();
        var parentSha = conversion.GetProperty("workflowSha256").GetString()!;
        var triggers = await ApprovedTriggersAsync(conversion);
        var (_, plan) = await PlanAsync(triggers, Repo);
        var head = plan.GetProperty("headBranch").GetString()!;
        await GitHub!.SendAsync("POST", "/_sim/faults", new { pathRegex = @"/contents/\.github/workflows/", method = "PUT", status = 500, message = "boom" });
        await GitHub.SendAsync("DELETE", "/_sim/requests");

        var (status, failure) = await PublishAsync(triggers, Repo, plan.GetProperty("planSha256").GetString());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "PublicationFileFailed"), (status, failure.GetProperty("error").GetString()));
        var record = failure.GetProperty("publication");
        Assert.Equal(("Failed", "FileCommitting", true, "Update"), (record.GetProperty("state").GetString(), record.GetProperty("failedAtState").GetString(), record.GetProperty("writesPossible").GetBoolean(),
            record.GetProperty("mode").GetString()));
        Assert.Equal(2, (await WritesAsync(Repo)).Length); // the branch and one attempt at the file: nothing repeated, no pull request
        var row = Assert.Single(await RowsAsync(triggers));
        Assert.Equal(("Update", oldBlob, parentSha), (row.Mode, row.ReplacedBlobSha, row.ReplacedWorkflowSha256));
        await GitHub.SendAsync("DELETE", "/_sim/faults");
        await GitHub.SendAsync("DELETE", "/_sim/requests");

        // what a person would find: a branch at the base commit that still holds the OLD text, and no pull request. The product says so and will not act.
        var (reconciled, body) = await ReconcileAsync(triggers, row.Id);
        Assert.Equal(HttpStatusCode.OK, reconciled);
        Assert.Equal("BranchWithoutPullRequest", body.GetProperty("verdict").GetString());
        Assert.Contains("ProductWillNotRetry", Codes(body));
        Assert.Equal(("PriorVersion", "PriorVersion"), (body.GetProperty("observed").GetProperty("fileOnHead").GetString(), body.GetProperty("observed").GetProperty("fileOnBase").GetString()));
        Assert.Equal(head, record.GetProperty("headBranch").GetString());
        Assert.NotNull(body.GetProperty("observed").GetProperty("headBranchSha").GetString());
        Assert.False(body.GetProperty("writesPerformed").GetBoolean());
        Assert.Empty(await WritesAsync(Repo));
    }

    // ---- reconciliation --------------------------------------------------------------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, JsonElement Body)> ReconcileAsync(JsonElement proposal, Guid publication, Who who = Who.Admin, Guid? destination = null)
    {
        using var response = await SendAsync(HttpMethod.Post, Url(proposal, $"/publications/{publication}/reconcile"), who, new { destinationConnectionId = destination ?? Api.DestinationConnectionId });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ReleaseAsync(JsonElement proposal, Guid publication, Who who = Who.Superadmin, string? note = "Removed what was left by hand.", Guid? destination = null)
    {
        using var response = await SendAsync(HttpMethod.Post, Url(proposal, $"/publications/{publication}/release"), who, new { destinationConnectionId = destination ?? Api.DestinationConnectionId, note });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string[] Details(JsonElement body) => body.GetProperty("details").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToArray();

    private async Task<(JsonElement Conversion, JsonElement Published, string Repo)> PublishedConversionAsync(string repo)
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        await RepoAsync(repo);
        var (status, published) = await PublishAsync(conversion, repo, await ReadyPlanHashAsync(conversion, repo));
        Assert.Equal(HttpStatusCode.OK, status);
        return (conversion, published, repo);
    }

    [RequiresAnalysersFact]
    public async Task An_open_pull_request_is_seen_as_open_then_as_merged_with_get_requests_only()
    {
        var (conversion, published, repo) = await PublishedConversionAsync("pub-rec");
        var id = published.GetProperty("id").GetGuid();
        await GitHub!.SendAsync("DELETE", "/_sim/requests");

        var (status, open) = await ReconcileAsync(conversion, id);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("OpenPullRequest", open.GetProperty("verdict").GetString());
        Assert.Empty(open.GetProperty("findings").EnumerateArray());
        var seen = open.GetProperty("observed");
        Assert.Equal(("Identical", "Absent", true), (seen.GetProperty("fileOnHead").GetString(), seen.GetProperty("fileOnBase").GetString(), seen.GetProperty("repositoryExists").GetBoolean()));
        var pull = Assert.Single(seen.GetProperty("pulls").EnumerateArray());
        Assert.Equal((1, "open", false), (pull.GetProperty("number").GetInt32(), pull.GetProperty("state").GetString(), pull.GetProperty("merged").GetBoolean()));
        Assert.Equal(("Published", false, published.GetProperty("commitSha").GetString()), (open.GetProperty("ledgerState").GetString(), open.GetProperty("writesPerformed").GetBoolean(), open.GetProperty("ledgerCommitSha").GetString()));

        // nothing but reads, all inside the one repository
        var calls = (await GitHub.SendAsync("GET", "/_sim/requests?limit=500")).EnumerateArray().Select(e => (Method: e.GetProperty("Method").GetString()!, Path: e.GetProperty("Path").GetString()!))
            .Where(c => !c.Path.StartsWith("/_sim", StringComparison.Ordinal)).ToArray(); // the simulator's own control calls are not the product's
        Assert.NotEmpty(calls);
        Assert.All(calls, c => { Assert.Equal("GET", c.Method); Assert.StartsWith($"/api/v3/repos/orion-org/{repo}", c.Path); });

        await GitHub.SendAsync("PUT", $"/repos/orion-org/{repo}/pulls/1/merge", new { merge_method = "squash" });
        var (_, merged) = await ReconcileAsync(conversion, id);
        Assert.Equal("Merged", merged.GetProperty("verdict").GetString());
        Assert.Equal("Identical", merged.GetProperty("observed").GetProperty("fileOnBase").GetString());
        Assert.True(merged.GetProperty("observed").GetProperty("pulls")[0].GetProperty("merged").GetBoolean());
        Assert.Contains(Codes(merged), c => c == "BaseMoved"); // the merge moved the base: information, not a problem
        Assert.DoesNotContain(merged.GetProperty("findings").EnumerateArray(), f => f.GetProperty("severity").GetString() == "Warning");
    }

    [RequiresAnalysersFact]
    public async Task A_branch_somebody_pushed_to_is_called_changed_and_a_pull_request_closed_without_merging_is_said_to_have_left_nothing()
    {
        var (conversion, published, repo) = await PublishedConversionAsync("pub-rec2");
        var id = published.GetProperty("id").GetGuid();
        var head = published.GetProperty("headBranch").GetString()!;
        var onHead = await GitHub!.SendAsync("GET", $"/repos/orion-org/{repo}/contents/{WorkflowPath}?ref={head}");
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{repo}/contents/{WorkflowPath}", new { message = "push to the branch", content = B64(Decode(onHead) + "# pushed by a person\n"), sha = onHead.GetProperty("sha").GetString(), branch = head });

        var (_, changed) = await ReconcileAsync(conversion, id);
        Assert.Equal("OpenPullRequestChanged", changed.GetProperty("verdict").GetString());
        Assert.Equal(["BranchMoved", "OpenPullRequestChanged"], Codes(changed).Order(StringComparer.Ordinal));
        Assert.Equal("Different", changed.GetProperty("observed").GetProperty("fileOnHead").GetString()); // not the approved text any more

        await GitHub.SendAsync("PATCH", $"/repos/orion-org/{repo}/pulls/1", new { state = "closed" });
        var (_, closed) = await ReconcileAsync(conversion, id);
        Assert.Equal("ClosedWithoutMerge", closed.GetProperty("verdict").GetString());
        Assert.Equal("Absent", closed.GetProperty("observed").GetProperty("fileOnBase").GetString());
        Assert.Contains("BranchMoved", Codes(closed));
    }

    [RequiresAnalysersFact]
    public async Task An_attempt_that_left_nothing_is_nothing_found_and_the_ledger_still_blocks_that_exact_plan_until_the_base_moves()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-rec3");
        var planHash = await ReadyPlanHashAsync(conversion, repo);
        await GitHub!.SendAsync("POST", "/_sim/faults", new { pathRegex = @"/contents/\.github/workflows/", method = "PUT", status = 500, message = "boom" });
        var (status, failure) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var id = failure.GetProperty("publication").GetProperty("id").GetGuid();
        var head = failure.GetProperty("publication").GetProperty("headBranch").GetString()!;
        await GitHub.SendAsync("DELETE", "/_sim/faults");

        var (_, leftover) = await ReconcileAsync(conversion, id);
        Assert.Equal("BranchWithoutPullRequest", leftover.GetProperty("verdict").GetString());
        Assert.Equal("Absent", leftover.GetProperty("observed").GetProperty("fileOnHead").GetString()); // the branch is there, the file never arrived

        // a person removes the branch; the product did not
        await GitHub.SendAsync("DELETE", $"/repos/orion-org/{repo}/git/refs/heads/{head}");
        var (_, clean) = await ReconcileAsync(conversion, id);
        Assert.Equal("NothingFound", clean.GetProperty("verdict").GetString());
        Assert.Contains("LedgerStillBlocksPlan", Codes(clean));
        // the destination looks free, but the ledger keeps this exact plan blocked: the plan says so, with the hash unchanged, and publishing is refused before anything is written
        var (_, blockedPlan) = await PlanAsync(conversion, repo);
        Assert.False(blockedPlan.GetProperty("ready").GetBoolean());
        Assert.Equal(planHash, blockedPlan.GetProperty("planSha256").GetString());
        Assert.Contains("AttemptBlocksPlan", Codes(blockedPlan));
        var writesBefore = (await WritesAsync(repo)).Length;
        var (again, refusal) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal((HttpStatusCode.Conflict, "PublicationPlanNotReady"), (again, refusal.GetProperty("error").GetString()));
        Assert.Contains("AttemptBlocksPlan", refusal.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(writesBefore, (await WritesAsync(repo)).Length);

        // "once the base moves": any commit on the base makes another plan, with another hash, and that one is not blocked
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{repo}/contents/CHANGES.md", new { message = "move the base", content = B64("# changes\n") });
        var (_, replan) = await PlanAsync(conversion, repo);
        Assert.True(replan.GetProperty("ready").GetBoolean(), replan.ToString());
        Assert.NotEqual(planHash, replan.GetProperty("planSha256").GetString());
        var (retried, second) = await PublishAsync(conversion, repo, replan.GetProperty("planSha256").GetString());
        Assert.Equal(HttpStatusCode.OK, retried);
        Assert.Equal("Published", second.GetProperty("state").GetString());
        Assert.Equal(2, (await RowsAsync(conversion)).Count); // the failed attempt stays on record next to the one that worked
    }

    // (the external endpoints allow a user twelve calls a minute: the two halves of this story are two tests so each stays inside it)
    [RequiresAnalysersFact]
    public async Task Releasing_is_for_a_superadmin_with_a_note_for_the_right_attempt_and_destination_and_refuses_what_is_still_there_or_cannot_be_read()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-rel");
        var planHash = await ReadyPlanHashAsync(conversion, repo);
        await GitHub!.SendAsync("POST", "/_sim/faults", new { pathRegex = @"/contents/\.github/workflows/", method = "PUT", status = 500, message = "boom" });
        var (failedStatus, failure) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failedStatus);
        var id = failure.GetProperty("publication").GetProperty("id").GetGuid();
        await GitHub.SendAsync("DELETE", "/_sim/faults");

        // who: only a superadmin; and a note is part of it
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReleaseAsync(conversion, id, Who.Anonymous)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReleaseAsync(conversion, id, Who.User)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReleaseAsync(conversion, id, Who.Admin)).Status);
        foreach (var note in new string?[] { null, "ok" })
        {
            var (noNote, noteBody) = await ReleaseAsync(conversion, id, note: note);
            Assert.Equal((HttpStatusCode.BadRequest, "NoteRequired"), (noNote, noteBody.GetProperty("error").GetString()));
        }
        // the right attempt and the right destination
        Assert.Equal(HttpStatusCode.NotFound, (await ReleaseAsync(conversion, Guid.NewGuid())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ReleaseAsync(await ApprovedAsync(), id)).Status);
        var (source, sourceBody) = await ReleaseAsync(conversion, id, destination: Api.SourceConnectionId);
        Assert.Equal((HttpStatusCode.BadRequest, "UnsupportedDestination"), (source, sourceBody.GetProperty("error").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await ReleaseAsync(conversion, id, destination: Guid.NewGuid())).Status);

        // the branch the attempt created is still there: the product sees it and refuses, and marks nothing
        var (leftover, refused) = await ReleaseAsync(conversion, id);
        Assert.Equal(HttpStatusCode.Conflict, leftover);
        Assert.Equal("PublicationLeftoversExist", refused.GetProperty("error").GetString());
        Assert.Equal(["BranchExists"], Details(refused));
        Assert.True(refused.GetProperty("publication").GetProperty("blocksItsPlan").GetBoolean());
        Assert.Null((await RowsAsync(conversion)).Single().ReleasedAtUtc);

        // the destination cannot be read: that is not a clean destination
        await GitHub.SendAsync("POST", "/_sim/faults", new { pathRegex = @"/repos/orion-org/pub-rel$", method = "GET", status = 500, message = "down" });
        var (unreadable, unreadableBody) = await ReleaseAsync(conversion, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreadable);
        Assert.Equal("DestinationUnreadable", unreadableBody.GetProperty("error").GetString());
        await GitHub.SendAsync("DELETE", "/_sim/faults");
        Assert.Null((await RowsAsync(conversion)).Single().ReleasedAtUtc);
    }

    [RequiresAnalysersFact]
    public async Task A_superadmin_releases_an_attempt_after_the_product_has_looked_at_the_destination_and_then_the_same_plan_goes_through()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-rel");
        var planHash = await ReadyPlanHashAsync(conversion, repo);
        await GitHub!.SendAsync("POST", "/_sim/faults", new { pathRegex = @"/contents/\.github/workflows/", method = "PUT", status = 500, message = "boom" });
        var (failedStatus, failure) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failedStatus);
        var id = failure.GetProperty("publication").GetProperty("id").GetGuid();
        var head = failure.GetProperty("publication").GetProperty("headBranch").GetString()!;
        await GitHub.SendAsync("DELETE", "/_sim/faults");

        // a person removes the branch: the plan still cannot be published, and says why
        await GitHub.SendAsync("DELETE", $"/repos/orion-org/{repo}/git/refs/heads/{head}");
        var (_, blockedPlan) = await PlanAsync(conversion, repo);
        Assert.False(blockedPlan.GetProperty("ready").GetBoolean());
        Assert.Equal(["AttemptBlocksPlan"], blockedPlan.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("severity").GetString() == "Blocker").Select(f => f.GetProperty("code").GetString()));
        Assert.Equal(planHash, blockedPlan.GetProperty("planSha256").GetString());

        // the release: the product reads again (GET only) and the attempt is marked, not changed
        await GitHub.SendAsync("DELETE", "/_sim/requests");
        var (released, body) = await ReleaseAsync(conversion, id, note: "  The branch was deleted from the web page.  ");
        Assert.Equal(HttpStatusCode.OK, released);
        Assert.Empty(await WritesAsync(repo));
        Assert.Equal(("Failed", true, false, "The branch was deleted from the web page."), (body.GetProperty("state").GetString(), body.GetProperty("writesPossible").GetBoolean(), body.GetProperty("blocksItsPlan").GetBoolean(), body.GetProperty("releaseNote").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("releasedBy").GetString()));
        var row = Assert.Single(await RowsAsync(conversion));
        Assert.Equal(("Failed", "PublicationFileFailed", true), (row.State, row.ErrorCode, row.WritesPossible));
        Assert.NotNull(row.ReleasedAtUtc);
        using (var evidence = JsonDocument.Parse(row.ReleaseEvidence!))
        {
            Assert.False(evidence.RootElement.GetProperty("headBranchExists").GetBoolean());
            Assert.Equal("Failed", evidence.RootElement.GetProperty("ledgerState").GetString());
            Assert.DoesNotContain("run:", row.ReleaseEvidence); // no workflow text, no credential
        }

        // decided once
        var (twice, twiceBody) = await ReleaseAsync(conversion, id);
        Assert.Equal(HttpStatusCode.Conflict, twice);
        Assert.Equal("PublicationAlreadyReleased", twiceBody.GetProperty("error").GetString());

        // the view says so, and the same plan, with the same hash, now goes through; the released attempt stays beside the new one
        var (_, view) = await ReconcileAsync(conversion, id);
        Assert.True(view.GetProperty("ledgerReleased").GetBoolean());
        Assert.False(view.GetProperty("ledgerBlocksPlan").GetBoolean());
        Assert.Contains("AttemptReleased", Codes(view));
        Assert.DoesNotContain("LedgerStillBlocksPlan", Codes(view));
        Assert.Equal(planHash, await ReadyPlanHashAsync(conversion, repo));
        var (retried, second) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.OK, retried);
        Assert.Equal("Published", second.GetProperty("state").GetString());
        var rows = await RowsAsync(conversion);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Count(r => r.ReleasedAtUtc is not null));
    }

    [RequiresAnalysersFact]
    public async Task A_published_attempt_whose_pull_request_was_closed_and_whose_branch_is_gone_is_released_but_not_while_anything_of_it_is_there_and_never_after_a_merge()
    {
        var (conversion, published, repo) = await PublishedConversionAsync("pub-rel2");
        var id = published.GetProperty("id").GetGuid();
        var head = published.GetProperty("headBranch").GetString()!;
        var number = published.GetProperty("pullNumber").GetInt32();
        var planHash = published.GetProperty("planSha256").GetString()!;

        // open: the pull request and the branch are there, and the plan says what is in the way (not "the attempt")
        var (_, openPlan) = await PlanAsync(conversion, repo);
        Assert.False(openPlan.GetProperty("ready").GetBoolean());
        Assert.DoesNotContain("AttemptBlocksPlan", Codes(openPlan));
        var (openStatus, open) = await ReleaseAsync(conversion, id);
        Assert.Equal((HttpStatusCode.Conflict, "PublicationLeftoversExist"), (openStatus, open.GetProperty("error").GetString()));
        Assert.Equal(["BranchExists", "PublishedPullRequestNotClosed", "PullRequestOpen"], Details(open));

        // closed without merging, branch still there
        await GitHub!.SendAsync("PATCH", $"/repos/orion-org/{repo}/pulls/{number}", new { state = "closed" });
        var (_, closedWithBranch) = await ReleaseAsync(conversion, id);
        Assert.Equal(["BranchExists"], Details(closedWithBranch));

        // branch gone: nothing of it remains, only the ledger still counts the plan as published
        await GitHub.SendAsync("DELETE", $"/repos/orion-org/{repo}/git/refs/heads/{head}");
        var (_, blocked) = await PlanAsync(conversion, repo);
        Assert.False(blocked.GetProperty("ready").GetBoolean());
        Assert.Contains("AttemptBlocksPlan", Codes(blocked));
        var (_, view) = await ReconcileAsync(conversion, id);
        Assert.Equal("ClosedWithoutMerge", view.GetProperty("verdict").GetString());
        Assert.Contains("LedgerStillBlocksPlan", Codes(view));
        Assert.True(view.GetProperty("ledgerBlocksPlan").GetBoolean());

        var (status, released) = await ReleaseAsync(conversion, id, note: "Closed it on purpose: wrong repository settings.");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("Published", number, false), (released.GetProperty("state").GetString(), released.GetProperty("pullNumber").GetInt32(), released.GetProperty("blocksItsPlan").GetBoolean()));

        // the same plan is published again: a new pull request, and the closed one is not mistaken for an open one in the way
        Assert.Equal(planHash, await ReadyPlanHashAsync(conversion, repo));
        var (again, second) = await PublishAsync(conversion, repo, planHash);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.NotEqual(number, second.GetProperty("pullNumber").GetInt32());
        Assert.Equal(2, (await RowsAsync(conversion)).Count);
    }

    [RequiresAnalysersFact]
    public async Task A_merged_pull_request_is_never_released_because_the_file_is_on_the_base_branch()
    {
        var (conversion, published, repo) = await PublishedConversionAsync("pub-rel3");
        var id = published.GetProperty("id").GetGuid();
        await GitHub!.SendAsync("PUT", $"/repos/orion-org/{repo}/pulls/{published.GetProperty("pullNumber").GetInt32()}/merge", new { merge_method = "squash" });
        await GitHub.SendAsync("DELETE", $"/repos/orion-org/{repo}/git/refs/heads/{published.GetProperty("headBranch").GetString()}");

        var (status, refused) = await ReleaseAsync(conversion, id);
        Assert.Equal((HttpStatusCode.Conflict, "PublicationLeftoversExist"), (status, refused.GetProperty("error").GetString()));
        Assert.Equal(["PublishedPullRequestNotClosed", "PullRequestMerged"], Details(refused));
        Assert.Null(Assert.Single(await RowsAsync(conversion)).ReleasedAtUtc);
    }

    [RequiresAnalysersFact]
    public async Task An_attempt_that_wrote_nothing_is_not_released_and_one_that_never_finished_is_released_only_once_it_has_not_moved_for_fifteen_minutes()
    {
        await RegisterAppAsync();
        var conversion = await ApprovedAsync();
        var repo = await RepoAsync("pub-rel4");
        var planHash = await ReadyPlanHashAsync(conversion, repo);

        // refused before any write (the installation token could not be minted): the plan was never blocked, so there is nothing to release
        await GitHub!.SendAsync("POST", "/_sim/faults", new { pathRegex = @"/access_tokens$", method = "POST", status = 500, message = "no token" });
        var (failedStatus, failure) = await PublishAsync(conversion, repo, planHash);
        await GitHub.SendAsync("DELETE", "/_sim/faults");
        Assert.NotEqual(HttpStatusCode.OK, failedStatus);
        var clean = Assert.Single(await RowsAsync(conversion));
        Assert.Equal(("Failed", false), (clean.State, clean.WritesPossible));
        var (free, freeBody) = await ReleaseAsync(conversion, clean.Id);
        Assert.Equal(HttpStatusCode.Conflict, free);
        Assert.Equal("PublicationAlreadyFree", freeBody.GetProperty("error").GetString());
        Assert.Null((await RowsAsync(conversion)).Single(r => r.Id == clean.Id).ReleasedAtUtc);

        // an attempt that never finished (the process died): running until it has not moved for fifteen minutes
        var proposal = conversion.GetProperty("proposalId").GetGuid();
        var stuck = Guid.NewGuid();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc) " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-rel4', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-release-safe.yml', 'BranchCreating', false, {5}, 'tester', now(), now())",
            stuck, proposal, new string('b', 64), conversion.GetProperty("workflowSha256").GetString()!, new string('c', 40), Guid.NewGuid()));
        var (running, runningBody) = await ReleaseAsync(conversion, stuck);
        Assert.Equal(HttpStatusCode.Conflict, running);
        Assert.Equal("PublicationStillRunning", runningBody.GetProperty("error").GetString());
        Assert.Equal("BranchCreating", (await RowsAsync(conversion)).Single(r => r.Id == stuck).State);

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE workflow_publications SET updated_at_utc = now() - interval '16 minutes' WHERE id = {0}", stuck));
        var (status, released) = await ReleaseAsync(conversion, stuck, note: "The API restarted while it was running.");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("Failed", "PublicationAbandoned", true, false), (released.GetProperty("state").GetString(), released.GetProperty("errorCode").GetString(), released.GetProperty("writesPossible").GetBoolean(), released.GetProperty("blocksItsPlan").GetBoolean()));
        var row = (await RowsAsync(conversion)).Single(r => r.Id == stuck);
        Assert.Equal(("Failed", "BranchCreating", "PublicationAbandoned", true), (row.State, row.FailedAtState, row.ErrorCode, row.WritesPossible));
        Assert.NotNull(row.ReleasedAtUtc);
    }

    [RequiresAnalysersFact]
    public async Task Reconciliation_is_for_the_people_who_may_plan_it_needs_the_right_attempt_and_the_right_destination_and_changes_nothing()
    {
        var (conversion, published, repo) = await PublishedConversionAsync("pub-rec");
        var id = published.GetProperty("id").GetGuid();
        var before = await RowsAsync(conversion);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ReconcileAsync(conversion, id, Who.Anonymous)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReconcileAsync(conversion, id, Who.User)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ReconcileAsync(conversion, id, Who.Superadmin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ReconcileAsync(conversion, Guid.NewGuid())).Status);

        // an attempt of another proposal is not reachable through this one
        var other = await ApprovedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await ReconcileAsync(other, id)).Status);
        // a destination that is not a verified destination, or does not exist
        var (source, sourceBody) = await ReconcileAsync(conversion, id, destination: Api.SourceConnectionId);
        Assert.Equal((HttpStatusCode.BadRequest, "UnsupportedDestination"), (source, sourceBody.GetProperty("error").GetString()));
        var (missing, missingBody) = await ReconcileAsync(conversion, id, destination: Guid.NewGuid());
        Assert.Equal((HttpStatusCode.BadRequest, "DestinationNotFound"), (missing, missingBody.GetProperty("error").GetString()));

        Assert.Equal(before.Select(r => (r.Id, r.State, r.UpdatedAtUtc)), (await RowsAsync(conversion)).Select(r => (r.Id, r.State, r.UpdatedAtUtc)));
        Assert.Equal(3, (await WritesAsync(repo)).Length);
    }

    // ---- the database -----------------------------------------------------------------------------------------------------------------------

    // What the service enforces, the database enforces too: these statements bypass the service on purpose.
    [RequiresAnalysersFact]
    public async Task The_database_keeps_the_mode_of_a_publication_and_what_it_replaces_consistent_and_fixed()
    {
        var conversion = await ApprovedAsync();
        var proposal = conversion.GetProperty("proposalId").GetGuid();
        var sha = conversion.GetProperty("workflowSha256").GetString()!;
        string Plan(char c) => new(c, 64);

        async Task<string> RefusedAsync(string sql, params object[] parameters)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => InDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, parameters)));
            return ex.ToString();
        }
        string Insert(string columns, string values) =>
            "INSERT INTO workflow_publications (id, proposal_id, plan_sha256, workflow_sha256, organization, repository, base_branch, base_sha, head_branch, workflow_path, state, writes_possible, requested_by_id, requested_by, started_at_utc, updated_at_utc" + columns + ") " +
            "VALUES ({0}, {1}, {2}, {3}, 'orion-org', 'pub-upd', 'main', {4}, 'migrate/azdo-42-abcd1234', '.github/workflows/azdo-42-release-safe.yml', 'Started', false, {5}, 'tester', now(), now()" + values + ")";
        object[] Args(char plan) => [Guid.NewGuid(), proposal, Plan(plan), sha, new string('c', 40), Guid.NewGuid()];

        // a row created without a mode is a creation that replaced nothing
        var plain = Args('a');
        Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync(Insert("", ""), plain)));
        var row = await InDbAsync(db => db.WorkflowPublications.AsNoTracking().SingleAsync(x => x.Id == (Guid)plain[0]));
        Assert.Equal(("Create", (string?)null, (string?)null), (row.Mode, row.ReplacedBlobSha, row.ReplacedWorkflowSha256));

        // a mode is Create or Update; an update names the blob and the text it replaces and a creation names neither; the text replaced is another text
        var blob = new string('b', 40); var prior = new string('f', 64);
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode", ", 'Delete'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode", ", 'Update'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_blob_sha", $", 'Update', '{blob}'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_blob_sha, replaced_workflow_sha256", $", 'Create', '{blob}', '{prior}'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_blob_sha, replaced_workflow_sha256", $", 'Update', 'not-a-blob', '{prior}'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_blob_sha, replaced_workflow_sha256", ", 'Update', '" + blob + "', 'short'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_blob_sha, replaced_workflow_sha256", $", 'Update', '{blob}', '{sha}'"), Args('b')));
        // each clause on its own: a creation naming only a blob, a creation naming only a text, an update naming only a text
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_blob_sha", $", 'Create', '{blob}'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_workflow_sha256", $", 'Create', '{prior}'"), Args('b')));
        Assert.Contains("ck_workflow_publications_mode", await RefusedAsync(Insert(", mode, replaced_workflow_sha256", $", 'Update', '{prior}'"), Args('b')));
        var update = Args('c');
        Assert.Equal(1, await InDbAsync(db => db.Database.ExecuteSqlRawAsync(Insert(", mode, replaced_blob_sha, replaced_workflow_sha256", $", 'Update', '{blob}', '{prior}'"), update)));

        // and once written, what a publication replaces cannot be rewritten
        var id = (Guid)update[0];
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET mode = 'Create', replaced_blob_sha = NULL, replaced_workflow_sha256 = NULL WHERE id = {0}", id));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET replaced_blob_sha = {1} WHERE id = {0}", id, new string('1', 40)));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET replaced_workflow_sha256 = {1} WHERE id = {0}", id, new string('2', 64)));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_publications SET mode = 'Update' WHERE id = {0}", (Guid)plain[0]));
    }
}
