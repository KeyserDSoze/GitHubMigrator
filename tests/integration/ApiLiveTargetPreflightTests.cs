using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Npgsql;
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
                Refs = [new RepositoryRef("repo-1", "refs/heads/main", ApiHarness.Sha)],
                Issues = [], Completeness = DiscoveryCompleteness.Complete,
                IdentitiesComplete = true, PermissionsComplete = true,
                SecurityCoverage = new DiscoveryCoverage(true, true, true, true)
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
        string targetRefSha;
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
            targetRefSha = evidenceJson.RootElement.GetProperty("evidenceSha256").GetString()!;
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

        // F-032 combines the historical, *complete* source snapshot with
        // persisted F-031 target evidence. The endpoint issues no GitHub
        // or Azure DevOps requests, stores no provider credentials and does
        // not turn ref equality into execution permission.
        var diffPath = $"{operationalPath}/{planId}/ref-reconciliations";
        var diffRequest = new {
            expectedManifestSha256 = planSha,
            sourceRepositoryId = "repo-1",
            targetObservationId = observationId,
            expectedTargetEvidenceSha256 = targetRefSha
        };
        using (var noAuth = await harness.Client.PostAsJsonAsync(diffPath, diffRequest))
            Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        using (var lowRole = await PostAsAsync(diffPath, diffRequest, user))
            Assert.Equal(HttpStatusCode.Forbidden, lowRole.StatusCode);
        using (var wrongDigest = await PostAsAsync(diffPath, new {
            expectedManifestSha256 = planSha,
            sourceRepositoryId = "repo-1",
            targetObservationId = observationId,
            expectedTargetEvidenceSha256 = new string('f', 64)
        }, admin))
            Assert.Equal(HttpStatusCode.Conflict, wrongDigest.StatusCode);
        using (var wrongMapping = await PostAsAsync(diffPath, new {
            expectedManifestSha256 = planSha,
            sourceRepositoryId = "does-not-exist",
            targetObservationId = observationId,
            expectedTargetEvidenceSha256 = targetRefSha
        }, admin))
            Assert.Equal(HttpStatusCode.Conflict, wrongMapping.StatusCode);
        Assert.Equal(callsBefore + 6, harness.Calls.Count);

        Guid diffId;
        string reconciliationSha;
        using (var compared = await PostAsAsync(diffPath, diffRequest, admin))
        {
            Assert.Equal(HttpStatusCode.OK, compared.StatusCode);
            var body = await compared.Content.ReadAsStringAsync();
            Assert.DoesNotContain(ApiHarness.SourceToken, body);
            Assert.DoesNotContain(ApiHarness.TargetToken, body);
            using var comparedJson = JsonDocument.Parse(body);
            var result = comparedJson.RootElement;
            diffId = result.GetProperty("id").GetGuid();
            reconciliationSha = result.GetProperty("reconciliationSha256").GetString()!;
            Assert.Equal(64, reconciliationSha.Length);
            Assert.Equal(1, result.GetProperty("sourceRefCount").GetInt32());
            Assert.Equal(2, result.GetProperty("targetRefCount").GetInt32());
            Assert.Equal(1, result.GetProperty("matchingCount").GetInt32());
            Assert.Equal(0, result.GetProperty("createCandidateCount").GetInt32());
            Assert.Equal(0, result.GetProperty("divergedCount").GetInt32());
            Assert.Equal(1, result.GetProperty("targetOnlyCount").GetInt32());
            Assert.True(result.GetProperty("isCurrent").GetBoolean());
            Assert.False(result.GetProperty("readyForExecution").GetBoolean());
            Assert.Equal(JsonValueKind.Null,
                result.GetProperty("sourceManifestRootSha256").ValueKind);
        }

        using (var replay = await PostAsAsync(diffPath, diffRequest, admin))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(diffId, body.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(reconciliationSha,
                body.RootElement.GetProperty("reconciliationSha256").GetString());
        }
        // Separate dependency-injection scopes simulate independent API
        // replicas replaying the same current digest under the DB lock.
        await using (var replicaOne = harness.Host.Services.CreateAsyncScope())
        await using (var replicaTwo = harness.Host.Services.CreateAsyncScope())
        {
            var one = replicaOne.ServiceProvider.GetRequiredService<
                IncrementalGitRefReconciliationService>();
            var two = replicaTwo.ServiceProvider.GetRequiredService<
                IncrementalGitRefReconciliationService>();
            var results = await Task.WhenAll(
                one.CreateAsync(prepId, planId, "repo-1", observationId,
                    planSha, targetRefSha, Guid.NewGuid(), default),
                two.CreateAsync(prepId, planId, "repo-1", observationId,
                    planSha, targetRefSha, Guid.NewGuid(), default));
            Assert.Equal(diffId, results[0].Id);
            Assert.Equal(diffId, results[1].Id);
            Assert.Equal(reconciliationSha, results[0].ReconciliationSha256);
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get, diffPath))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(body.RootElement.EnumerateArray());
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get,
            diffPath + "/" + diffId + "?take=1"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("refs/heads/main",
                body.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
            Assert.Equal("refs/heads/main",
                body.RootElement.GetProperty("nextAfter").GetString());
            Assert.Equal((int)GitMirrorRefDisposition.MatchingObjectId,
                body.RootElement.GetProperty("items")[0]
                    .GetProperty("disposition").GetInt32());
            Assert.False(body.RootElement.GetProperty("reconciliation")
                .GetProperty("readyForExecution").GetBoolean());
        }
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.IncrementalGitRefReconciliations.AsNoTracking()
                .CountAsync(x => x.PlanId == planId));
        }
        Assert.Equal(callsBefore + 6, harness.Calls.Count);

        // F-034: durable read-only transfer preparation is bound to this
        // exact current F-032 digest. It cannot create an executable task.
        var workPath = $"{diffPath}/{diffId}/transfer-workpacks";
        var workRequest = new {
            expectedManifestSha256 = planSha,
            expectedReconciliationSha256 = reconciliationSha
        };
        using (var unauth = await harness.Client.PostAsJsonAsync(workPath, workRequest))
            Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        using (var noAdmin = await PostAsAsync(workPath, workRequest, user))
            Assert.Equal(HttpStatusCode.Forbidden, noAdmin.StatusCode);
        using (var forged = await PostAsAsync(workPath, new {
            expectedManifestSha256 = planSha,
            expectedReconciliationSha256 = new string('e', 64)
        }, admin))
            Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);

        Guid workId;
        using (var accepted = await PostAsAsync(workPath, workRequest, admin))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var body = await accepted.Content.ReadAsStringAsync();
            Assert.DoesNotContain(ApiHarness.SourceToken, body);
            Assert.DoesNotContain(ApiHarness.TargetToken, body);
            using var workJson = JsonDocument.Parse(body);
            workId = workJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Queued", workJson.RootElement.GetProperty("state").GetString());
            Assert.Equal("EvidencePending", workJson.RootElement.GetProperty("phase").GetString());
            Assert.False(workJson.RootElement.GetProperty("readyForExecution").GetBoolean());
        }
        Assert.Equal(callsBefore + 6, harness.Calls.Count);
        await using (var firstReplica = harness.Host.Services.CreateAsyncScope())
        await using (var secondReplica = harness.Host.Services.CreateAsyncScope())
        {
            var a = firstReplica.ServiceProvider.GetRequiredService<GitTransferWorkpackService>();
            var b = secondReplica.ServiceProvider.GetRequiredService<GitTransferWorkpackService>();
            var replay = await Task.WhenAll(
                a.EnqueueAsync(prepId, planId, diffId,
                    planSha, reconciliationSha, Guid.NewGuid(), default),
                b.EnqueueAsync(prepId, planId, diffId,
                    planSha, reconciliationSha, Guid.NewGuid(), default));
            Assert.All(replay, x => Assert.Equal(workId, x.Id));
        }
        await using (var firstReplica = harness.Host.Services.CreateAsyncScope())
        await using (var secondReplica = harness.Host.Services.CreateAsyncScope())
        {
            var jobs1 = firstReplica.ServiceProvider.GetRequiredService<IGitTransferWorkpackRepository>();
            var jobs2 = secondReplica.ServiceProvider.GetRequiredService<IGitTransferWorkpackRepository>();
            var claims = await Task.WhenAll(
                jobs1.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(2), default),
                jobs2.ClaimAsync(Guid.NewGuid(), TimeSpan.FromMinutes(2), default));
            var firstLease = Assert.Single(claims, x => x is not null)!;
            Assert.Equal(workId, firstLease.Id);
            Assert.Equal(1, firstLease.Attempt);
            Assert.Equal("EvidencePending", firstLease.Phase);
            Assert.True(await jobs1.CheckpointEvidenceAsync(firstLease, default));
            Assert.False(await jobs1.CheckpointEvidenceAsync(firstLease, default));

            // Simulate a process crash after the persisted EvidenceChecked
            // checkpoint. Reclaim must resume without repeating that phase.
            var db = firstReplica.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GitTransferWorkpacks.Where(x => x.Id == workId)
                .ExecuteUpdateAsync(updates => updates.SetProperty(
                    x => x.LeaseUntilUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
            var secondLease = await jobs2.ClaimAsync(
                Guid.NewGuid(), TimeSpan.FromMinutes(2), default);
            Assert.NotNull(secondLease);
            Assert.Equal(workId, secondLease!.Id);
            Assert.Equal(2, secondLease.Attempt);
            Assert.Equal("EvidenceChecked", secondLease.Phase);

            // Original worker is irreversibly fenced, even if it wakes up
            // just before the new worker commits the next checkpoint.
            Assert.False(await jobs1.BlockAtWriteBarrierAsync(firstLease, default));
            Assert.True(await jobs2.BlockAtWriteBarrierAsync(secondLease, default));
            Assert.False(await jobs1.FailAsync(
                firstLease, "StaleWorkerAttempt", false, default));
            Assert.False(await jobs2.BlockAtWriteBarrierAsync(secondLease, default));
            Assert.Null(await jobs1.ClaimAsync(
                Guid.NewGuid(), TimeSpan.FromMinutes(2), default));
        }
        using (var read = new HttpRequestMessage(HttpMethod.Get,
            $"{operationalPath}/{planId}/transfer-workpacks/{workId}"))
        {
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var workJson = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.Equal("Blocked", workJson.RootElement.GetProperty("state").GetString());
            Assert.Equal("WriteBlocked", workJson.RootElement.GetProperty("phase").GetString());
            Assert.Equal("DestinationWritesNotAuthorized",
                workJson.RootElement.GetProperty("lastErrorCode").GetString());
            Assert.False(workJson.RootElement.GetProperty("readyForExecution").GetBoolean());
        }
        using (var read = new HttpRequestMessage(HttpMethod.Get,
            $"{operationalPath}/{planId}/transfer-workpacks/{workId}/events"))
        {
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var workJson = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "Queued", "Claimed", "EvidenceChecked",
                "Claimed", "WriteBlocked" }, workJson.RootElement.EnumerateArray()
                .Select(x => x.GetProperty("type").GetString()).ToArray());
        }
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.GitTransferWorkpacks.AsNoTracking()
                .CountAsync(x => x.ReconciliationId == diffId));
            Assert.False((await db.GitTransferWorkpacks.AsNoTracking()
                .SingleAsync(x => x.Id == workId)).ReadyForExecution);
            // SQL itself, not only the .NET DTO, forbids both an executable
            // workpack and a fabricated success state.
            var writable = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE git_transfer_workpacks SET ready_for_execution = {true}
                    WHERE id = {workId}
                    """));
            Assert.Equal("23514", writable.SqlState);
            var fabricated = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE git_transfer_workpacks SET state = {"Succeeded"}
                    WHERE id = {workId}
                    """));
            Assert.Equal("23514", fabricated.SqlState);
        }
        Assert.Equal(callsBefore + 6, harness.Calls.Count);

        // F-035 local-only export API never accepts a path, remote URL or
        // source credential. With no trusted roots configured it fails closed.
        var bundlePath = $"{operationalPath}/{planId}/transfer-workpacks/{workId}/source-bundle";
        using (var denied = await harness.Client.PostAsJsonAsync(bundlePath, new {
            expectedReconciliationSha256 = reconciliationSha
        }))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var deniedUser = await PostAsAsync(bundlePath, new {
            expectedReconciliationSha256 = reconciliationSha
        }, user))
            Assert.Equal(HttpStatusCode.Forbidden, deniedUser.StatusCode);
        using (var noLocalRoots = await PostAsAsync(bundlePath, new {
            expectedReconciliationSha256 = reconciliationSha
        }, admin))
        {
            Assert.Equal(HttpStatusCode.Conflict, noLocalRoots.StatusCode);
            Assert.Contains("LocalBundleStoreNotConfigured",
                await noLocalRoots.Content.ReadAsStringAsync());
        }

        // Verify the immutable PostgreSQL write boundary independently of
        // local-file generation (the real Git CLI runs in its own tests).
        // The internal store is given the exact source snapshot proof, not
        // arbitrary ref names from the HTTP caller.
        string sourceDigest;
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var compare = await db.IncrementalGitRefReconciliations
                .AsNoTracking().SingleAsync(x => x.Id == diffId);
            sourceDigest = compare.SourceEvidenceSha256;
            var evidenceStore = scope.ServiceProvider
                .GetRequiredService<IGitSourceBundleEvidenceStore>();
            var artifact = new GitSourceBundleArtifact(
                workId, sourceDigest, workId.ToString("N") + ".bundle",
                new string('d', 64), 1024, 1, DateTimeOffset.UtcNow);
            var saved = await evidenceStore.SaveAsync(prepId, planId,
                workId, reconciliationSha, artifact, default);
            Assert.True(saved.IsCurrent);
            Assert.False(saved.ReadyForExecution);
            Assert.Equal(sourceDigest, saved.SourceEvidenceSha256);
            var replay = await evidenceStore.SaveAsync(prepId, planId,
                workId, reconciliationSha, artifact, default);
            Assert.Equal(saved.BundleSha256, replay.BundleSha256);
            Assert.Equal(1, await db.GitSourceBundleEvidence.AsNoTracking()
                .CountAsync(x => x.WorkpackId == workId));
            var invalid = artifact with { BundleSha256 = new string('e', 64) };
            var mismatch = await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                evidenceStore.SaveAsync(prepId, planId, workId,
                    reconciliationSha, invalid, default));
            Assert.Equal("BundleProofMismatch", mismatch.Code);
            var unsafeFlag = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE git_source_bundle_evidence
                    SET ready_for_execution = {true}
                    WHERE workpack_id = {workId}
                    """));
            Assert.Equal("23514", unsafeFlag.SqlState);
            var unsafeName = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE git_source_bundle_evidence
                    SET bundle_file_name = {"../../etc/passwd"}
                    WHERE workpack_id = {workId}
                    """));
            Assert.Equal("23514", unsafeName.SqlState);
        }
        using (var audit = new HttpRequestMessage(HttpMethod.Get, bundlePath))
        {
            audit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await harness.Client.SendAsync(audit);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("readyForExecution").GetBoolean());
            Assert.Equal(new string('d', 64),
                body.RootElement.GetProperty("bundleSha256").GetString());
            Assert.DoesNotContain(ApiHarness.SourceToken,
                body.RootElement.ToString());
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

        // A same-row mutation that preserves a plausible JSON shape but
        // changes the recorded count must fail SHA/metadata verification.
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GitHubTargetRefEvidence
                .Where(x => x.Id == observationId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RefCount, 3));
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get,
            evidencePath + "/" + observationId + "?take=50"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var response = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("RefEvidenceIntegrityFailed",
                await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(callsBefore + 6, harness.Calls.Count);
        // Corrupt an F-032 count without changing the sealed proof; reads
        // must reject even though the persisted JSON retains valid syntax.
        await using (var scope = harness.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.IncrementalGitRefReconciliations
                .Where(x => x.Id == diffId)
                .ExecuteUpdateAsync(updates => updates.SetProperty(
                    x => x.MatchingCount, 2));
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get,
            diffPath + "/" + diffId))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using var reply = await harness.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
            Assert.Contains("ReconciliationIntegrityFailed",
                await reply.Content.ReadAsStringAsync());
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
