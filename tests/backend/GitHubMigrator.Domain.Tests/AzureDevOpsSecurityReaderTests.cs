using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Infrastructure.AzureDevOps;

namespace GitHubMigrator.Domain.Tests;

public sealed class AzureDevOpsSecurityReaderTests
{
    private const string Project = "aabbccdd-0000-0000-0000-000000000001";
    private const string Repository = "aabbccdd-0000-0000-0000-000000000002";
    private const string LegacyGroup = "Microsoft.TeamFoundation.Identity;S-1-9-hello";
    private const string LegacyUser = "Microsoft.TeamFoundation.Identity;S-1-9-user";

    [Fact]
    public async Task Captures_direct_memberships_and_separate_parent_repo_allow_deny_without_claiming_effective_access()
    {
        var calls = new List<Uri>();
        var http = new HttpClient(new Stub(req =>
        {
            var url = req.RequestUri!;
            calls.Add(url);
            Assert.True(req.Method == HttpMethod.Get);
            Assert.Contains(url.Host, new[] { "dev.azure.com", "vssps.dev.azure.com" });
            Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(":hidden-pat")), req.Headers.Authorization.Parameter);
            Assert.DoesNotContain("hidden-pat", url.ToString());

            if (url.AbsolutePath.EndsWith("/_apis/graph/groups"))
                return Json("""{"value":[{"descriptor":"vssgp.team","displayName":"Contributors","legacyDescriptor":"Microsoft.TeamFoundation.Identity;S-1-9-hello"}]}""");
            if (url.AbsolutePath.Contains("/_apis/graph/Memberships/"))
                return Json("""{"value":[{"containerDescriptor":"vssgp.team","memberDescriptor":"aad.user"},{"containerDescriptor":"different","memberDescriptor":"ignored"}]}""");
            if (url.AbsolutePath.Contains("/_apis/accesscontrollists/"))
            {
                var securityToken = System.Net.WebUtility.UrlDecode(url.Query.Split("token=")[1].Split('&')[0]);
                if (securityToken == $"repoV2/{Project}")
                    return Json(JsonSerializer.Serialize(new { value = new[] {
                        new { token = securityToken, inheritPermissions = true,
                            acesDictionary = new Dictionary<string, object> {
                                [LegacyGroup] = new { descriptor = LegacyGroup, allow = 2, deny = 0 }
                            } } } }));
                if (securityToken == $"repoV2/{Project}/{Repository}")
                    return Json(JsonSerializer.Serialize(new { value = new[] {
                        new { token = securityToken, inheritPermissions = false,
                            acesDictionary = new Dictionary<string, object> {
                                [LegacyUser] = new { descriptor = LegacyUser, allow = 4, deny = 2 }
                            } } } }));
            }
            throw new Xunit.Sdk.XunitException("Unexpected provider endpoint");
        }));
        var project = new GitHubMigrator.Domain.SourceProject(Project, "Alpha",
            [new(Repository, Project, "Repo", GitHubMigrator.Domain.VersionControlKind.Git)]);
        var evidence = await new AzureDevOpsSecurityReader(http)
            .ReadAsync("my-org", "hidden-pat", [project], CancellationToken.None);
        Assert.True(evidence.Coverage.GroupsComplete);
        Assert.True(evidence.Coverage.MembershipsComplete);
        Assert.True(evidence.Coverage.AclsCollected);
        Assert.False(evidence.Coverage.EffectivePermissionsVerified);
        Assert.Equal(LegacyGroup, Assert.Single(evidence.Groups).LegacyDescriptor);
        Assert.Equal("aad.user", Assert.Single(evidence.Memberships).MemberDescriptor);
        Assert.Equal(2, evidence.Acls.Count);
        Assert.Contains(evidence.Acls, x => x.Scope == "project" && x.AllowMask == 2 &&
            x.InheritPermissions && x.RepositoryId == Repository);
        Assert.Contains(evidence.Acls, x => x.Scope == "repository" && x.AllowMask == 4 &&
            x.DenyMask == 2 && !x.InheritPermissions);
        Assert.Empty(evidence.Issues);
        Assert.Equal(4, calls.Count);
        Assert.DoesNotContain("hidden-pat", JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public async Task Missing_Graph_scope_is_partial_while_project_ACL_failure_is_reported()
    {
        var http = new HttpClient(new Stub(req =>
            req.RequestUri!.Host == "vssps.dev.azure.com"
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var project = new GitHubMigrator.Domain.SourceProject(Project, "Alpha",
            [new(Repository, Project, "Repo", GitHubMigrator.Domain.VersionControlKind.Git)]);
        var evidence = await new AzureDevOpsSecurityReader(http)
            .ReadAsync("my-org", "hidden-pat", [project], CancellationToken.None);
        Assert.False(evidence.Coverage.GroupsComplete);
        Assert.False(evidence.Coverage.MembershipsComplete);
        Assert.False(evidence.Coverage.AclsCollected);
        Assert.False(evidence.Coverage.EffectivePermissionsVerified);
        Assert.Contains(evidence.Issues, i => i.Code == "GroupsUnavailable:InsufficientScope");
        Assert.Contains(evidence.Issues, i => i.Code == "ProjectAclUnavailable:InsufficientScope");
        Assert.Contains(evidence.Issues, i => i.Code == "RepositoryAclUnavailable:InsufficientScope");
    }

    [Fact]
    public async Task Group_pagination_follows_opaque_token_and_rejects_unrelated_acl()
    {
        var calls = new List<Uri>();
        var http = new HttpClient(new Stub(req =>
        {
            var url = req.RequestUri!;
            calls.Add(url);
            if (url.AbsolutePath.EndsWith("/_apis/graph/groups"))
            {
                var result = Json("""{"value":[]}""");
                if (!url.Query.Contains("continuationToken"))
                    result.Headers.Add("X-MS-ContinuationToken", "opaque/next+token");
                return result;
            }
            if (url.AbsolutePath.Contains("/_apis/accesscontrollists/"))
                return Json("""{"value":[{"token":"another-collection","inheritPermissions":true,"acesDictionary":{}}]}""");
            throw new Xunit.Sdk.XunitException("Unexpected endpoint");
        }));
        var project = new GitHubMigrator.Domain.SourceProject(Project, "Alpha",
            [new(Repository, Project, "Repo", GitHubMigrator.Domain.VersionControlKind.Git)]);
        var evidence = await new AzureDevOpsSecurityReader(http)
            .ReadAsync("my-org", "hidden-pat", [project], CancellationToken.None);
        Assert.True(evidence.Coverage.GroupsComplete);
        Assert.False(evidence.Coverage.AclsCollected);
        Assert.Contains(evidence.Issues, x => x.Code == "RepositoryAclUnavailable:UnexpectedAclToken");
        Assert.Contains(calls, x => x.Query.Contains("continuationToken=opaque%2Fnext%2Btoken"));
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
}
