using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// ASP.NET, JWT, PostgreSQL, encrypted PAT store and actual GitHub GET adapters.
// Only provider HTTP responses are stubbed by the existing strict GET-only
// ApiHarness handler (fixed api.github.com, no redirection or writes).
public sealed class ApiLiveTargetPreflightTests(ApiHarness harness)
    : IClassFixture<ApiHarness>
{
    [Fact]
    public async Task F028_GET_only_preflight_is_review_gated_RBAC_scoped_and_never_executable()
    {
        var snapshotId = Guid.NewGuid();
        var source = harness.SourceConnectionId;
        var target = harness.DestinationConnectionId;
        var now = DateTimeOffset.UtcNow;
        Guid prepId;
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshot = new DiscoveryInventory {
                Id = snapshotId, ConnectionId = source,
                SourceProvider = "azure-devops-services", Organization = "az-org",
                CollectedAtUtc = now.AddMinutes(-4),
                Snapshot = new DiscoverySnapshot {
                    SnapshotId = snapshotId,
                    SourceSystem = SourceSystemKind.AzureDevOpsServices,
                    CollectedAt = now.AddMinutes(-4),
                    Projects = [new SourceProject("project", "Project", [
                        new SourceRepository("repo-1", "project", "One",
                            VersionControlKind.Git),
                        new SourceRepository("repo-2", "project", "Two",
                            VersionControlKind.Git)])],
                    Identities = [], AccessGrants = []
                },
                Refs = [new RepositoryRef("repo-1", "refs/heads/main", "abc")],
                Issues = [], Completeness = DiscoveryCompleteness.Partial,
                IdentitiesComplete = false, PermissionsComplete = false
            };
            await new PostgresDiscoveryRepository(db).SaveAsync(snapshot, default);
            var normalized = StagingAssetNormalizer.Normalize(snapshot);
            await new PostgresStagingRepository(db, TimeProvider.System)
                .ApplyAsync(snapshot, normalized.Assets, normalized.CompleteTypes, default);
            var queue = new PostgresIncrementalPreparationRepository(db, TimeProvider.System);
            var created = await queue.EnqueueAsync(snapshotId, source, target,
                Guid.NewGuid(), default);
            prepId = created.Id;
            var lease = await queue.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5),
                default, source);
            Assert.NotNull(lease);
            Assert.True(await queue.PrepareAsync(lease, default));
        }
        var admin = await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword);
        var user = await harness.LoginAsync(harness.UserName, ApiHarness.UserPassword);
        var path = $"/api/incremental-preparations/{prepId}/target-preflight";
        var payload = new {
            expectedReviewSha256 = new string('a', 64),
            repositories = new[] {
                new { sourceRepositoryId = "repo-1",
                    targetRepositoryName = "occupied", strategy = 0, include = true },
                new { sourceRepositoryId = "repo-2",
                    targetRepositoryName = "hidden", strategy = 0, include = true }
            }
        };
        var callsBefore = harness.Calls.Count;
        using (var anonymous = await harness.Client.PostAsJsonAsync(path, payload))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var forbidden = await PostAsAsync(path, payload, user))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var noReview = await PostAsAsync(path, payload, admin))
        {
            Assert.Equal(HttpStatusCode.Conflict, noReview.StatusCode);
            Assert.Contains("PlanningReviewRequired",
                await noReview.Content.ReadAsStringAsync());
        }
        Assert.Equal(callsBefore, harness.Calls.Count);

        string hash;
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var review = new PostgresIncrementalReviewRepository(db, TimeProvider.System);
            var proof = await review.DryRunAsync(prepId, Guid.NewGuid(), default);
            var approved = await review.DecideAsync(prepId, Guid.NewGuid(),
                "ApproveForPlanning", proof.DryRunSha256, false, null, default);
            Assert.True(approved.IsCurrent);
            hash = approved.DryRunSha256;
        }

        var approvedRequest = new {
            expectedReviewSha256 = hash,
            repositories = payload.repositories
        };
        using var checkedRequest = await PostAsAsync(path, approvedRequest, admin);
        Assert.Equal(HttpStatusCode.OK, checkedRequest.StatusCode);
        var responseJson = await checkedRequest.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(responseJson);
        var root = json.RootElement;
        Assert.Equal("Blocked", root.GetProperty("state").GetString());
        Assert.Equal(2, root.GetProperty("checkedRepositories").GetInt32());
        Assert.Equal(1, root.GetProperty("visibleCollisions").GetInt32());
        Assert.Equal(1, root.GetProperty("uncertainRepositories").GetInt32());
        Assert.True(root.GetProperty("membershipVerified").GetBoolean());
        Assert.False(root.GetProperty("repositoryCreatePermissionVerified").GetBoolean());
        Assert.False(root.GetProperty("targetAvailabilityVerified").GetBoolean());
        Assert.False(root.GetProperty("readyForExecution").GetBoolean());
        Assert.True(root.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(["Exists", "NotVisibleOrMissing"],
            root.GetProperty("repositories").EnumerateArray()
                .Select(x => x.GetProperty("status").GetString()).ToArray());

        var calls = harness.Calls.ToArray().Skip(callsBefore).ToArray();
        Assert.Equal(4, calls.Length); // membership + organization + two repo GETs
        Assert.All(calls, call => {
            Assert.Equal("GET", call.Method);
            Assert.Equal("api.github.com", call.Url.Host);
            Assert.Equal("Bearer", call.AuthScheme);
        });
        Assert.Contains(calls, x => x.Url.AbsolutePath == "/repos/gh-org/occupied");
        Assert.Contains(calls, x => x.Url.AbsolutePath == "/repos/gh-org/hidden");
        Assert.DoesNotContain(ApiHarness.TargetToken, responseJson);
        Assert.DoesNotContain(ApiHarness.SourceToken, responseJson);
        Assert.DoesNotContain("secret", responseJson, StringComparison.OrdinalIgnoreCase);

        using (var get = new HttpRequestMessage(HttpMethod.Get, path))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var stored = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.Equal(root.GetProperty("evidenceSha256").GetString(),
                stored.RootElement.GetProperty("evidenceSha256").GetString());
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get, path + "/events"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var eventsJson = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.Single(eventsJson.RootElement.EnumerateArray());
        }
        Assert.Equal(callsBefore + 4, harness.Calls.Count);

        // F-029: immutable blocked operation manifest is constructed entirely
        // from the saved F-025–F-028 proofs. It never calls any provider.
        var operationalPath = $"/api/incremental-preparations/{prepId}/operation-plans";
        var sourceProof = root.GetProperty("evidenceSha256").GetString()!;
        using var invalidPlan = await PostAsAsync(operationalPath,
            new { expectedPreflightSha256 = new string('a', 64) }, admin);
        Assert.Equal(HttpStatusCode.Conflict, invalidPlan.StatusCode);

        using var planResponse = await PostAsAsync(operationalPath,
            new { expectedPreflightSha256 = sourceProof }, admin);
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        using var planJson = JsonDocument.Parse(
            await planResponse.Content.ReadAsStringAsync());
        var manifest = planJson.RootElement;
        var planId = manifest.GetProperty("id").GetGuid();
        var planSha = manifest.GetProperty("manifestSha256").GetString()!;
        Assert.Equal(64, planSha.Length);
        Assert.Equal(2, manifest.GetProperty("repositoryCount").GetInt32());
        Assert.Equal(12, manifest.GetProperty("stepCount").GetInt32());
        Assert.Equal(1, manifest.GetProperty("changedRefCount").GetInt32());
        Assert.True(manifest.GetProperty("isCurrent").GetBoolean());
        Assert.False(manifest.GetProperty("readyForExecution").GetBoolean());
        Assert.All(manifest.GetProperty("steps").EnumerateArray(),
            s => Assert.Equal("Blocked", s.GetProperty("state").GetString()));

        using var replayPlan = await PostAsAsync(operationalPath,
            new { expectedPreflightSha256 = sourceProof }, admin);
        Assert.Equal(HttpStatusCode.OK, replayPlan.StatusCode);
        using var replayJson = JsonDocument.Parse(
            await replayPlan.Content.ReadAsStringAsync());
        Assert.Equal(planId, replayJson.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(callsBefore + 4, harness.Calls.Count);

        var authorizePath = $"{operationalPath}/{planId}/execution-authorization";
        using var nonSuperadmin = await PostAsAsync(authorizePath,
            new { expectedManifestSha256 = planSha,
                acknowledgeBlockedOperations = true }, admin);
        Assert.Equal(HttpStatusCode.Forbidden, nonSuperadmin.StatusCode);
        using var notLogged = await harness.Client.PostAsJsonAsync(authorizePath,
            new { expectedManifestSha256 = planSha,
                acknowledgeBlockedOperations = true });
        Assert.Equal(HttpStatusCode.Unauthorized, notLogged.StatusCode);

        var superUserName = "f029-super-" + Guid.NewGuid().ToString("N");
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var userInfo = new ApplicationUser {
                UserName = superUserName, MustChangePassword = false
            };
            Assert.True((await users.CreateAsync(userInfo, ApiHarness.AdminPassword)).Succeeded);
            Assert.True((await users.AddToRoleAsync(userInfo, "superadmin")).Succeeded);
        }
        var superToken = await harness.LoginAsync(superUserName, ApiHarness.AdminPassword);
        using var denied = await PostAsAsync(authorizePath,
            new { expectedManifestSha256 = planSha,
                acknowledgeBlockedOperations = true }, superToken);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        using var deniedJson = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("Denied", deniedJson.RootElement.GetProperty("decision").GetString());
        Assert.Equal("DestinationWritePermissionUnverified",
            deniedJson.RootElement.GetProperty("reasonCode").GetString());
        using (var get = new HttpRequestMessage(HttpMethod.Get,
            $"{operationalPath}/{planId}/authorization-attempts"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", superToken);
            using var response = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var audit = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Single(audit.RootElement.EnumerateArray());
        }
        Assert.Equal(callsBefore + 4, harness.Calls.Count);

        // F-031: read-only GitHub Git refs are bound to this manifest and
        // persisted immutably. Neither a user nor an invalid digest can GET
        // the target provider or emit an observation.
        var evidencePath = $"{operationalPath}/{planId}/target-ref-evidence";
        var evidenceRequest = new {
            expectedManifestSha256 = planSha, sourceRepositoryId = "repo-1"
        };
        using (var anonymousEvidence = await harness.Client.PostAsJsonAsync(
            evidencePath, evidenceRequest))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousEvidence.StatusCode);
        using (var userEvidence = await PostAsAsync(
            evidencePath, evidenceRequest, user))
            Assert.Equal(HttpStatusCode.Forbidden, userEvidence.StatusCode);
        using (var invalidDigest = await PostAsAsync(evidencePath,
            new { expectedManifestSha256 = new string('a', 64),
                sourceRepositoryId = "repo-1" }, admin))
            Assert.Equal(HttpStatusCode.Conflict, invalidDigest.StatusCode);
        Assert.Equal(callsBefore + 4, harness.Calls.Count);

        Guid observationId;
        using (var collected = await PostAsAsync(evidencePath, evidenceRequest, admin))
        {
            Assert.Equal(HttpStatusCode.OK, collected.StatusCode);
            var body = await collected.Content.ReadAsStringAsync();
            Assert.DoesNotContain(ApiHarness.TargetToken, body);
            Assert.DoesNotContain(ApiHarness.SourceToken, body);
            using var evidenceJson = JsonDocument.Parse(body);
            Assert.Equal(2, evidenceJson.RootElement.GetProperty("refCount").GetInt32());
            Assert.True(evidenceJson.RootElement.GetProperty("isCurrent").GetBoolean());
            Assert.False(evidenceJson.RootElement.GetProperty("readyForExecution").GetBoolean());
            Assert.Equal(64, evidenceJson.RootElement.GetProperty(
                "evidenceSha256").GetString()!.Length);
            observationId = evidenceJson.RootElement.GetProperty("id").GetGuid();
        }
        var providerCalls = harness.Calls.ToArray().Skip(callsBefore + 4).ToArray();
        Assert.Equal(2, providerCalls.Length);
        Assert.Equal(new[] {
            "/repos/gh-org/occupied/git/matching-refs/heads",
            "/repos/gh-org/occupied/git/matching-refs/tags"
        }, providerCalls.Select(x => x.Url.AbsolutePath).ToArray());
        Assert.All(providerCalls, call => {
            Assert.Equal("GET", call.Method);
            Assert.Equal("api.github.com", call.Url.Host);
        });

        using (var get = new HttpRequestMessage(HttpMethod.Get, evidencePath))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var saved = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Single(saved.RootElement.EnumerateArray());
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get,
            evidencePath + "/" + observationId + "?take=1"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var saved = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Single(saved.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("refs/heads/main",
                saved.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
            Assert.Equal("refs/heads/main",
                saved.RootElement.GetProperty("nextAfter").GetString());
            Assert.False(saved.RootElement.GetProperty("evidence")
                .GetProperty("readyForExecution").GetBoolean());
        }
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.GitHubTargetRefEvidence.AsNoTracking()
                .CountAsync(x => x.PlanId == planId));
        }
        Assert.Equal(callsBefore + 6, harness.Calls.Count);

        // A connection re-validation invalidates even previously persisted GET
        // observations. Reading the old record makes ZERO additional API calls.
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Connections.Where(x => x.Id == target)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ValidatedAtUtc,
                    DateTimeOffset.UtcNow.AddSeconds(25)));
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get, path))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var stale = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.False(stale.RootElement.GetProperty("isCurrent").GetBoolean());
        }
        Assert.Equal(callsBefore + 6, harness.Calls.Count);
        using (var get = new HttpRequestMessage(HttpMethod.Get,
            $"{operationalPath}/{planId}"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var stale = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.False(stale.RootElement.GetProperty("isCurrent").GetBoolean());
            Assert.False(stale.RootElement.GetProperty("readyForExecution").GetBoolean());
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get, evidencePath))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var stale = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.False(stale.RootElement[0].GetProperty("isCurrent").GetBoolean());
        }
        Assert.Equal(callsBefore + 6, harness.Calls.Count);
    }

    private async Task<HttpResponseMessage> PostAsAsync(
        string path, object payload, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await harness.Client.SendAsync(request);
    }
}
