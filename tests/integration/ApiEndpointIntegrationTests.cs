using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace GitHubMigrator.Integration.Tests;

// Full HTTP requests through the production ASP.NET pipeline. PostgreSQL and
// JWT are real; Azure DevOps/GitHub only are replaced at the HTTP transport.
public sealed class ApiEndpointIntegrationTests(ApiHarness harness) : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task Health_and_readiness_are_served_by_real_API_and_database()
    {
        using var health = await harness.Client.GetAsync("/api/health");
        using var ready = await harness.Client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("ready", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Workload_endpoints_require_authentication_admin_role_and_completed_password_change()
    {
        using var anonymous = await PostAsync("/api/workloads/boards/preview",
            new { sourceConnectionId = harness.SourceConnectionId, projectId = "project", workItemIds = new[] { 101 } });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var ordinary = await PostAsync("/api/workloads/boards/preview",
            new { sourceConnectionId = harness.SourceConnectionId, projectId = "project", workItemIds = new[] { 101 } },
            await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);

        using var pending = await PostAsync("/api/workloads/boards/preview",
            new { sourceConnectionId = harness.SourceConnectionId, projectId = "project", workItemIds = new[] { 101 } },
            await harness.LoginAsync(harness.PendingName, ApiHarness.PendingPassword));
        Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode);
    }

    [Fact]
    public async Task Pipeline_route_exercises_real_reader_auth_database_and_returns_a_disabled_safe_workflow()
    {
        var jwt = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        using var response = await PostAsync("/api/workloads/pipelines/preview",
            new { sourceConnectionId = harness.SourceConnectionId,
                  destinationConnectionId = harness.DestinationConnectionId,
                  projectId = "project", pipelineId = 42, targetRepository = "migration-target" }, jwt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var data = JsonDocument.Parse(json);
        var root = data.RootElement;
        Assert.Equal(".github/workflows/azdo-42-release-safe.yml", root.GetProperty("workflowPath").GetString());
        Assert.False(root.GetProperty("deployableWithoutReview").GetBoolean());
        Assert.Contains("if: ${{ false }}", root.GetProperty("workflowYaml").GetString());
        Assert.Contains("REVIEW 1: UseDotNet@2", root.GetProperty("workflowYaml").GetString());
        Assert.Contains("TaskTranslationPending", json);
        Assert.Contains("candidate-service-connection", json);
        Assert.Equal("not-configured", root.GetProperty("ai").GetProperty("status").GetString());
        foreach (var forbidden in new[] { ApiHarness.SourceToken, ApiHarness.TargetToken,
                     ApiHarness.SourceValue, "DONT_SHOW_GROUP_TOKEN", "NEVER_SHOW_SERVICE_PASSWORD",
                     "DONT_SHOW_YAML_PASSWORD" })
            Assert.DoesNotContain(forbidden, json);
        Assert.Contains(harness.Calls, x => x.Url.AbsolutePath.EndsWith("/_apis/pipelines/42"));
        Assert.Contains(harness.Calls, x => x.Url.AbsolutePath.EndsWith("/actions/secrets"));
        Assert.All(harness.Calls, call => Assert.Equal("GET", call.Method));
        Assert.DoesNotContain(harness.Calls, x =>
            x.Url.ToString().Contains(ApiHarness.SourceToken) ||
            x.Url.ToString().Contains(ApiHarness.TargetToken));
    }

    [Fact]
    public async Task Provider_403_is_a_422_with_no_false_success_preview()
    {
        var token = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        using var response = await PostAsync("/api/workloads/pipelines/preview",
            new { sourceConnectionId = harness.SourceConnectionId,
                  destinationConnectionId = harness.DestinationConnectionId,
                  projectId = "project", pipelineId = 43, targetRepository = "migration-target" }, token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ProviderReadFailed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bad_pipeline_ID_is_a_validation_400_before_reading_provider()
    {
        var token = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var before = harness.Calls.Count;
        using var response = await PostAsync("/api/workloads/pipelines/preview",
            new { sourceConnectionId = harness.SourceConnectionId,
                  destinationConnectionId = harness.DestinationConnectionId,
                  projectId = "project", pipelineId = 0, targetRepository = "migration-target" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidPipelineId", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, harness.Calls.Count);
    }

    [Fact]
    public async Task Board_issue_preview_preserves_original_ID_and_warns_about_unmapped_relations()
    {
        var token = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        using var response = await PostAsync("/api/workloads/boards/preview",
            new { sourceConnectionId = harness.SourceConnectionId,
                  projectId = "project", workItemIds = new[] { 101, 999 } }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = data.RootElement;
        Assert.False(root.GetProperty("readyToPublish").GetBoolean());
        var items = root.GetProperty("issues");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(101, items[0].GetProperty("sourceId").GetInt32());
        Assert.Contains("azdo-workitem:az-org:project:101", items[0].GetProperty("body").GetString());
        Assert.Contains("RelatedIssueUnmapped", items[0].GetProperty("issues").ToString());
        Assert.Equal(999, items[1].GetProperty("sourceId").GetInt32());
        Assert.Contains("WorkItemMissing", items[1].GetProperty("issues").ToString());
    }

    [Fact]
    public async Task Board_reconcile_uses_source_ID_mappings_and_proves_target_existence_not_link_recreation()
    {
        var token = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        using var response = await PostAsync("/api/workloads/boards/reconcile",
            new { sourceConnectionId = harness.SourceConnectionId,
                  destinationConnectionId = harness.DestinationConnectionId,
                  projectId = "project", targetRepository = "migration-target",
                  workItemIds = new[] { 101 },
                  knownGitHubIssues = new Dictionary<int, int> { [102] = 55 },
                  knownGitHubCommits = new Dictionary<string, string> { [ApiHarness.Sha] = ApiHarness.Sha },
                  knownGitHubPullRequests = new Dictionary<int, int> { [301] = 9 } }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("readyToPublish").GetBoolean());
        Assert.Equal(2, root.GetProperty("targetObjectsFound").GetInt32());
        Assert.Equal(1, root.GetProperty("unresolvedLinks").GetInt32());
        var links = root.GetProperty("links");
        Assert.Equal(3, links.GetArrayLength());
        Assert.Contains(links.EnumerateArray(), x => x.GetProperty("sourceRelation").GetString() ==
             "System.LinkTypes.Hierarchy-Forward" && x.GetProperty("status").GetString() == "targetExists");
        Assert.Contains(links.EnumerateArray(), x => x.GetProperty("sourceRelation").GetString() ==
             "commit" && x.GetProperty("status").GetString() == "targetExists");
        Assert.Contains(links.EnumerateArray(), x => x.GetProperty("sourceRelation").GetString() ==
             "pull-request" && x.GetProperty("status").GetString() == "notFound");
        Assert.Contains("CoverageNotGuaranteed", root.GetProperty("issues").ToString());
        Assert.All(harness.Calls, x => Assert.Equal("GET", x.Method));
    }

    [Fact]
    public async Task Invalid_work_item_batch_is_rejected_at_HTTP_boundary_without_external_requests()
    {
        var token = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var count = harness.Calls.Count;
        using var response = await PostAsync("/api/workloads/boards/preview",
            new { sourceConnectionId = harness.SourceConnectionId,
                  projectId = "project", workItemIds = new[] { 101, 101 } }, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidWorkItemIds", await response.Content.ReadAsStringAsync());
        Assert.Equal(count, harness.Calls.Count);
    }

    [Fact]
    public async Task Password_change_unlocks_admin_endpoints_only_with_a_new_JWT()
    {
        var name = "rotation-" + Guid.NewGuid().ToString("N");
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = name, MustChangePassword = true };
            var created = await manager.CreateAsync(user, "Temporary-Admin#2026");
            Assert.True(created.Succeeded);
            Assert.True((await manager.AddToRoleAsync(user, "admin")).Succeeded);
        }

        var pendingJwt = await harness.LoginAsync(name, "Temporary-Admin#2026");
        var input = new { sourceConnectionId = harness.SourceConnectionId,
                          projectId = "project", workItemIds = new[] { 101 } };
        using var denied = await PostAsync("/api/workloads/boards/preview", input, pendingJwt);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
        {
            Content = JsonContent.Create(new {
                currentPassword = "Temporary-Admin#2026",
                newPassword = "Rotated-Admin#2026"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pendingJwt);
        using var changed = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var response = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.False(response.RootElement.GetProperty("mustChangePassword").GetBoolean());
        var updatedJwt = response.RootElement.GetProperty("accessToken").GetString()!;

        using var stillDenied = await PostAsync("/api/workloads/boards/preview", input, pendingJwt);
        Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode);
        using var allowed = await PostAsync("/api/workloads/boards/preview", input, updatedJwt);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object payload, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrEmpty(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await harness.Client.SendAsync(request);
    }
}
