using System.Net;
using System.Text.Json;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Turning the Azure triggers of an APPROVED conversion into a proposal of its own, against the real API, the real database and the LLM simulator.
// The Azure YAML of the stub pipeline 42 carries a branch trigger, a pull request trigger and a schedule.
public sealed class WorkflowTriggerApiTests() : ProposalApiTestBase(singleReviewer: false)
{
    private async Task<(JsonElement Conversion, Guid ParentId)> ApprovedConversionAsync()
    {
        var conversion = await ReadyProposalAsync();
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Reviewed the manual-only workflow." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return (conversion, conversion.GetProperty("proposalId").GetGuid());
    }

    private static string DeriveUrl(Guid id) => $"/api/workloads/pipelines/proposals/{id}/derive-triggers";
    private static string[] Codes(JsonElement triggers) => triggers.GetProperty("differences").EnumerateArray().Select(d => d.GetProperty("code").GetString()!).Order(StringComparer.Ordinal).ToArray();

    private async Task<int> ModelRequestsAsync()
    {
        using var document = JsonDocument.Parse(await Control.GetStringAsync(Origin + "/_sim/requests"));
        return document.RootElement.GetArrayLength();
    }

    [RequiresAnalysersFact]
    public async Task The_azure_triggers_become_a_proposal_of_their_own_with_no_model_call_and_are_approved_by_their_own_hash_under_the_trigger_policy()
    {
        var (conversion, parentId) = await ApprovedConversionAsync();
        var parentSha = conversion.GetProperty("workflowSha256").GetString()!;
        var modelCalls = await ModelRequestsAsync();

        using var response = await SendAsync(HttpMethod.Post, DeriveUrl(parentId), Who.Admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var derived = await JsonOf(response);
        var id = derived.GetProperty("id").GetGuid();

        // a proposal of its own: another id, another hash, linked to the approved parent, not approved by anything yet
        Assert.NotEqual(parentId, id);
        Assert.Equal("Triggers", derived.GetProperty("kind").GetString());
        Assert.Equal(parentId, derived.GetProperty("parentProposalId").GetGuid());
        Assert.Equal("Proposed", derived.GetProperty("state").GetString());
        Assert.NotEqual(parentSha, derived.GetProperty("workflowSha256").GetString());
        Assert.Equal(JsonValueKind.Null, derived.GetProperty("approvedSha256").ValueKind);
        Assert.Equal(Api.AdminName, derived.GetProperty("createdBy").GetString());

        // no model saw the Azure YAML: deriving made no request to it
        Assert.Equal(modelCalls, await ModelRequestsAsync());

        // only the on: entry changed
        var yaml = derived.GetProperty("workflowYaml").GetString()!;
        Assert.Contains("  push:\n    branches:\n      - 'main'\n      - 'release/**'\n", yaml);
        Assert.Contains("  pull_request:\n    branches:\n      - 'main'\n", yaml);
        Assert.Contains("  schedule:\n    - cron: '0 3 * * 1-5'\n", yaml);
        Assert.Contains("  workflow_dispatch:\n", yaml);
        var parentLines = conversion.GetProperty("workflowYaml").GetString()!.TrimEnd().Split('\n');
        var derivedLines = yaml.TrimEnd().Split('\n');
        Assert.Equal(parentLines.SkipWhile(l => !l.StartsWith("permissions:")), derivedLines.SkipWhile(l => !l.StartsWith("permissions:")));

        var triggers = derived.GetProperty("triggers");
        Assert.Equal(["push — branches: main, release/**", "pull_request — branches: main", "schedule: 0 3 * * 1-5"],
            triggers.GetProperty("mapped").EnumerateArray().Select(m => m.GetString()));
        Assert.Contains("TRIGGER_PR_AZURE_REPOS_IGNORED", Codes(triggers));
        Assert.Contains("TRIGGER_STARTS_PRIVILEGED_JOBS", Codes(triggers)); // the stub workflow reads a secret
        Assert.DoesNotContain("SENSITIVE_SOURCE_VALUE", derived.GetRawText());
        Assert.DoesNotContain("DONT_SHOW", derived.GetRawText());

        // kept in the database with its kind and parent, and listed with its kind but without its text
        var row = await InDbAsync(db => db.WorkflowProposals.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.Equal(("Triggers", parentId), (row.Kind, row.ParentProposalId));
        using var list = await SendAsync(HttpMethod.Get, $"/api/workloads/pipelines/proposals?sourceConnectionId={Api.SourceConnectionId}&projectId=project&pipelineId=42");
        var listed = (await JsonOf(list)).EnumerateArray().ToArray();
        Assert.Equal("Triggers", listed.Single(e => e.GetProperty("id").GetGuid() == id).GetProperty("kind").GetString());
        Assert.Equal("Conversion", listed.Single(e => e.GetProperty("id").GetGuid() == parentId).GetProperty("kind").GetString());

        // its differences are findings of the "triggers" gate and have to be accepted by name; gate warnings alone are not enough
        var sha = derived.GetProperty("workflowSha256").GetString()!;
        var gateWarnings = derived.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("gate").GetString() != "triggers" && f.GetProperty("severity").GetString() == "Warning")
            .Select(f => f.GetProperty("code").GetString()!).Distinct().ToArray();
        using var short_ = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{id}/approve", Who.Superadmin, new { workflowSha256 = sha, acknowledgedWarnings = gateWarnings, note = "Reviewed." });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, short_.StatusCode);
        var refused = await JsonOf(short_);
        Assert.Equal("WarningsNotAcknowledged", refused.GetProperty("error").GetString());
        Assert.Equal(Codes(triggers).Except(gateWarnings).Order(StringComparer.Ordinal), refused.GetProperty("details").EnumerateArray().Select(d => d.GetString()!).Order(StringComparer.Ordinal));

        // four eyes apply to it too: its author cannot approve it
        var all = Warned(derived);
        using var self = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{id}/approve", Who.Admin, new { workflowSha256 = sha, acknowledgedWarnings = all, note = "Reviewed." });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);

        using var approved = await SendAsync(HttpMethod.Post, $"/api/workloads/pipelines/proposals/{id}/approve", Who.Superadmin, new { workflowSha256 = sha, acknowledgedWarnings = all, note = "Reviewed the triggers and the differences." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var detail = await JsonOf(approved);
        Assert.Equal("Approved", detail.GetProperty("state").GetString());
        Assert.Equal(sha, detail.GetProperty("approvedSha256").GetString());
        var approval = detail.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "Approved");
        Assert.Equal(WorkflowGatePolicy.Default.WithAutomaticTriggers().Version, approval.GetProperty("policyVersion").GetString());
        Assert.Equal(all, approval.GetProperty("acknowledgedWarnings").EnumerateArray().Select(a => a.GetString()!).Order(StringComparer.Ordinal));

        // the manual-only parent keeps its own approval and policy
        using var parent = await SendAsync(HttpMethod.Get, Url(conversion));
        var parentView = await JsonOf(parent);
        Assert.Equal("Approved", parentView.GetProperty("state").GetString());
        Assert.Equal(parentSha, parentView.GetProperty("approvedSha256").GetString());
        Assert.Equal("Conversion", parentView.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, parentView.GetProperty("parentProposalId").ValueKind);
        Assert.Equal(JsonValueKind.Null, parentView.GetProperty("triggers").ValueKind);
    }

    [RequiresAnalysersFact]
    public async Task Only_an_approved_conversion_is_a_parent_and_triggers_are_never_derived_from_triggers()
    {
        var pending = await ReadyProposalAsync();
        var pendingId = pending.GetProperty("proposalId").GetGuid();
        using var notApproved = await SendAsync(HttpMethod.Post, DeriveUrl(pendingId));
        Assert.Equal(HttpStatusCode.Conflict, notApproved.StatusCode);
        Assert.Equal("ProposalNotApproved", (await JsonOf(notApproved)).GetProperty("error").GetString());
        var before = await InDbAsync(db => db.WorkflowProposals.CountAsync(x => x.Kind == "Triggers" && x.ParentProposalId == pendingId));
        Assert.Equal(0, before);

        var (_, parentId) = await ApprovedConversionAsync();
        using var first = await SendAsync(HttpMethod.Post, DeriveUrl(parentId));
        var derivedId = (await JsonOf(first)).GetProperty("id").GetGuid();
        using var fromTriggers = await SendAsync(HttpMethod.Post, DeriveUrl(derivedId)); // not approved either
        Assert.Equal("ProposalNotApproved", (await JsonOf(fromTriggers)).GetProperty("error").GetString());

        using var ghost = await SendAsync(HttpMethod.Post, DeriveUrl(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, DeriveUrl(parentId), Who.Anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, DeriveUrl(parentId), Who.User)).StatusCode);
    }

    // What the service enforces, the database enforces too: these statements bypass the service on purpose.
    [RequiresAnalysersFact]
    public async Task The_database_keeps_kind_and_parent_consistent_and_fixed()
    {
        var (_, parentId) = await ApprovedConversionAsync();
        using var derivedResponse = await SendAsync(HttpMethod.Post, DeriveUrl(parentId));
        var id = (await JsonOf(derivedResponse)).GetProperty("id").GetGuid();

        async Task<string> RefusedAsync(string sql, params object[] parameters)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => InDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, parameters)));
            return ex.ToString();
        }

        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_proposals SET kind = 'Conversion', parent_proposal_id = NULL WHERE id = {0}", id));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_proposals SET parent_proposal_id = {1} WHERE id = {0}", id, id));
        Assert.Contains("immutable", await RefusedAsync("UPDATE workflow_proposals SET kind = 'Triggers' WHERE id = {0}", parentId));
        // a Triggers proposal has a parent and a conversion has none; nothing is its own parent
        // the columns of the source row are copied so that only kind and parent_proposal_id can be what is refused
        string Insert(string kind, string parent) => $$"""
            INSERT INTO workflow_proposals (id, state, source_connection_id, project_id, pipeline_id, ai_connection_id, workflow_yaml, workflow_sha256, evidence, created_by_id, created_by, created_at_utc, kind, parent_proposal_id)
            SELECT {0}, 'Proposed', source_connection_id, project_id, pipeline_id, ai_connection_id, workflow_yaml, workflow_sha256, evidence, created_by_id, created_by, created_at_utc, '{{kind}}', {{parent}}
            FROM workflow_proposals WHERE id = {1}
            """;
        Assert.Contains("ck_workflow_proposals_kind", await RefusedAsync(Insert("Triggers", "NULL"), Guid.NewGuid(), parentId));
        Assert.Contains("ck_workflow_proposals_kind", await RefusedAsync(Insert("Conversion", "id"), Guid.NewGuid(), parentId));
        Assert.Contains("ck_workflow_proposals_kind", await RefusedAsync(Insert("Other", "NULL"), Guid.NewGuid(), parentId));
        Assert.Contains("ck_workflow_proposals_kind", await RefusedAsync(Insert("Triggers", "{0}"), parentId, parentId)); // nothing is its own parent
        Assert.Contains("audit record", await RefusedAsync("DELETE FROM workflow_proposals WHERE id = {0}", parentId)); // the parent cannot disappear under its child
        Assert.Equal(1, await InDbAsync(db => db.WorkflowProposals.CountAsync(x => x.ParentProposalId == parentId)));
    }
}
