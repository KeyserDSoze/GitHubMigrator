using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using GitHubMigrator.Infrastructure.PostgreSql;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
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

    [Fact]
    public async Task Plan_draft_HTTP_requires_admin_and_returns_409_for_stale_revision()
    {
        var snapshotId = Guid.NewGuid();
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var discoveries = scope.ServiceProvider.GetRequiredService<IDiscoveryRepository>();
            await discoveries.SaveAsync(new DiscoveryInventory
            {
                Id = snapshotId, ConnectionId = harness.SourceConnectionId,
                SourceProvider = "azure-devops-services", Organization = "az-org",
                CollectedAtUtc = DateTimeOffset.UtcNow,
                Snapshot = new DiscoverySnapshot
                {
                    SnapshotId = snapshotId, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                    CollectedAt = DateTimeOffset.UtcNow,
                    Projects = [new SourceProject("proj-1", "Project", [
                        new SourceRepository("repo-1", "proj-1", "Api", VersionControlKind.Git)])],
                    Identities = [], AccessGrants = [], PermissionsComplete = false
                },
                Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
                IdentitiesComplete = false, PermissionsComplete = false
            }, default);
        }
        var url = $"/api/migration-plans/drafts/{snapshotId}";
        var payload = new
        {
            destinationConnectionId = harness.DestinationConnectionId,
            expectedRevision = 0,
            repositories = new[] { new {
                sourceRepositoryId = "repo-1", targetRepositoryName = "renamed",
                strategy = 0, include = true
            }}
        };
        using (var anonymous = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(payload) })
        using (var response = await harness.Client.SendAsync(anonymous))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var userJwt = await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword);
        using (var denied = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(payload) })
        {
            denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userJwt);
            using var response = await harness.Client.SendAsync(denied);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        var jwt = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var before = harness.Calls.Count;
        using (var write = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(payload) })
        {
            write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var response = await harness.Client.SendAsync(write);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, doc.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal("renamed", doc.RootElement.GetProperty("repositories")[0]
                .GetProperty("targetRepositoryName").GetString());
        }
        using (var stale = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(payload) })
        {
            stale.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var response = await harness.Client.SendAsync(stale);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("PlanRevisionConflict", await response.Content.ReadAsStringAsync());
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get,
            url + "?destinationConnectionId=" + harness.DestinationConnectionId))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var response = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, doc.RootElement.GetProperty("revision").GetInt32());
        }
        Assert.Equal(before, harness.Calls.Count); // draft persistence must not call external providers
    }

    [Fact]
    public async Task GitHub_target_preflight_detects_visible_collision_but_never_claims_404_name_available()
    {
        var snapshotId = Guid.NewGuid();
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IDiscoveryRepository>();
            await reader.SaveAsync(new DiscoveryInventory
            {
                Id = snapshotId, ConnectionId = harness.SourceConnectionId,
                SourceProvider = "azure-devops-services", Organization = "az-org",
                CollectedAtUtc = DateTimeOffset.UtcNow,
                Snapshot = new DiscoverySnapshot
                {
                    SnapshotId = snapshotId, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                    CollectedAt = DateTimeOffset.UtcNow,
                    Projects = [new SourceProject("proj", "Project", [
                        new SourceRepository("first", "proj", "First", VersionControlKind.Git),
                        new SourceRepository("second", "proj", "Second", VersionControlKind.Git)])],
                    Identities = [], AccessGrants = [], PermissionsComplete = false
                },
                Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
                IdentitiesComplete = false, PermissionsComplete = false
            }, default);
        }
        var payload = new
        {
            snapshotId, destinationConnectionId = harness.DestinationConnectionId,
            repositories = new[]
            {
                new { sourceRepositoryId = "first", targetRepositoryName = "occupied", strategy = 0, include = true },
                new { sourceRepositoryId = "second", targetRepositoryName = "hidden", strategy = 0, include = true }
            }
        };
        var url = "/api/migration-plans/preflight";
        using var anonymous = await PostAsync(url, payload);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var user = await PostAsync(url, payload,
            await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        var before = harness.Calls.Count;
        var adminJwt = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        using var response = await PostAsync(url, payload, adminJwt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("targetAvailabilityVerified").GetBoolean());
        Assert.False(root.GetProperty("readyForApproval").GetBoolean());
        Assert.Equal(2, root.GetProperty("checkedCount").GetInt32());
        Assert.Equal("Exists", root.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert.Equal("NotVisibleOrMissing", root.GetProperty("repositories")[1].GetProperty("status").GetString());
        var newCalls = harness.Calls.ToArray().Skip(before).ToArray();
        Assert.Equal(2, newCalls.Length);
        Assert.All(newCalls, x =>
        {
            Assert.Equal("GET", x.Method);
            Assert.Equal("api.github.com", x.Url.Host);
            Assert.DoesNotContain(ApiHarness.TargetToken, x.Url.ToString());
        });
    }

    [Fact]
    public async Task GitHub_destination_access_requires_admin_and_never_claims_token_create_permission()
    {
        var url = $"/api/migration-plans/destinations/{harness.DestinationConnectionId}/access";
        using var anonymous = await harness.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var userToken = await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword);
        using (var denied = new HttpRequestMessage(HttpMethod.Get, url))
        {
            denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            using var response = await harness.Client.SendAsync(denied);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        var adminToken = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var before = harness.Calls.Count;
        using var authorized = new HttpRequestMessage(HttpMethod.Get, url);
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var accepted = await harness.Client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var doc = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var result = doc.RootElement;
        Assert.Equal("gh-org", result.GetProperty("targetOrganization").GetString());
        Assert.Equal("test", result.GetProperty("authenticatedAs").GetString());
        Assert.True(result.GetProperty("membershipVerified").GetBoolean());
        Assert.Equal("Active", result.GetProperty("membershipStatus").GetString());
        Assert.Equal("Owner", result.GetProperty("membershipRole").GetString());
        Assert.Equal("AllowedForMembers", result.GetProperty("privateRepositoryPolicy").GetString());
        Assert.False(result.GetProperty("repositoryCreatePermissionVerified").GetBoolean());
        Assert.False(result.GetProperty("readyForApproval").GetBoolean());
        var calls = harness.Calls.ToArray().Skip(before).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("/user/memberships/orgs/gh-org", calls[0].Url.AbsolutePath);
        Assert.Equal("/orgs/gh-org", calls[1].Url.AbsolutePath);
        Assert.All(calls, call =>
        {
            Assert.Equal("GET", call.Method);
            Assert.Equal("api.github.com", call.Url.Host);
            Assert.DoesNotContain(ApiHarness.TargetToken, call.Url.ToString());
        });
    }

    [Fact]
    public async Task Staging_HTTP_contract_supports_admin_sync_replay_and_read_only_summary()
    {
        var snapshotId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var discovery = scope.ServiceProvider.GetRequiredService<IDiscoveryRepository>();
            await discovery.SaveAsync(new DiscoveryInventory
            {
                Id = snapshotId, ConnectionId = harness.SourceConnectionId,
                SourceProvider = "azure-devops-services", Organization = "az-org",
                CollectedAtUtc = now,
                Snapshot = new DiscoverySnapshot
                {
                    SnapshotId = snapshotId, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                    CollectedAt = now,
                    Projects = [new SourceProject("project-1", "Project", [
                        new SourceRepository("repo-1", "project-1", "API", VersionControlKind.Git)])],
                    Identities = [], AccessGrants = []
                },
                Refs = [new RepositoryRef("repo-1", "refs/heads/main", "abc")],
                Issues = [], Completeness = DiscoveryCompleteness.Partial,
                IdentitiesComplete = false, PermissionsComplete = false
            }, default);
        }

        var path = $"/api/staging/snapshots/{snapshotId}/sync";
        using var anonymous = await PostAsync(path, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var ordinaryJwt = await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword);
        using var forbidden = await PostAsync(path, new { }, ordinaryJwt);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var adminJwt = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var before = harness.Calls.Count;
        using var created = await PostAsync(path, new { }, adminJwt);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(3, doc.RootElement.GetProperty("observed").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("markedMissing").GetInt32());
        Assert.False(doc.RootElement.GetProperty("sourceInventoryComplete").GetBoolean());
        using var replay = await PostAsync(path, new { }, adminJwt);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());

        using var get = new HttpRequestMessage(HttpMethod.Get,
            $"/api/staging/snapshots/{snapshotId}");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
        using var viewed = await harness.Client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, viewed.StatusCode);
        using var summaryRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/staging/connections/{harness.SourceConnectionId}/summary");
        summaryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
        using var summaryResponse = await harness.Client.SendAsync(summaryRequest);
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, summary.RootElement.GetProperty("activeAssets").GetInt32());
        Assert.Equal(snapshotId.ToString(), summary.RootElement.GetProperty("latestSnapshotId").GetString());
        Assert.Equal(before, harness.Calls.Count); // staging never talks to source or GitHub
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
