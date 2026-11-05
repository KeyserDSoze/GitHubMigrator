using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.AzureDevOps;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureDevOpsPageCheckpointTests
{
    [Fact]
    public async Task Ref_page_failure_replays_persisted_pages_without_refetching_them()
    {
        var source = Guid.NewGuid();
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var store = new FakePageStore();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var oneThousand = string.Join(",", Enumerable.Range(0, 1000)
            .Select(i => $$"""{"name":"refs/heads/feature{{i:0000}}","objectId":"sha-{{i}}","providerSecret":"NOT_FOR_STAGING"}"""));
        using var http = new HttpClient(new Stub(req =>
        {
            var url = req.RequestUri!;
            var path = url.AbsolutePath;
            var key = path + (url.Query.Contains("continuationToken", StringComparison.Ordinal)
                ? "#continued" : "#start");
            calls[key] = calls.TryGetValue(key, out var n) ? n + 1 : 1;
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains(url.Host, new[] { "dev.azure.com", "vssps.dev.azure.com" });
            if (path.EndsWith("/_apis/projects"))
                return Json("""{"value":[{"id":"p1","name":"Project","unsafeField":"NOT_FOR_STAGING"}]}""");
            if (path.EndsWith("/_apis/projects/p1"))
                return Json("""{"capabilities":{"versioncontrol":{"sourceControlType":"Git"}}}""");
            if (path.EndsWith("/_apis/git/repositories"))
                return Json("""{"value":[{"id":"repo-1","name":"Repo"}]}""");
            if (path.EndsWith("/_apis/git/repositories/repo-1/refs"))
            {
                if (url.Query.Contains("continuationToken", StringComparison.Ordinal))
                {
                    if (calls[key] == 1) throw new HttpRequestException("temporary provider failure");
                    return Json("""{"value":[{"name":"refs/heads/main","objectId":"last"}]}""");
                }
                var response = Json("{\"value\":[" + oneThousand + "]}");
                response.Headers.Add("X-MS-ContinuationToken", "opaque+/token=");
                return response;
            }
            if (path.EndsWith("/_apis/graph/users"))
                return Json("""{"value":[{"descriptor":"aad.one","displayName":"Person","mailAddress":"one@test.invalid","rawToken":"NOT_FOR_STAGING"}]}""");
            if (path.EndsWith("/_apis/graph/groups") || path.Contains("/_apis/accesscontrollists/"))
                return Json("""{"value":[]}""");
            throw new Xunit.Sdk.XunitException("Unexpected GET: " + path);
        }));
        var reader = new AzureDevOpsDiscoveryReader(http, TimeProvider.System, store);

        var error = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            reader.DiscoverWithCheckpointsAsync(source, "test-org", "TEST-PAT", lease, default));
        Assert.Equal("ProviderUnavailable", error.Code);
        Assert.NotNull(await store.GetAsync(lease.JobId, "projects", 0, default));
        Assert.NotNull(await store.GetAsync(lease.JobId, "refs:repo-1", 0, default));
        Assert.Null(await store.GetAsync(lease.JobId, "refs:repo-1", 1, default));
        Assert.DoesNotContain("NOT_FOR_STAGING",
            string.Join(" ", store.Items.Select(x => x.SafePayloadJson)));

        var result = await reader.DiscoverWithCheckpointsAsync(
            source, "test-org", "TEST-PAT", lease, default);
        Assert.Empty(result.Refs); // leased workers never retain the full ref list
        Assert.True(result.RefsExternalized);
        Assert.Equal(1001, result.RefCount);
        Assert.True(result.IdentitiesExternalized);
        Assert.Empty(result.Snapshot.Identities);
        Assert.Equal(1, result.IdentityCount);
        Assert.Single(result.Snapshot.Projects);
        Assert.True(result.IdentitiesComplete);
        Assert.Equal(1, calls["/test-org/_apis/projects#start"]);
        Assert.Equal(1, calls["/test-org/p1/_apis/git/repositories/repo-1/refs#start"]);
        Assert.Equal(2, calls["/test-org/p1/_apis/git/repositories/repo-1/refs#continued"]);
        Assert.Equal(1, store.Items.Count(x => x.ResourceKey == "refs:repo-1" && x.PageIndex == 0));
        Assert.Equal(1, store.Items.Count(x => x.ResourceKey == "refs:repo-1" && x.PageIndex == 1));
        Assert.All(store.Items, item => Assert.Equal(64, item.RequestHash.Length));
    }

    [Fact]
    public async Task Graph_page_two_retry_reuses_first_1000_users_without_materializing_identity_inventory()
    {
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var store = new FakePageStore();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var users = string.Join(",", Enumerable.Range(0, 1000).Select(i =>
            $$"""{"descriptor":"aad.user-{{i:0000}}","displayName":"Person {{i}}","unsafeToken":"DO_NOT_SAVE"}"""));
        using var http = new HttpClient(new Stub(req =>
        {
            var url = req.RequestUri!;
            var path = url.AbsolutePath;
            var key = path + (url.Query.Contains("continuationToken", StringComparison.Ordinal)
                ? "#next" : "#first");
            calls[key] = calls.GetValueOrDefault(key) + 1;
            if (path.EndsWith("/_apis/projects"))
                return Json("""{"value":[]}""");
            if (path.EndsWith("/_apis/graph/users"))
            {
                if (key.EndsWith("#next", StringComparison.Ordinal))
                {
                    if (calls[key] == 1) throw new HttpRequestException("transient");
                    return Json("""{"value":[{"descriptor":"aad.user-1000","displayName":"Last"}]}""");
                }
                var result = Json("{\"value\":[" + users + "]}");
                result.Headers.Add("X-MS-ContinuationToken", "next");
                return result;
            }
            if (path.EndsWith("/_apis/graph/groups") ||
                path.Contains("/_apis/accesscontrollists/"))
                return Json("""{"value":[]}""");
            throw new Xunit.Sdk.XunitException("Unexpected GET " + path);
        }));
        var reader = new AzureDevOpsDiscoveryReader(http, TimeProvider.System, store);
        var source = Guid.NewGuid();
        var first = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            reader.DiscoverWithCheckpointsAsync(source, "test-org", "PAT", lease, default));
        Assert.Equal("ProviderUnavailable", first.Code);
        Assert.NotNull(await store.GetAsync(lease.JobId, "graph-users", 0, default));
        Assert.Null(await store.GetAsync(lease.JobId, "graph-users", 1, default));
        Assert.DoesNotContain("DO_NOT_SAVE",
            string.Join(" ", store.Items.Select(x => x.SafePayloadJson)));

        var inventory = await reader.DiscoverWithCheckpointsAsync(
            source, "test-org", "PAT", lease, default);
        Assert.True(inventory.IdentitiesExternalized);
        Assert.Equal(1001, inventory.IdentityCount);
        Assert.Empty(inventory.Snapshot.Identities);
        Assert.True(inventory.IdentitiesComplete);
        Assert.Equal(1, calls["/test-org/_apis/graph/users#first"]);
        Assert.Equal(2, calls["/test-org/_apis/graph/users#next"]);
        Assert.Equal(2, store.Items.Count(x => x.ResourceKey == "graph-users"));
    }

    [Fact]
    public async Task Project_page_two_retry_reuses_first_100_project_rows_and_skip_cursor()
    {
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var store = new FakePageStore();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstPage = string.Join(",", Enumerable.Range(0, 100)
            .Select(i => $$"""{"id":"project-{{i}}","name":"Project {{i}}"}"""));
        using var http = new HttpClient(new Stub(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = req.RequestUri.Query;
            var name = path + (query.Contains("skip=100", StringComparison.Ordinal) ? "#second" : "#first");
            calls[name] = calls.TryGetValue(name, out var count) ? count + 1 : 1;
            if (path.EndsWith("/_apis/projects"))
            {
                if (name.EndsWith("#second", StringComparison.Ordinal))
                {
                    if (calls[name] == 1) throw new HttpRequestException("temporary failure");
                    return Json("""{"value":[{"id":"project-final","name":"Final"}]}""");
                }
                return Json("{\"value\":[" + firstPage + "]}");
            }
            if (path.Contains("/_apis/projects/", StringComparison.Ordinal))
                return Json("""{"capabilities":{"versioncontrol":{"sourceControlType":"Git"}}}""");
            if (path.EndsWith("/_apis/git/repositories", StringComparison.Ordinal) ||
                path.EndsWith("/_apis/graph/users", StringComparison.Ordinal) ||
                path.EndsWith("/_apis/graph/groups", StringComparison.Ordinal))
                return Json("""{"value":[]}""");
            throw new Xunit.Sdk.XunitException("Unexpected " + path);
        }));
        var reader = new AzureDevOpsDiscoveryReader(http, TimeProvider.System, store);
        var error = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            reader.DiscoverWithCheckpointsAsync(Guid.NewGuid(), "test-org", "PAT", lease, default));
        Assert.Equal("ProviderUnavailable", error.Code);
        Assert.NotNull(await store.GetAsync(lease.JobId, "projects", 0, default));
        Assert.Null(await store.GetAsync(lease.JobId, "projects", 1, default));

        var inventory = await reader.DiscoverWithCheckpointsAsync(
            Guid.NewGuid(), "test-org", "PAT", lease, default);
        Assert.Equal(101, inventory.Snapshot.Projects.Count);
        Assert.True(inventory.RefsExternalized);
        Assert.Empty(inventory.Refs);
        Assert.Equal(0, inventory.RefCount);
        Assert.Equal(1, calls["/test-org/_apis/projects#first"]);
        Assert.Equal(2, calls["/test-org/_apis/projects#second"]);
        Assert.Equal(2, store.Items.Count(x => x.ResourceKey == "projects"));
        Assert.DoesNotContain(inventory.Issues, issue => issue.Code == "ProjectsTruncated");
    }

    [Fact]
    public async Task Failed_repository_listing_reuses_project_and_capability_checkpoints()
    {
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var store = new FakePageStore();
        var requests = new Dictionary<string, int>(StringComparer.Ordinal);
        using var http = new HttpClient(new Stub(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            requests[path] = requests.TryGetValue(path, out var n) ? n + 1 : 1;
            if (path.EndsWith("/_apis/projects"))
                return Json("""{"value":[{"id":"p1","name":"Project","providerToken":"DO_NOT_STAGE"}]}""");
            if (path.EndsWith("/_apis/projects/p1"))
                return Json("""{"capabilities":{"versioncontrol":{"sourceControlType":"Git","otherField":"DO_NOT_STAGE"}},"serviceConnections":"DO_NOT_STAGE"}""");
            if (path.EndsWith("/_apis/git/repositories"))
            {
                if (requests[path] == 1) throw new HttpRequestException("temporary repository list error");
                return Json("""{"value":[{"id":"repo-1","name":"Repo","privateMetadata":"DO_NOT_STAGE"}]}""");
            }
            if (path.EndsWith("/_apis/git/repositories/repo-1/refs"))
                return Json("""{"value":[{"name":"refs/heads/main","objectId":"abc"}]}""");
            if (path.EndsWith("/_apis/graph/users") || path.EndsWith("/_apis/graph/groups") ||
                path.Contains("/_apis/accesscontrollists/"))
                return Json("""{"value":[]}""");
            throw new Xunit.Sdk.XunitException("Unexpected GET " + path);
        }));
        var reader = new AzureDevOpsDiscoveryReader(http, TimeProvider.System, store);
        var source = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<DiscoveryProviderException>(() =>
            reader.DiscoverWithCheckpointsAsync(source, "az-org", "PAT", lease, default));
        Assert.Equal("ProviderUnavailable", error.Code);
        Assert.NotNull(await store.GetAsync(lease.JobId, "projects", 0, default));
        Assert.NotNull(await store.GetAsync(lease.JobId, "capabilities:p1", 0, default));
        Assert.Null(await store.GetAsync(lease.JobId, "repos:p1", 0, default));

        var completed = await reader.DiscoverWithCheckpointsAsync(source, "az-org", "PAT", lease, default);
        Assert.Single(completed.Snapshot.Projects[0].Repositories);
        Assert.Empty(completed.Refs);
        Assert.True(completed.RefsExternalized);
        Assert.Equal(1, completed.RefCount);
        Assert.Equal(1, requests["/az-org/_apis/projects"]);
        Assert.Equal(1, requests["/az-org/_apis/projects/p1"]);
        Assert.Equal(2, requests["/az-org/p1/_apis/git/repositories"]);
        Assert.NotNull(await store.GetAsync(lease.JobId, "repos:p1", 0, default));
        Assert.DoesNotContain("DO_NOT_STAGE",
            string.Join(" ", store.Items.Select(x => x.SafePayloadJson)));
        var totalRequests = requests.Values.Sum();
        await reader.DiscoverWithCheckpointsAsync(source, "az-org", "PAT", lease, default);
        Assert.Equal(totalRequests, requests.Values.Sum());
    }

    [Fact]
    public async Task Mismatched_page_request_is_rejected_before_any_provider_call()
    {
        var source = Guid.NewGuid();
        var lease = new DiscoveryLeaseContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var store = new FakePageStore();
        await store.SaveAsync(lease, new DiscoveryPageCheckpoint(
            lease.JobId, "projects", 0, new string('0', 64),
            """{"value":[]}""", null, DateTimeOffset.UtcNow), default);
        var calls = 0;
        using var http = new HttpClient(new Stub(_ =>
        {
            calls++;
            return Json("""{"value":[]}""");
        }));
        var reader = new AzureDevOpsDiscoveryReader(http, TimeProvider.System, store);
        var error = await Assert.ThrowsAsync<DiscoveryCheckpointException>(() =>
            reader.DiscoverWithCheckpointsAsync(source, "test-org", "TEST-PAT", lease, default));
        Assert.Equal("CheckpointRequestMismatch", error.Code);
        Assert.Equal(0, calls);
    }

    private sealed class FakePageStore : IDiscoveryPageCheckpointStore, IDiscoveryIdentityObservationStore
    {
        private readonly Dictionary<(Guid, string, int), DiscoveryPageCheckpoint> _rows = [];
        public IReadOnlyCollection<DiscoveryPageCheckpoint> Items => _rows.Values;

        public Task<DiscoveryIdentityProgress> GetProgressAsync(Guid job, CancellationToken ct)
        {
            var pages = _rows.Values.Where(x =>
                x.JobId == job && x.ResourceKey == "graph-users").ToArray();
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in pages)
            {
                using var parsed = JsonDocument.Parse(page.SafePayloadJson);
                foreach (var item in parsed.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (item.TryGetProperty("descriptor", out var id) &&
                        id.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(id.GetString()))
                        known.Add(id.GetString()!);
                }
            }
            return Task.FromResult(new DiscoveryIdentityProgress(job, pages.Length, known.Count));
        }

        public Task<DiscoveryIdentityObservationPage> ListAsync(
            Guid job, string? after, int take, CancellationToken ct) =>
            throw new NotSupportedException("Reader tests only use page-count evidence");

        public Task VerifySnapshotAsync(MigrationJobLease lease, CancellationToken ct) =>
            throw new NotSupportedException("Reader tests do not finalize staging");
        public Task<DiscoveryPageCheckpoint?> GetAsync(
            Guid job, string resource, int page, CancellationToken ct) =>
            Task.FromResult(_rows.GetValueOrDefault((job, resource, page)));
        public Task<DiscoveryPageCheckpoint> SaveAsync(
            DiscoveryLeaseContext lease, DiscoveryPageCheckpoint page, CancellationToken ct)
        {
            _rows.TryAdd((page.JobId, page.ResourceKey, page.PageIndex), page);
            return Task.FromResult(_rows[(page.JobId, page.ResourceKey, page.PageIndex)]);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) => Task.FromResult(responder(req));
    }
}
