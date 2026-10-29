using System.Net;
using System.Net.Http;
using System.Text;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.AzureDevOps;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureDevOpsDiscoveryReaderTests
{
    private static Guid ConnectionId => Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Discovers_git_refs_and_graph_users_without_permission_guesses()
    {
        var calls = new List<Uri>();
        var client = new HttpClient(new Stub(req =>
        {
            calls.Add(req.RequestUri!);
            Assert.Equal("Basic", req.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(":private-token")), req.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("private-token", req.RequestUri!.ToString());

            var path = req.RequestUri.AbsolutePath;
            if (path.EndsWith("/_apis/projects")) return Json("""{"value":[{"id":"pr-1","name":"Alpha"}]}""");
            if (path.EndsWith("/_apis/projects/pr-1")) return Json("""{"capabilities":{"versioncontrol":{"sourceControlType":"Git"}}}""");
            if (path.EndsWith("/_apis/git/repositories")) return Json("""{"value":[{"id":"git-1","name":"Service"}]}""");
            if (path.EndsWith("/_apis/git/repositories/git-1/refs"))
                return Json("""{"value":[{"name":"refs/heads/main","objectId":"abcdef"},{"name":"refs/tags/v1","objectId":"123456"}]}""");
            if (path.EndsWith("/_apis/graph/users"))
                return Json("""{"value":[{"descriptor":"aad.123","displayName":"An Example","mailAddress":"example@test.invalid"}]}""");
            if (path.EndsWith("/_apis/graph/groups")) return Json("""{"value":[]}""");
            if (path.Contains("/_apis/accesscontrollists/")) return Json("""{"value":[]}""");
            throw new Xunit.Sdk.XunitException("Unexpected call: " + path);
        }));
        var inventory = await new AzureDevOpsDiscoveryReader(client, TimeProvider.System)
            .DiscoverAsync(ConnectionId, "test-org", "private-token", CancellationToken.None);

        Assert.Equal("test-org", inventory.Organization);
        Assert.Single(inventory.Snapshot.Projects);
        Assert.Equal("Service", Assert.Single(inventory.Snapshot.Projects[0].Repositories).Name);
        Assert.Equal(2, inventory.Refs.Count);
        Assert.Contains(inventory.Refs, x => x.Name == "refs/heads/main" && x.ObjectId == "abcdef");
        Assert.Equal("aad.123", Assert.Single(inventory.Snapshot.Identities).ProviderId);
        Assert.True(inventory.IdentitiesComplete);
        Assert.False(inventory.PermissionsComplete);
        Assert.Equal(DiscoveryCompleteness.Partial, inventory.Completeness);
        Assert.Contains(inventory.Issues, x => x.Code == "EffectivePermissionsNotVerified");
        Assert.DoesNotContain("private-token", System.Text.Json.JsonSerializer.Serialize(inventory));
        Assert.Equal(8, calls.Count);
    }

    [Fact]
    public async Task Projects_continue_using_skip_and_git_refs_use_opaque_continuation()
    {
        var seen = new List<string>();
        var pageOne = string.Join(",", Enumerable.Range(0, 100).Select(i =>
            $$"""{"id":"project-{{i}}","name":"Project{{i}}"}"""));
        var client = new HttpClient(new Stub(req =>
        {
            var url = req.RequestUri!;
            seen.Add(url.ToString());
            if (url.AbsolutePath.EndsWith("/_apis/projects"))
                return url.Query.Contains("%24skip=100", StringComparison.OrdinalIgnoreCase) ||
                       url.Query.Contains("$skip=100", StringComparison.Ordinal)
                    ? Json("""{"value":[{"id":"last-project","name":"Last"}]}""")
                    : Json("{\"value\":[" + pageOne + "]}");
            if (url.AbsolutePath.Contains("/_apis/projects/"))
                return Json("""{"capabilities":{"versioncontrol":{"sourceControlType":"Git"}}}""");
            if (url.AbsolutePath.EndsWith("/_apis/git/repositories"))
                return Json(url.AbsolutePath.Contains("last-project") ?
                    """{"value":[{"id":"repo","name":"LastRepo"}]}""" : """{"value":[]}""");
            if (url.AbsolutePath.EndsWith("/refs"))
            {
                var result = Json(url.Query.Contains("continuationToken") ?
                    """{"value":[{"name":"refs/heads/dev","objectId":"b"}]}""" :
                    """{"value":[{"name":"refs/heads/main","objectId":"a"}]}""");
                if (!url.Query.Contains("continuationToken"))
                    result.Headers.Add("X-MS-ContinuationToken", "opaque+/token=");
                return result;
            }
            if (url.AbsolutePath.EndsWith("/_apis/graph/users")) return Json("""{"value":[]}""");
            if (url.AbsolutePath.EndsWith("/_apis/graph/groups")) return Json("""{"value":[]}""");
            if (url.AbsolutePath.Contains("/_apis/accesscontrollists/")) return Json("""{"value":[]}""");
            throw new Xunit.Sdk.XunitException(url.ToString());
        }));
        var inventory = await new AzureDevOpsDiscoveryReader(client, TimeProvider.System)
            .DiscoverAsync(ConnectionId, "test-org", "private-token", CancellationToken.None);
        Assert.Equal(101, inventory.Snapshot.Projects.Count);
        Assert.Equal(2, inventory.Refs.Count);
        Assert.Contains(seen, x => x.Contains("continuationToken=opaque%2B%2Ftoken%3D"));
        Assert.Contains(seen, x => x.Contains("skip=100"));
    }

    [Fact]
    public async Task Tfvc_roots_and_graph_scope_errors_are_visible_as_partial()
    {
        var client = new HttpClient(new Stub(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_apis/projects")) return Json("""{"value":[{"id":"tfvc-project","name":"Legacy"}]}""");
            if (path.EndsWith("/_apis/projects/tfvc-project"))
                return Json("""{"capabilities":{"versioncontrol":{"sourceControlType":"Tfvc"}}}""");
            if (path.EndsWith("/_apis/git/repositories")) return Json("""{"value":[]}""");
            if (path.EndsWith("/_apis/tfvc/branches"))
                return Json("""{"value":[{"path":"$/Legacy/Main","children":[{"path":"$/Legacy/Release"}]}]}""");
            if (path.EndsWith("/_apis/graph/users")) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (path.EndsWith("/_apis/graph/groups")) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            throw new Xunit.Sdk.XunitException(path);
        }));
        var inventory = await new AzureDevOpsDiscoveryReader(client, TimeProvider.System)
            .DiscoverAsync(ConnectionId, "test-org", "private-token", CancellationToken.None);
        var paths = inventory.Snapshot.Projects.Single().Repositories;
        Assert.Equal(3, paths.Count);
        Assert.All(paths, x => Assert.Equal(VersionControlKind.Tfvc, x.VersionControl));
        Assert.Contains(paths, x => x.SourcePath == "$/Legacy/Release");
        Assert.Contains(inventory.Issues, x => x.Code == "InsufficientScope");
        Assert.Contains(inventory.Issues, x => x.Code == "TfvcPathsRequireFullInventory");
        Assert.False(inventory.IdentitiesComplete);
    }

    [Fact]
    public async Task Invalid_source_organization_is_rejected_before_http()
    {
        var calls = 0;
        var client = new HttpClient(new Stub(_ => { calls++; return Json("{}"); }));
        await Assert.ThrowsAsync<ArgumentException>(() => new AzureDevOpsDiscoveryReader(client, TimeProvider.System)
            .DiscoverAsync(ConnectionId, "http://127.0.0.1/", "private-token", CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
}
