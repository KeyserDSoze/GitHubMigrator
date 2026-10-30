using System.Net;
using System.Net.Http;
using System.Text;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

public sealed class BoardReconciliationTests
{
    private const string Sha = "aaaabbbbccccddddeeeeffff0000111122223333";
    private readonly MigrationConnection source = MakeConnection("azure-devops-services", ConnectionDirection.Source, "azure-org");
    private readonly MigrationConnection target = MakeConnection("github-enterprise-cloud", ConnectionDirection.Destination, "github-org");

    [Fact]
    public async Task Reconciles_each_source_link_against_proven_target_entity_and_reports_gaps()
    {
        var github = new FakeTraceability();
        var svc = Service(new StubBoards(), github);
        var result = await svc.ReconcileAsync(source.Id, target.Id, "project", "repo", [101],
            new Dictionary<int, int> { [102] = 88 },
            new Dictionary<string, string> { [Sha] = Sha },
            new Dictionary<int, int> { [301] = 22 }, CancellationToken.None);
        Assert.Equal(4, result.Links.Count);
        Assert.Contains(result.Links, x => x.WorkItemId == 101 &&
            x.SourceRelation == "System.LinkTypes.Hierarchy-Forward" &&
            x.TargetReference == "#88" && x.Status == TraceabilityStatus.Verified);
        Assert.Contains(result.Links, x => x.SourceRelation == "commit" &&
            x.TargetReference == Sha && x.Status == TraceabilityStatus.Verified);
        Assert.Contains(result.Links, x => x.SourceRelation == "pull-request" &&
            x.TargetReference == "#22" && x.Status == TraceabilityStatus.NotFound);
        Assert.Contains(result.Links, x => x.Status == TraceabilityStatus.Unsupported);
        Assert.Equal(2, result.VerifiedLinks);
        Assert.Equal(2, result.UnresolvedLinks);
        Assert.False(result.ReadyToPublish);
        Assert.Contains(result.Issues, x => x.Code == "UnresolvedTraceability");
        Assert.Contains(result.Issues, x => x.Code == "CoverageNotGuaranteed");
        Assert.Equal(1, github.Issues);
        Assert.Equal(1, github.Commits);
        Assert.Equal(1, github.Prs);
    }

    [Fact]
    public async Task Missing_mapping_does_not_probe_github_or_invent_issue_numbers()
    {
        var github = new FakeTraceability();
        var result = await Service(new StubBoards(), github).ReconcileAsync(
            source.Id, target.Id, "project", "repo", [101], null, null, null, CancellationToken.None);
        Assert.Contains(result.Links, x => x.SourceRelation == "System.LinkTypes.Hierarchy-Forward" &&
            x.Status == TraceabilityStatus.MissingMapping && x.TargetReference is null);
        Assert.Contains(result.Links, x => x.SourceRelation == "pull-request" &&
            x.Status == TraceabilityStatus.MissingMapping);
        Assert.Equal(0, github.Issues);
        Assert.Equal(1, github.Commits);
        Assert.Equal(0, github.Prs);
    }

    [Fact]
    public async Task Rejects_invalid_id_batches_and_target_repository_without_reaching_providers()
    {
        var github = new FakeTraceability();
        var svc = Service(new StubBoards(), github);
        await Assert.ThrowsAsync<WorkloadInputException>(() =>
            svc.ReconcileAsync(source.Id, target.Id, "project", "../oops", [101], null, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<WorkloadInputException>(() =>
            svc.ReconcileAsync(source.Id, target.Id, "project", "repo", [101, 101], null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Github_lookups_differentiate_404_from_200_and_never_return_pull_requests_as_issues()
    {
        var requests = new List<string>();
        var http = new HttpClient(new StubHttp(req =>
        {
            requests.Add(req.RequestUri!.ToString());
            Assert.Equal("api.github.com", req.RequestUri.Host);
            Assert.DoesNotContain("private-token", req.RequestUri.ToString());
            if (req.RequestUri.AbsolutePath.EndsWith("/issues/33"))
                return Json("""{"number":33,"pull_request":{"url":"ignored"}}""");
            if (req.RequestUri.AbsolutePath.EndsWith("/commits/aaaabbbbccccddddeeeeffff0000111122223333"))
                return Json("""{"sha":"aaaabbbbccccddddeeeeffff0000111122223333"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var reader = new GitHubTraceabilityReader(http);
        Assert.False(await reader.IssueExistsAsync("org", "repo", 33, "private-token", CancellationToken.None));
        Assert.True(await reader.CommitExistsAsync("org", "repo", Sha, "private-token", CancellationToken.None));
        Assert.False(await reader.PullRequestExistsAsync("org", "repo", 7, "private-token", CancellationToken.None));
        Assert.Equal(3, requests.Count);
    }

    private BoardReconciliationService Service(IAzureWorkloadReader reader, IGitHubTraceabilityReader github) =>
        new(new Connections([source, target]), new Secrets(), reader, github);

    private static MigrationConnection MakeConnection(string provider, ConnectionDirection direction, string org)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "test", provider, direction, org, "secret-reference",
            now, now, "Verified", null, "not_verified");
    }

    private sealed class FakeTraceability : IGitHubTraceabilityReader
    {
        public int Issues, Commits, Prs;
        public Task<bool> IssueExistsAsync(string org, string repo, int number, string token, CancellationToken ct)
        { Issues++; return Task.FromResult(number == 88); }
        public Task<bool> CommitExistsAsync(string org, string repo, string sha, string token, CancellationToken ct)
        { Commits++; return Task.FromResult(sha == Sha); }
        public Task<bool> PullRequestExistsAsync(string org, string repo, int number, string token, CancellationToken ct)
        { Prs++; return Task.FromResult(false); }
    }

    private sealed class StubBoards : IAzureWorkloadReader
    {
        public Task<AzurePipelineSource> GetPipelineAsync(string org, string project, int id, string token, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AzureBoardItem>> GetBoardItemsAsync(string org, string project,
            IReadOnlyList<int> ids, string token, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AzureBoardItem>>([
                new AzureBoardItem(101, "Fix access", "Bug", "Active", "A description", [
                    new("work-item", "https://dev.azure.com/org/_apis/wit/workitems/102", 102,
                        "System.LinkTypes.Hierarchy-Forward"),
                    new("commit", "vstfs:///Git/Commit/proj/repo/" + Sha),
                    new("pull-request", "vstfs:///Git/PullRequestId/proj/repo/301"),
                    new("unknown", "legacy-link")
                ])
            ]);
    }

    private sealed class Connections(IReadOnlyList<MigrationConnection> items) : IConnectionRepository
    {
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(items.FirstOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) => Task.FromResult(items);
        public Task SaveAsync(MigrationConnection connection, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Secrets : IConnectionSecretStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken ct) =>
            Task.FromResult<string?>("private-token");
        public Task SaveAsync(string reference, string value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string reference, CancellationToken ct) => Task.CompletedTask;
    }
    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(fn(request));
    }
}
