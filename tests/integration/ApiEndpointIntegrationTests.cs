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
using Microsoft.EntityFrameworkCore;

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
    public async Task Paged_F004_identity_sources_keep_RBAC_and_validate_mapping_across_pages()
    {
        var snapshotId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
                Id = snapshotId, ConnectionId = harness.SourceConnectionId,
                SourceProvider = "azure-devops-services", Organization = "az-org",
                CollectedAtUtc = now,
                Snapshot = new DiscoverySnapshot {
                    SnapshotId = snapshotId,
                    SourceSystem = SourceSystemKind.AzureDevOpsServices,
                    CollectedAt = now, Projects = [], AccessGrants = [],
                    Identities = [
                        new SourceIdentity("aad.alpha", "Alpha", null, "legacy.alpha"),
                        new SourceIdentity("aad.beta", "Beta", null, "legacy.beta"),
                        new SourceIdentity("aad.gamma", "Gamma", null, "legacy.gamma")
                    ]
                },
                Refs = [], Issues = [], Completeness = DiscoveryCompleteness.Partial,
                IdentitiesComplete = true, PermissionsComplete = false
            }, default);
        }

        var url = $"/api/identity-mappings/{snapshotId}/sources" +
            $"?destinationConnectionId={harness.DestinationConnectionId}&take=1";
        using var anonymous = await harness.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var userToken = await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword);
        using (var forbiddenRequest = new HttpRequestMessage(HttpMethod.Get, url))
        {
            forbiddenRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", userToken);
            using var denied = await harness.Client.SendAsync(forbiddenRequest);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var admin = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        using var page1Request = new HttpRequestMessage(HttpMethod.Get, url);
        page1Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var page1Reply = await harness.Client.SendAsync(page1Request);
        Assert.Equal(HttpStatusCode.OK, page1Reply.StatusCode);
        using var page1 = JsonDocument.Parse(await page1Reply.Content.ReadAsStringAsync());
        Assert.Equal("aad.alpha",
            page1.RootElement.GetProperty("items")[0].GetProperty("sourceIdentityId").GetString());
        Assert.Equal("aad.alpha", page1.RootElement.GetProperty("nextAfter").GetString());

        using var page2Request = new HttpRequestMessage(HttpMethod.Get,
            url + "&after=aad.alpha");
        page2Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var page2Reply = await harness.Client.SendAsync(page2Request);
        Assert.Equal(HttpStatusCode.OK, page2Reply.StatusCode);
        using var page2 = JsonDocument.Parse(await page2Reply.Content.ReadAsStringAsync());
        Assert.Equal("aad.beta",
            page2.RootElement.GetProperty("items")[0].GetProperty("sourceIdentityId").GetString());

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get,
            url.Replace("take=1", "take=101", StringComparison.Ordinal));
        invalidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var invalid = await harness.Client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var save = await PostAsync(
            $"/api/identity-mappings/{snapshotId}", new {
                destinationConnectionId = harness.DestinationConnectionId,
                expectedRevision = 0,
                mappings = new [] {
                    new { sourceIdentityId = "aad.alpha", gitHubLogin = "alpha-gh" },
                    new { sourceIdentityId = "aad.gamma", gitHubLogin = "gamma-gh" }
                }
            }, admin, HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var saved = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
        Assert.Equal(3, saved.RootElement.GetProperty("preview")
            .GetProperty("discoveredIdentities").GetInt32());
        Assert.Equal(2, saved.RootElement.GetProperty("preview")
            .GetProperty("mappedIdentities").GetInt32());
        Assert.False(saved.RootElement.GetProperty("preview")
            .GetProperty("readyForGrants").GetBoolean());
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
        using var assetsRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/staging/connections/{harness.SourceConnectionId}/assets/ref?take=1");
        assetsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
        using var assetsResponse = await harness.Client.SendAsync(assetsRequest);
        Assert.Equal(HttpStatusCode.OK, assetsResponse.StatusCode);
        using var assets = JsonDocument.Parse(await assetsResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, assets.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("abc", assets.RootElement.GetProperty("items")[0]
            .GetProperty("properties").GetProperty("ObjectId").GetString());

        using var badPage = new HttpRequestMessage(HttpMethod.Get,
            $"/api/staging/connections/{harness.SourceConnectionId}/assets/ref?take=1001");
        badPage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
        using var invalid = await harness.Client.SendAsync(badPage);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("InvalidStagingPage", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(before, harness.Calls.Count); // staging never talks to source or GitHub
    }

    [Fact]
    public async Task Durable_staging_job_HTTP_is_admin_only_idempotent_audited_and_worker_driven()
    {
        // The queue is global to the test PostgreSQL instance; isolate this
        // HTTP test from other worker tests' intentionally abandoned leases.
        await using (var reset = harness.Host.Services.CreateAsyncScope())
            await reset.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlRawAsync("TRUNCATE TABLE discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY");
        // The staging catalog is shared across API tests: use an isolated
        // logical source connection so exact asset count assertions are stable.
        var sourceId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDiscoveryRepository>().SaveAsync(
                new DiscoveryInventory
                {
                    Id = snapshotId, ConnectionId = sourceId,
                    SourceProvider = "azure-devops-services", Organization = "az-org",
                    CollectedAtUtc = now,
                    Snapshot = new DiscoverySnapshot
                    {
                        SnapshotId = snapshotId, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                        CollectedAt = now,
                        Projects = [new SourceProject("project", "Project", [
                            new SourceRepository("repo", "project", "Repo", VersionControlKind.Git)])],
                        Identities = [], AccessGrants = []
                    },
                    Refs = [new RepositoryRef("repo", "refs/heads/main", "sha")],
                    Issues = [], Completeness = DiscoveryCompleteness.Partial,
                    IdentitiesComplete = false, PermissionsComplete = false
                }, default);
        }

        var path = "/api/jobs/staging";
        using var anonymous = await PostAsync(path, new { snapshotId });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var user = await PostAsync(path, new { snapshotId },
            await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        using var pending = await PostAsync(path, new { snapshotId },
            await harness.LoginAsync(harness.PendingName, ApiHarness.PendingPassword));
        Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode);
        var admin = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var before = harness.Calls.Count;
        using var first = await PostAsync(path, new { snapshotId }, admin);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var id = firstJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Queued", firstJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(0, firstJson.RootElement.GetProperty("attempts").GetInt32());
        Assert.Equal($"/api/jobs/{id}", first.Headers.Location?.ToString());

        using var duplicate = await PostAsync(path, new { snapshotId }, admin);
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        using var duplicateJson = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(id, duplicateJson.RootElement.GetProperty("id").GetGuid());

        await using (var scope = harness.Host.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MigrationJobProcessor>()
                .RunOnceAsync(default));

        using var jobRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{id}");
        jobRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var job = await harness.Client.SendAsync(jobRequest);
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
        using var status = JsonDocument.Parse(await job.Content.ReadAsStringAsync());
        Assert.Equal("Succeeded", status.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, status.RootElement.GetProperty("attempts").GetInt32());
        Assert.Equal(3, status.RootElement.GetProperty("result").GetProperty("observed").GetInt32());

        using var eventsRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{id}/events");
        eventsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var eventsResponse = await harness.Client.SendAsync(eventsRequest);
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        using var events = JsonDocument.Parse(await eventsResponse.Content.ReadAsStringAsync());
        Assert.Equal(["Queued", "Claimed", "Succeeded"],
            events.RootElement.EnumerateArray().Select(x => x.GetProperty("eventType").GetString()));

        using var listRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/connections/{sourceId}");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var listing = await harness.Client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        using var list = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
        Assert.Contains(list.RootElement.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal(before, harness.Calls.Count); // no Azure DevOps/GitHub provider request
    }

    [Fact]
    public async Task Durable_discovery_job_reads_Azure_once_then_checkpoints_and_stages_immutable_snapshot()
    {
        var sourceId = Guid.NewGuid();
        var secretRef = "durable-discovery-" + sourceId.ToString("N");
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlRawAsync("TRUNCATE TABLE discovery_identity_observations, discovery_ref_chunks, discovery_ref_manifests, discovery_ref_observations, discovery_page_checkpoints, migration_job_events, migration_jobs RESTART IDENTITY");
            await scope.ServiceProvider.GetRequiredService<IConnectionSecretStore>()
                .SaveAsync(secretRef, ApiHarness.SourceToken, default);
            await scope.ServiceProvider.GetRequiredService<IConnectionRepository>().SaveAsync(
                new MigrationConnection(sourceId, "Async Azure DevOps", "azure-devops-services",
                    ConnectionDirection.Source, "az-org", secretRef,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Verified",
                    "operator", "read_only"), default);
        }

        var requestId = Guid.NewGuid();
        var payload = new { connectionId = sourceId, requestId };
        using var unauth = await PostAsync("/api/jobs/discovery", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        using var denied = await PostAsync("/api/jobs/discovery", payload,
            await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var invalid = await PostAsync("/api/jobs/discovery",
            new { connectionId = harness.DestinationConnectionId, requestId },
            await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("UnsupportedDiscoverySource", await invalid.Content.ReadAsStringAsync());

        var admin = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var countBefore = harness.Calls.Count;
        using var first = await PostAsync("/api/jobs/discovery", payload, admin);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var jobId = document.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("DiscoverSource", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal(Guid.Empty, document.RootElement.GetProperty("snapshotId").GetGuid());

        using var retry = await PostAsync("/api/jobs/discovery", payload, admin);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var repeated = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(jobId, repeated.RootElement.GetProperty("id").GetGuid());
        using var parallel = await PostAsync("/api/jobs/discovery",
            new { connectionId = sourceId, requestId = Guid.NewGuid() }, admin);
        Assert.Equal(HttpStatusCode.Accepted, parallel.StatusCode);
        using var second = JsonDocument.Parse(await parallel.Content.ReadAsStringAsync());
        Assert.Equal(jobId, second.RootElement.GetProperty("id").GetGuid());

        // A manifest may be prepared during a scan but must never be published
        // to callers before the durable discovery job reports success.
        using (var pendingManifest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/ref-manifest"))
        {
            pendingManifest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await harness.Client.SendAsync(pendingManifest);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        await using (var scope = harness.Host.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MigrationJobProcessor>()
                .RunOnceAsync(default));
        var providerCalls = harness.Calls.ToArray().Skip(countBefore).ToArray();
        Assert.NotEmpty(providerCalls);
        Assert.All(providerCalls, call =>
        {
            Assert.Equal("GET", call.Method);
            Assert.Contains(call.Url.Host, new[] { "dev.azure.com", "vssps.dev.azure.com" });
            Assert.DoesNotContain(ApiHarness.SourceToken, call.Url.ToString());
        });
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var completed = await harness.Client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var job = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        var status = job.RootElement;
        Assert.Equal("Succeeded", status.GetProperty("state").GetString());
        Assert.Equal(jobId, status.GetProperty("snapshotId").GetGuid());
        Assert.Equal(sourceId, status.GetProperty("sourceConnectionId").GetGuid());
        Assert.Equal(4, status.GetProperty("result").GetProperty("observed").GetInt32());

        using var snapshot = new HttpRequestMessage(HttpMethod.Get,
            $"/api/discoveries/{jobId}");
        snapshot.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var snapshotResponse = await harness.Client.SendAsync(snapshot);
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        using var persisted = JsonDocument.Parse(await snapshotResponse.Content.ReadAsStringAsync());
        Assert.Equal(jobId, persisted.RootElement.GetProperty("snapshot")
            .GetProperty("snapshotId").GetGuid());
        Assert.Equal(sourceId, persisted.RootElement.GetProperty("connectionId").GetGuid());
        Assert.True(persisted.RootElement.GetProperty("refsExternalized").GetBoolean());
        Assert.Equal(1, persisted.RootElement.GetProperty("refCount").GetInt32());
        Assert.Equal(0, persisted.RootElement.GetProperty("refs").GetArrayLength());
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var snapshotDb = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Discoveries.AsNoTracking().SingleAsync(x => x.Id == jobId);
            using var storedJson = JsonDocument.Parse(snapshotDb.Json);
            Assert.True(storedJson.RootElement.GetProperty("refsExternalized").GetBoolean());
            Assert.Equal(1, storedJson.RootElement.GetProperty("refCount").GetInt32());
            Assert.Empty(storedJson.RootElement.GetProperty("refs").EnumerateArray());
        }
        using var summariesGet = new HttpRequestMessage(HttpMethod.Get, "/api/discoveries");
        summariesGet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var summariesResponse = await harness.Client.SendAsync(summariesGet);
        Assert.Equal(HttpStatusCode.OK, summariesResponse.StatusCode);
        using var summaryList = JsonDocument.Parse(await summariesResponse.Content.ReadAsStringAsync());
        var currentSummary = summaryList.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == jobId);
        Assert.Equal(1, currentSummary.GetProperty("refCount").GetInt32());
        Assert.True(currentSummary.GetProperty("refsExternalized").GetBoolean());

        using var eventRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}/events");
        eventRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var eventResponse = await harness.Client.SendAsync(eventRequest);
        Assert.Equal(HttpStatusCode.OK, eventResponse.StatusCode);
        using var events = JsonDocument.Parse(await eventResponse.Content.ReadAsStringAsync());
        // All eight read-only GET sections are checkpointed before the final
        // snapshot: projects, capabilities, repositories, refs, users, groups,
        // parent ACL evidence and repository ACL evidence.
        Assert.Equal(new[] { "Queued", "Claimed" }
            .Concat(Enumerable.Repeat("PageSaved", 8))
            .Concat(["SnapshotSaved", "Succeeded"]),
            events.RootElement.EnumerateArray().Select(x => x.GetProperty("eventType").GetString()));
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pages = await db.DiscoveryPageCheckpoints.AsNoTracking()
                .Where(x => x.JobId == jobId).ToListAsync();
            Assert.Equal(8, pages.Count);
            Assert.Contains(pages, x => x.ResourceKey == "projects");
            Assert.Contains(pages, x => x.ResourceKey == "capabilities:project");
            Assert.Contains(pages, x => x.ResourceKey == "repos:project");
            Assert.Contains(pages, x => x.ResourceKey == "refs:source-git");
            Assert.Contains(pages, x => x.ResourceKey == "graph-users");
            Assert.Contains(pages, x => x.ResourceKey == "security-groups");
            Assert.Equal(2, pages.Count(x => x.ResourceKey.StartsWith("security-acl:", StringComparison.Ordinal)));
            Assert.All(pages, x => Assert.DoesNotContain(ApiHarness.SourceToken, x.SafePayloadJson));
        }

        using var progressRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/ref-progress");
        progressRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var progressReply = await harness.Client.SendAsync(progressRequest);
        Assert.Equal(HttpStatusCode.OK, progressReply.StatusCode);
        using var progress = JsonDocument.Parse(await progressReply.Content.ReadAsStringAsync());
        Assert.Equal(jobId, progress.RootElement.GetProperty("jobId").GetGuid());
        Assert.Equal(1, progress.RootElement.GetProperty("checkpointedRefPages").GetInt32());
        Assert.Equal(1, progress.RootElement.GetProperty("observedRefs").GetInt32());

        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var streamed = await db.DiscoveryRefObservations.AsNoTracking()
                .SingleAsync(x => x.JobId == jobId);
            var committed = await db.StagingAssets.AsNoTracking()
                .SingleAsync(x => x.SourceConnectionId == sourceId && x.AssetType == "ref");
            Assert.Equal(committed.SourceId, streamed.SourceId);
            Assert.Equal(committed.Fingerprint, streamed.Fingerprint);
        }
        using var progressUnauthenticated = await harness.Client.GetAsync(
            $"/api/jobs/{jobId}/ref-progress");
        Assert.Equal(HttpStatusCode.Unauthorized, progressUnauthenticated.StatusCode);

        using var identityProgressRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/identity-progress");
        identityProgressRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", admin);
        using var identityProgressReply = await harness.Client.SendAsync(identityProgressRequest);
        Assert.Equal(HttpStatusCode.OK, identityProgressReply.StatusCode);
        using var identityProgress = JsonDocument.Parse(
            await identityProgressReply.Content.ReadAsStringAsync());
        Assert.Equal(1, identityProgress.RootElement.GetProperty("checkpointedUserPages").GetInt32());
        Assert.Equal(1, identityProgress.RootElement.GetProperty("observedUsers").GetInt32());

        using var identityListRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/identities?take=1");
        identityListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var identityListReply = await harness.Client.SendAsync(identityListRequest);
        Assert.Equal(HttpStatusCode.OK, identityListReply.StatusCode);
        using var identityList = JsonDocument.Parse(await identityListReply.Content.ReadAsStringAsync());
        Assert.Equal(jobId, identityList.RootElement.GetProperty("jobId").GetGuid());
        var identityRow = Assert.Single(identityList.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("aad.test", identityRow.GetProperty("sourceId").GetString());
        Assert.Equal("Test User",
            identityRow.GetProperty("properties").GetProperty("DisplayName").GetString());
        Assert.Equal("test@example.invalid",
            identityRow.GetProperty("properties").GetProperty("Email").GetString());
        Assert.Equal(64, identityRow.GetProperty("fingerprint").GetString()!.Length);
        Assert.Equal(JsonValueKind.Null,
            identityList.RootElement.GetProperty("nextAfter").ValueKind);
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var observedIdentity = await db.DiscoveryIdentityObservations.AsNoTracking()
                .SingleAsync(x => x.JobId == jobId);
            var canonicalIdentity = await db.StagingAssets.AsNoTracking()
                .SingleAsync(x => x.SourceConnectionId == sourceId &&
                    x.AssetType == "identity" && x.SourceId == "aad.test");
            Assert.Equal(canonicalIdentity.Fingerprint, observedIdentity.Fingerprint);
        }
        using var rejectedIdentityPage = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/identities?take=101");
        rejectedIdentityPage.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", admin);
        using var rejectedIdentityPageReply = await harness.Client.SendAsync(rejectedIdentityPage);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedIdentityPageReply.StatusCode);
        using var unauthenticatedIdentities = await harness.Client.GetAsync(
            $"/api/jobs/{jobId}/identities");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedIdentities.StatusCode);

        using var manifestGet = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/ref-manifest");
        manifestGet.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var manifestResponse = await harness.Client.SendAsync(manifestGet);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
        Assert.Equal(jobId, manifest.RootElement.GetProperty("jobId").GetGuid());
        Assert.Equal(1, manifest.RootElement.GetProperty("assetCount").GetInt32());
        Assert.Equal(1, manifest.RootElement.GetProperty("chunkCount").GetInt32());
        Assert.Equal(64, manifest.RootElement.GetProperty("rootSha256").GetString()!.Length);
        using var chunksRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/ref-manifest/chunks?take=1");
        chunksRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var chunksResponse = await harness.Client.SendAsync(chunksRequest);
        Assert.Equal(HttpStatusCode.OK, chunksResponse.StatusCode);
        using var chunkDescriptors = JsonDocument.Parse(await chunksResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, chunkDescriptors.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, chunkDescriptors.RootElement.GetProperty("nextAfter").ValueKind);

        using var chunkRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/ref-manifest/chunks/0");
        chunkRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var chunkResponse = await harness.Client.SendAsync(chunkRequest);
        Assert.Equal(HttpStatusCode.OK, chunkResponse.StatusCode);
        using var loadedChunk = JsonDocument.Parse(await chunkResponse.Content.ReadAsStringAsync());
        var firstItem = loadedChunk.RootElement.GetProperty("items")[0];
        Assert.Equal(64, firstItem.GetProperty("fingerprint").GetString()!.Length);
        Assert.Equal(new string('a', 40),
            firstItem.GetProperty("properties").GetProperty("ObjectId").GetString());
        using var deniedManifest = await harness.Client.GetAsync(
            $"/api/jobs/{jobId}/ref-manifest");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedManifest.StatusCode);

        using var invalidChunksRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/jobs/{jobId}/ref-manifest/chunks?take=999");
        invalidChunksRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var invalidChunksResponse = await harness.Client.SendAsync(invalidChunksRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidChunksResponse.StatusCode);

        var providerReadCount = harness.Calls.Count;
        using var replay = await PostAsync("/api/jobs/discovery", payload, admin);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        using var original = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(jobId, original.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", original.RootElement.GetProperty("state").GetString());
        Assert.Equal(providerReadCount, harness.Calls.Count);
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
