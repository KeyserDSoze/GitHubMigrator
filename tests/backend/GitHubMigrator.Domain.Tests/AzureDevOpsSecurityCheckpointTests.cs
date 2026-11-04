using System.Net;
using System.Net.Http;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.AzureDevOps;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureDevOpsSecurityCheckpointTests
{
    [Fact]
    public async Task Groups_page_two_failure_resumes_without_refetch_and_preserves_raw_allow_deny()
    {
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var checkpoints = new FakeCheckpoints();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        using var client = new HttpClient(new Stub(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains(req.RequestUri!.Host, new[] { "dev.azure.com", "vssps.dev.azure.com" });
            Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
            Assert.DoesNotContain("TEST-PAT", req.RequestUri.ToString());

            var uri = req.RequestUri!;
            var path = uri.AbsolutePath;
            var key = path.EndsWith("/_apis/graph/groups", StringComparison.Ordinal)
                ? (uri.Query.Contains("continuationToken", StringComparison.Ordinal)
                    ? "groups-next" : "groups-start")
                : path.Contains("/_apis/graph/Memberships/", StringComparison.Ordinal)
                    ? "membership:" + path.Split('/').Last()
                    : "acl:" + Uri.UnescapeDataString(uri.Query.Split("token=")[1].Split('&')[0]);
            calls[key] = calls.TryGetValue(key, out var count) ? count + 1 : 1;

            if (key == "groups-start")
            {
                var response = Json("""{"value":[{"descriptor":"group.one","displayName":"One","legacyDescriptor":"legacy-one","arbitraryCredential":"DO_NOT_STAGE"}]}""");
                response.Headers.Add("X-MS-ContinuationToken", "opaque+/groups");
                return response;
            }
            if (key == "groups-next")
            {
                if (calls[key] == 1) throw new HttpRequestException("Azure unavailable");
                return Json("""{"value":[{"descriptor":"group.two","displayName":"Two","arbitraryCredential":"DO_NOT_STAGE"}]}""");
            }
            if (key.StartsWith("membership:", StringComparison.Ordinal))
            {
                var descriptor = Uri.UnescapeDataString(path.Split('/').Last());
                return Json("{\"value\":[{\"containerDescriptor\":\"" + descriptor +
                    "\",\"memberDescriptor\":\"aad.user\",\"PAT\":\"DO_NOT_STAGE\"}]}");
            }
            if (key.StartsWith("acl:", StringComparison.Ordinal))
            {
                var token = key["acl:".Length..];
                return Json("{\"value\":[{\"token\":\"" + token +
                    "\",\"inheritPermissions\":true,\"acesDictionary\":{\"principal\":{\"descriptor\":\"principal\",\"allow\":4,\"deny\":2,\"secretValue\":\"DO_NOT_STAGE\"}},\"extra\":\"DO_NOT_STAGE\"}]}");
            }
            throw new Xunit.Sdk.XunitException("Unexpected endpoint: " + path);
        }));
        var reader = new AzureDevOpsSecurityReader(client, checkpoints, TimeProvider.System);
        SourceProject project = new("project-1", "P", [
            new SourceRepository("repo-1", "project-1", "R", VersionControlKind.Git)]);

        var error = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            reader.ReadAsync("az-org", "TEST-PAT", [project], default, lease));
        Assert.Equal("ProviderUnavailable", error.Code);
        Assert.NotNull(await checkpoints.GetAsync(lease.JobId, "security-groups", 0, default));
        Assert.Null(await checkpoints.GetAsync(lease.JobId, "security-groups", 1, default));

        var finished = await reader.ReadAsync("az-org", "TEST-PAT", [project], default, lease);
        Assert.True(finished.Coverage.GroupsComplete);
        Assert.True(finished.Coverage.MembershipsComplete);
        Assert.True(finished.Coverage.AclsCollected);
        Assert.False(finished.Coverage.EffectivePermissionsVerified);
        Assert.Equal(2, finished.Groups.Count);
        Assert.Equal(2, finished.Memberships.Count);
        Assert.Equal(2, finished.Acls.Count);
        Assert.All(finished.Acls, row =>
        {
            Assert.Equal("principal", row.SecurityDescriptor);
            Assert.Equal(4, row.AllowMask);
            Assert.Equal(2, row.DenyMask);
            Assert.True(row.InheritPermissions);
        });
        Assert.Equal(1, calls["groups-start"]);
        Assert.Equal(2, calls["groups-next"]);
        Assert.Equal(6, checkpoints.Saved.Count); // two group pages, two memberships, parent + repo ACL
        Assert.DoesNotContain("DO_NOT_STAGE",
            string.Join(" ", checkpoints.Saved.Select(x => x.SafePayloadJson)));
        Assert.DoesNotContain("TEST-PAT",
            string.Join(" ", checkpoints.Saved.Select(x => x.SafePayloadJson)));

        var totalCalls = calls.Values.Sum();
        var replay = await reader.ReadAsync("az-org", "TEST-PAT", [project], default, lease);
        Assert.Equal(totalCalls, calls.Values.Sum()); // second full replay is DB-only
        Assert.Equal(finished.Acls, replay.Acls);
    }

    [Fact]
    public async Task ACL_response_failure_is_retried_without_repeating_saved_group_and_member_pages()
    {
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var checkpoints = new FakeCheckpoints();
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        using var client = new HttpClient(new Stub(req =>
        {
            var uri = req.RequestUri!;
            var path = uri.AbsolutePath;
            var kind = path.Contains("accesscontrollists", StringComparison.Ordinal) ? "acl" :
                path.Contains("Memberships", StringComparison.Ordinal) ? "members" : "groups";
            reads[kind] = reads.TryGetValue(kind, out var n) ? n + 1 : 1;
            if (kind == "groups")
                return Json("""{"value":[{"descriptor":"g1","displayName":"Group"}]}""");
            if (kind == "members")
                return Json("""{"value":[{"containerDescriptor":"g1","memberDescriptor":"aad.user"}]}""");
            if (reads["acl"] == 1) throw new HttpRequestException("temporary ACL outage");
            var token = Uri.UnescapeDataString(uri.Query.Split("token=")[1].Split('&')[0]);
            return Json("{\"value\":[{\"token\":\"" + token +
                "\",\"inheritPermissions\":true,\"acesDictionary\":{}}]}");
        }));
        SourceProject project = new("project-1", "P", [
            new SourceRepository("repo-1", "project-1", "R", VersionControlKind.Git)]);
        var reader = new AzureDevOpsSecurityReader(client, checkpoints, TimeProvider.System);
        var transient = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            reader.ReadAsync("az-org", "TEST-PAT", [project], default, lease));
        Assert.Equal("ProviderUnavailable", transient.Code);
        var restored = await reader.ReadAsync("az-org", "TEST-PAT", [project], default, lease);
        Assert.True(restored.Coverage.AclsCollected);
        Assert.Equal(1, reads["groups"]);
        Assert.Equal(1, reads["members"]);
        Assert.Equal(3, reads["acl"]); // failed parent, successful parent and repo
    }

    [Fact]
    public async Task Direct_membership_second_page_retries_using_checkpointed_group_and_first_edge()
    {
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var checkpoints = new FakeCheckpoints();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        using var client = new HttpClient(new Stub(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var continued = req.RequestUri.Query.Contains("continuationToken", StringComparison.Ordinal);
            var key = path.Contains("/_apis/graph/Memberships/", StringComparison.Ordinal)
                ? continued ? "members-next" : "members-start" : "groups";
            calls[key] = calls.TryGetValue(key, out var n) ? n + 1 : 1;
            if (key == "groups")
                return Json("""{"value":[{"descriptor":"group.1","displayName":"Operators"}]}""");
            if (key == "members-start")
            {
                var response = Json("""{"value":[{"containerDescriptor":"group.1","memberDescriptor":"user.a","unknownPAT":"DO_NOT_STAGE"}]}""");
                response.Headers.Add("X-MS-ContinuationToken", "opaque+/next");
                return response;
            }
            if (calls[key] == 1) throw new HttpRequestException("temporary second page failure");
            return Json("""{"value":[{"containerDescriptor":"group.1","memberDescriptor":"user.b"}]}""");
        }));
        var reader = new AzureDevOpsSecurityReader(client, checkpoints, TimeProvider.System);
        var error = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            reader.ReadAsync("az-org", "TEST-PAT", [], default, lease));
        Assert.Equal("ProviderUnavailable", error.Code);
        Assert.NotNull(await checkpoints.GetAsync(lease.JobId, "security-groups", 0, default));
        Assert.Equal(2, checkpoints.Saved.Count); // group + first membership page

        var result = await reader.ReadAsync("az-org", "TEST-PAT", [], default, lease);
        Assert.True(result.Coverage.MembershipsComplete);
        Assert.Equal(2, result.Memberships.Count);
        Assert.Contains(result.Memberships, m => m.MemberDescriptor == "user.b");
        Assert.Equal(1, calls["groups"]);
        Assert.Equal(1, calls["members-start"]);
        Assert.Equal(2, calls["members-next"]);
        Assert.Equal(3, checkpoints.Saved.Count);
        Assert.DoesNotContain("DO_NOT_STAGE",
            string.Join(" ", checkpoints.Saved.Select(x => x.SafePayloadJson)));
        var providerCalls = calls.Values.Sum();
        await reader.ReadAsync("az-org", "TEST-PAT", [], default, lease);
        Assert.Equal(providerCalls, calls.Values.Sum());
    }

    [Fact]
    public async Task Repeated_membership_cursor_sets_incomplete_coverage_instead_of_reporting_all_members()
    {
        using var client = new HttpClient(new Stub(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/_apis/graph/groups"))
                return Json("""{"value":[{"descriptor":"group.1","displayName":"Operators"}]}""");
            var response = Json("""{"value":[{"containerDescriptor":"group.1","memberDescriptor":"user.a"}]}""");
            response.Headers.Add("X-MS-ContinuationToken", "same-cursor");
            return response;
        }));
        var result = await new AzureDevOpsSecurityReader(client)
            .ReadAsync("az-org", "PAT", [], default);
        Assert.True(result.Coverage.GroupsComplete);
        Assert.False(result.Coverage.MembershipsComplete);
        Assert.Contains(result.Issues, issue => issue.Code == "MembershipsPaginationIncomplete");
        Assert.Single(result.Memberships);
    }

    private sealed class FakeCheckpoints : IDiscoveryPageCheckpointStore
    {
        private readonly Dictionary<(Guid, string, int), DiscoveryPageCheckpoint> rows = [];
        public IReadOnlyCollection<DiscoveryPageCheckpoint> Saved => rows.Values;
        public Task<DiscoveryPageCheckpoint?> GetAsync(Guid jobId, string resourceKey,
            int pageIndex, CancellationToken ct) =>
            Task.FromResult(rows.GetValueOrDefault((jobId, resourceKey, pageIndex)));
        public Task<DiscoveryPageCheckpoint> SaveAsync(DiscoveryLeaseContext lease,
            DiscoveryPageCheckpoint checkpoint, CancellationToken ct)
        {
            rows.TryAdd((checkpoint.JobId, checkpoint.ResourceKey, checkpoint.PageIndex), checkpoint);
            return Task.FromResult(rows[(checkpoint.JobId, checkpoint.ResourceKey, checkpoint.PageIndex)]);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(reply(req));
    }
}
