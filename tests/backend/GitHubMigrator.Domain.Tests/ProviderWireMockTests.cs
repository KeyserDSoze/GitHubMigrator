using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.AzureDevOps;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests;

// Contract-style tests: REAL provider adapters and REAL business orchestration;
// only the outbound HTTP transport and external AI service are simulated.
public sealed class ProviderWireMockTests
{
    private const string Sha = "aaaabbbbccccddddeeeeffff0000111122223333";
    private const string Pat = "AZDO_PRIVATE_PAT_NEVER_ECHO";
    private const string GhPat = "GITHUB_PRIVATE_PAT_NEVER_ECHO";
    private const string InlineSecret = "INLINE_PASSWORD_SHOULD_NOT_LEAK";
    private const string SourceValue = "SOURCE_VARIABLE_VALUE_SHOULD_NOT_LEAK";
    private readonly MigrationConnection _azure = NewConnection("azure-devops-services", ConnectionDirection.Source, "az-org", "src-secret");
    private readonly MigrationConnection _github = NewConnection("github-enterprise-cloud", ConnectionDirection.Destination, "gh-org", "dst-secret");

    [Fact]
    public async Task Full_pipeline_HTTP_to_business_preview_preserves_sanitized_tasks_and_omits_secrets()
    {
        var azure = AzureMock();
        var github = GitHubMock();
        var ai = new RecordingAi();
        var service = Service(azure, github, ai);
        var result = await service.PreviewPipelineAsync(_azure.Id, _github.Id, "project", 42, "destination", CancellationToken.None);
        Assert.Equal(".github/workflows/azdo-42-release-api.yml", result.WorkflowPath);
        Assert.Contains("REVIEW 1: UseDotNet@2", result.WorkflowYaml);
        Assert.Contains("REVIEW 2: bash", result.WorkflowYaml);
        Assert.Contains("REVIEW 3: external-template", result.WorkflowYaml);
        Assert.Contains("if: ${{ false }}", result.WorkflowYaml);
        Assert.Equal(3, result.Issues.Count(x => x.Code == "TaskTranslationPending"));
        Assert.Equal("mock-reviewed", result.Ai.Status);
        Assert.False(result.DeployableWithoutReview);
        Assert.Contains(result.SetupSteps, s => s.Name == "AZURE_CLIENT_ID" && s.Kind == "secret" && s.ExistsOnGitHub);
        Assert.Contains(result.SetupSteps, s => s.Name == "DEPLOY_TOKEN" && s.Kind == "secret" && !s.ExistsOnGitHub);
        Assert.Contains(result.SetupSteps, s => s.Name == "BUILD_FLAVOR" && s.Kind == "variable" && s.ExistsOnGitHub);
        Assert.Contains(result.SetupSteps, s => s.Kind == "candidate-service-connection");
        Assert.Contains(result.Issues, x => x.Code == "ServiceConnectionUsageUnverified");
        Assert.Contains(result.Issues, x => x.Code == "ConfigurationMissing");
        var everything = JsonSerializer.Serialize(result) + ai.Summary + ai.Workflow + ai.StepSummary;
        foreach (var forbidden in new[] { Pat, GhPat, InlineSecret, SourceValue, "NEVER_REVEAL_GROUP_VALUE" })
            Assert.DoesNotContain(forbidden, everything);
        Assert.All(azure.Requests.Concat(github.Requests), req => Assert.Equal("GET", req.Method));
        Assert.All(azure.Requests, req => Assert.Equal("dev.azure.com", req.Uri.Host));
        Assert.All(github.Requests, req => Assert.Equal("api.github.com", req.Uri.Host));
        Assert.DoesNotContain(azure.Requests.Concat(github.Requests),
            r => r.Uri.ToString().Contains(Pat) || r.Uri.ToString().Contains(GhPat));
    }

    [Fact]
    public async Task Source_YAML_missing_yields_blocked_scaffold_without_fabricating_task_translation()
    {
        var azure = AzureMock(yamlStatus: HttpStatusCode.NotFound);
        var result = await Service(azure, GitHubMock(), new RecordingAi()).PreviewPipelineAsync(
            _azure.Id, _github.Id, "project", 42, "destination", CancellationToken.None);
        Assert.Contains(result.Issues, x => x.Code == "SourceYamlUnavailable");
        Assert.DoesNotContain(result.Issues, x => x.Code == "TaskTranslationPending");
        Assert.Contains("if: ${{ false }}", result.WorkflowYaml);
    }

    [Fact]
    public async Task Unavailable_GitHub_configuration_is_not_reported_as_an_empty_verified_inventory()
    {
        var github = GitHubMock(secretsStatus: HttpStatusCode.Forbidden);
        var result = await Service(AzureMock(), github, new RecordingAi()).PreviewPipelineAsync(
            _azure.Id, _github.Id, "project", 42, "destination", CancellationToken.None);
        Assert.Contains(result.Issues, x => x.Code == "GitHubConfigUnreadable" && x.Severity == "blocker");
        Assert.All(result.SetupSteps.Where(x => x.Kind == "secret" || x.Kind == "variable"), x => Assert.False(x.ExistsOnGitHub));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Azure_pipeline_metadata_failure_never_returns_a_success_preview(HttpStatusCode code)
    {
        var azure = AzureMock(pipelineStatus: code);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Service(azure, GitHubMock(), new RecordingAi()).PreviewPipelineAsync(
                _azure.Id, _github.Id, "project", 42, "destination", CancellationToken.None));
    }

    [Fact]
    public async Task Adapters_do_not_read_or_write_credentials_as_parameters_in_HTTP_URL()
    {
        var azure = AzureMock();
        var reader = new AzureDevOpsWorkloadReader(new HttpClient(azure));
        var result = await reader.GetPipelineAsync("az-org", "project", 42, Pat, CancellationToken.None);
        Assert.All(azure.Requests, x => Assert.Equal("Basic", x.AuthorizationScheme));
        Assert.Equal(2, result.Variables.Count(x => x.Group == "pipeline"));
        Assert.Equal("available", result.SourceYaml);
        Assert.Equal(["UseDotNet@2", "bash", "external-template"],
            result.Structure!.Tasks.Select(x => x.Identifier).ToArray());
        Assert.DoesNotContain(SourceValue, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(InlineSecret, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(Pat, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Target_config_reader_rejects_non_success_and_redirects(HttpStatusCode code)
    {
        var mock = GitHubMock(secretsStatus: code);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubConfigurationReader(new HttpClient(mock)).GetNamesAsync("gh-org", "destination", GhPat, CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_secrets_are_paginated_without_exposing_or_reading_secret_values()
    {
        var mock = new MockHttp(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/secrets"))
            {
                if (req.RequestUri.Query.Contains("page=1"))
                    return Json(JsonSerializer.Serialize(new { total_count = 101,
                        secrets = Enumerable.Range(0, 100).Select(i => new { name = "KEY_" + i }).ToArray() }));
                return Json("""{"total_count":101,"secrets":[{"name":"KEY_100"}]}""");
            }
            if (req.RequestUri.AbsolutePath.EndsWith("/variables"))
                return Json("""{"total_count":1,"variables":[{"name":"REGION"}]}""");
            throw new InvalidOperationException("Unexpected provider HTTP route");
        });
        var result = await new GitHubConfigurationReader(new HttpClient(mock))
            .GetNamesAsync("gh-org", "destination", GhPat, CancellationToken.None);
        Assert.Equal(101, result.Secrets.Count);
        Assert.Contains("KEY_100", result.Secrets);
        Assert.Equal("REGION", Assert.Single(result.Variables));
        Assert.Equal(3, mock.Requests.Count);
    }

    [Fact]
    public async Task GitHub_truncated_paging_fails_closed_instead_of_claiming_missing_secrets()
    {
        var mock = new MockHttp(req => Json(
            req.RequestUri!.AbsolutePath.EndsWith("/secrets")
                ? JsonSerializer.Serialize(new { total_count = 1000,
                    secrets = Enumerable.Range(0, 100).Select(i => new { name = "KEY_" + i }) })
                : """{"total_count":0,"variables":[]}"""));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubConfigurationReader(new HttpClient(mock))
                .GetNamesAsync("gh-org", "destination", GhPat, CancellationToken.None));
        Assert.Equal(5, mock.Requests.Count);
    }

    [Fact]
    public async Task Azure_Boards_API_and_GitHub_probes_reconcile_real_relation_payloads_by_ID()
    {
        var azure = AzureMock();
        var github = GitHubMock();
        var reconciler = new BoardReconciliationService(new FakeConnections([_azure, _github]),
            new FakeSecrets(), new AzureDevOpsWorkloadReader(new HttpClient(azure)),
            new GitHubTraceabilityReader(new HttpClient(github)));
        var output = await reconciler.ReconcileAsync(_azure.Id, _github.Id, "project", "destination",
            [101], new Dictionary<int, int> { [102] = 88 },
            new Dictionary<string, string> { [Sha] = Sha },
            new Dictionary<int, int> { [301] = 21 }, CancellationToken.None);
        Assert.Equal(3, output.Links.Count);
        Assert.Equal(2, output.TargetObjectsFound);
        Assert.Equal(1, output.UnresolvedLinks);
        Assert.Contains(output.Links, x => x.SourceRelation == "System.LinkTypes.Hierarchy-Forward" &&
            x.TargetReference == "#88" && x.Status == TraceabilityStatus.TargetExists);
        Assert.Contains(output.Links, x => x.SourceRelation == "commit" &&
            x.TargetReference == Sha && x.Status == TraceabilityStatus.TargetExists);
        Assert.Contains(output.Links, x => x.SourceRelation == "pull-request" &&
            x.TargetReference == "#21" && x.Status == TraceabilityStatus.NotFound);
        Assert.Contains(output.Issues, x => x.Code == "CoverageNotGuaranteed");
        Assert.False(output.ReadyToPublish);
        Assert.All(azure.Requests.Concat(github.Requests), x => Assert.Equal("GET", x.Method));
    }

    [Fact]
    public async Task Missing_Board_record_is_an_explicit_blocking_issue_without_GitHub_writes()
    {
        var azure = AzureMock();
        var github = GitHubMock();
        var output = await new BoardReconciliationService(new FakeConnections([_azure, _github]),
            new FakeSecrets(), new AzureDevOpsWorkloadReader(new HttpClient(azure)),
            new GitHubTraceabilityReader(new HttpClient(github)))
            .ReconcileAsync(_azure.Id, _github.Id, "project", "destination", [101, 999],
                null, null, null, CancellationToken.None);
        Assert.Contains(output.Links, x => x.WorkItemId == 999 && x.Status == TraceabilityStatus.NotFound);
        Assert.DoesNotContain(github.Requests, x => x.Method != "GET");
    }

    [Fact]
    public async Task Target_403_on_one_link_is_unavailable_not_not_found()
    {
        var github = GitHubMock(commitStatus: HttpStatusCode.Forbidden);
        var output = await new BoardReconciliationService(new FakeConnections([_azure, _github]),
            new FakeSecrets(), new AzureDevOpsWorkloadReader(new HttpClient(AzureMock())),
            new GitHubTraceabilityReader(new HttpClient(github)))
            .ReconcileAsync(_azure.Id, _github.Id, "project", "destination", [101], null, null, null, CancellationToken.None);
        Assert.Contains(output.Links, x => x.SourceRelation == "commit" && x.Status == TraceabilityStatus.Unavailable);
    }

    [Theory]
    [InlineData("https://evil.example/redirect")]
    [InlineData("https://api.github.com/other-route")]
    public async Task Azure_HTTP_redirect_is_not_followed(string location)
    {
        var azure = AzureMock(pipelineStatus: HttpStatusCode.Found);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AzureDevOpsWorkloadReader(new HttpClient(azure))
                .GetPipelineAsync("az-org", "project", 42, Pat, CancellationToken.None));
        Assert.Single(azure.Requests);
        Assert.DoesNotContain(azure.Requests, x => x.Uri.Host == "evil.example");
    }

    private WorkloadPreviewService Service(MockHttp azure, MockHttp github, IPipelineAiReviewer ai) =>
        new(new FakeConnections([_azure, _github]), new FakeSecrets(),
            new AzureDevOpsWorkloadReader(new HttpClient(azure)),
            new GitHubConfigurationReader(new HttpClient(github)), ai);

    private static MigrationConnection NewConnection(string provider, ConnectionDirection kind,
        string org, string secret)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "mock-" + provider, provider, kind, org, secret,
            now, now, "Verified", null, "not_verified");
    }

    private static MockHttp AzureMock(HttpStatusCode? pipelineStatus = null,
        HttpStatusCode? yamlStatus = null) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/_apis/pipelines/42"))
            return pipelineStatus is not null
                ? new HttpResponseMessage(pipelineStatus.Value)
                : Json("""{"id":42,"name":"Release API","configuration":{"type":"yaml","path":"/azure-pipelines.yml","repository":{"id":"repo-1"}}}""");
        if (path.EndsWith("/_apis/build/definitions/42"))
            return Json(JsonSerializer.Serialize(new { repository = new { id = "repo-1" }, variableGroups = new[] { 8 },
                variables = new Dictionary<string, object> {
                    ["AZURE_CLIENT_ID"] = new { isSecret = true, value = SourceValue },
                    ["BUILD_FLAVOR"] = new { isSecret = false, value = SourceValue }
                }}));
        if (path.EndsWith("/_apis/distributedtask/variablegroups/8"))
            return Json("""{"name":"prod","variables":{"DEPLOY_TOKEN":{"isSecret":true,"value":"NEVER_REVEAL_GROUP_VALUE"}}}""");
        if (path.EndsWith("/_apis/serviceendpoint/endpoints"))
            return Json("""{"value":[{"name":"Azure subscription","type":"azurerm","authorization":{"parameters":{"serviceprincipalkey":"DO_NOT_RETURN"}}}]}""");
        if (path.EndsWith("/_apis/git/repositories/repo-1/items"))
            return yamlStatus is not null ? new HttpResponseMessage(yamlStatus.Value) :
                Json(JsonSerializer.Serialize(new { content = """
                    steps:
                    - task: UseDotNet@2
                      inputs:
                        password: INLINE_PASSWORD_SHOULD_NOT_LEAK
                    - bash: |
                        echo INLINE_PASSWORD_SHOULD_NOT_LEAK
                    - template: shared.yml@templates
                    """ }));
        if (path.EndsWith("/_apis/wit/workitems"))
            return Json(JsonSerializer.Serialize(new { value = new[] {
                new {
                    id = 101,
                    fields = new Dictionary<string, object> {
                        ["System.Title"] = "Fix issue",
                        ["System.WorkItemType"] = "Bug",
                        ["System.State"] = "Active",
                        ["System.Description"] = "<b>Original description</b>" },
                    relations = new[] {
                        new { rel = "System.LinkTypes.Hierarchy-Forward", url = "https://dev.azure.com/az-org/_apis/wit/workitems/102" },
                        new { rel = "ArtifactLink", url = "vstfs:///Git/Commit/project/repo-1/" + Sha },
                        new { rel = "ArtifactLink", url = "vstfs:///Git/PullRequestId/project/repo-1/301" }
                    }
                }
            }}));
        throw new InvalidOperationException("Unexpected Azure API call: " + path);
    });

    private static MockHttp GitHubMock(HttpStatusCode? secretsStatus = null,
        HttpStatusCode? commitStatus = null) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/actions/secrets"))
            return secretsStatus is not null ? new HttpResponseMessage(secretsStatus.Value)
                : Json("""{"total_count":1,"secrets":[{"name":"AZURE_CLIENT_ID"}]}""");
        if (path.EndsWith("/actions/variables"))
            return Json("""{"total_count":1,"variables":[{"name":"BUILD_FLAVOR"}]}""");
        if (path.EndsWith("/issues/88")) return Json("""{"number":88,"title":"Mapped issue"}""");
        if (path.EndsWith("/pulls/21")) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (path.EndsWith("/commits/" + Sha))
            return commitStatus is not null ? new HttpResponseMessage(commitStatus.Value) :
                Json(JsonSerializer.Serialize(new { sha = Sha }));
        throw new InvalidOperationException("Unexpected GitHub API call: " + path);
    });

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingAi : IPipelineAiReviewer
    {
        public string Summary = "", Workflow = "", StepSummary = "";
        public Task<AiReview> ReviewAsync(string summary, string workflow, IReadOnlyList<ConfigurationStep> steps,
            CancellationToken ct)
        {
            Summary = summary; Workflow = workflow; StepSummary = JsonSerializer.Serialize(steps);
            return Task.FromResult(new AiReview("mock-reviewed", ["Human approval required"]));
        }
    }
    private sealed record HttpRequestLog(string Method, Uri Uri, string? AuthorizationScheme);
    private sealed class MockHttp(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestLog> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.Scheme));
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains(request.RequestUri!.Host, new[] { "dev.azure.com", "api.github.com" });
            return Task.FromResult(reply(request));
        }
    }
    private sealed class FakeSecrets : IConnectionSecretStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken ct) =>
            Task.FromResult<string?>(reference == "src-secret" ? Pat : GhPat);
        public Task SaveAsync(string reference, string value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string reference, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class FakeConnections(IReadOnlyList<MigrationConnection> items) : IConnectionRepository
    {
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(items.FirstOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) => Task.FromResult(items);
        public Task SaveAsync(MigrationConnection value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }
}
