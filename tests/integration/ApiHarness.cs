using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.AzureDevOps;
using GitHubMigrator.Infrastructure.GitHub;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// A full TestServer host: production routes, JWT, authorization, EF Core, Npgsql,
// AES-GCM secret store and production REST adapters. Only third-party HTTP is mocked.
public sealed class ApiHarness : IAsyncLifetime
{
    public const string AdminPassword = "Admin-Test#1234";
    public const string UserPassword = "User-Test#1234";
    public const string PendingPassword = "Pending-Test#1234";
    public const string SuperadminPassword = "Superadmin-Test#1234";
    public const string SourceToken = "AZDO-INTEGRATION-PAT-DO-NOT-LEAK";
    public const string TargetToken = "GITHUB-INTEGRATION-PAT-DO-NOT-LEAK";
    public const string SourceValue = "SENSITIVE_SOURCE_VALUE_DO_NOT_LEAK";
    public const string Sha = "aaaabbbbccccddddeeeeffff0000111122223333";

    // Opt in only for dedicated API authorization tests. The default
    // fixture must continue exercising the production default OFF.
    public bool EnableCheckpointHumanReview { get; set; }
    public bool EnableCheckpointTargetReadPreflight { get; set; }
    public bool EnableCheckpointTargetObservation { get; set; }
    public bool EnableLaterCheckpointAuditRead { get; set; }
    public bool EnableLaterCheckpointWorkpack { get; set; }
    public bool EnableLaterCheckpointHistory { get; set; }
    public bool EnableLaterCheckpointBundleRead { get; set; }
    public bool EnableLaterCheckpointBundleLease { get; set; }
    public bool EnableLaterCheckpointLocalBundle { get; set; }
    public bool EnableLaterCheckpointDurableBundle { get; set; }
    public string? TrustedLocalBundleSourceRoot { get; set; }
    public string? TrustedLocalBundleOutputRoot { get; set; }
    public int TargetObjectIdLength { get; set; } = 40;
    // Fake fixed-host provider fault injection; no real outbound network.
    public string TargetObservationFault { get; set; } = "";

    public string AdminName { get; } = "admin-" + Guid.NewGuid().ToString("N");
    public string UserName { get; } = "user-" + Guid.NewGuid().ToString("N");
    public string PendingName { get; } = "pending-" + Guid.NewGuid().ToString("N");
    public string SuperadminName { get; } = "reviewer-" + Guid.NewGuid().ToString("N");
    public Guid SourceConnectionId { get; } = Guid.NewGuid();
    public Guid DestinationConnectionId { get; } = Guid.NewGuid();
    public ConcurrentQueue<(string Method, Uri Url, string? AuthScheme)> Calls { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public WebApplicationFactory<Program> Host { get; private set; } = null!;
    private readonly Dictionary<string, string?> _savedEnvironment = new();
    private readonly Dictionary<string, string> _issuedTokens = new();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("GITHUBMIGRATOR_TEST_POSTGRES")
            ?? throw new InvalidOperationException("GITHUBMIGRATOR_TEST_POSTGRES required; no InMemory fallback.");
        // Minimal-hosting WebApplicationFactory applies ConfigureAppConfiguration after the
        // top-level Program reads some settings. Install isolated process environment values
        // BEFORE starting TestServer; restore all of them when the fixture is disposed.
        var values = new Dictionary<string, string>
        {
            ["ConnectionStrings__PostgreSql"] = connection,
            ["Jwt__SigningKey"] = "integration-testing-only-key-with-48-characters-2026",
            ["Bootstrap__Username"] = "bootstrap-" + Guid.NewGuid().ToString("N"),
            ["Bootstrap__Password"] = "Bootstrap-Strong#2026",
            ["Secrets__Provider"] = "EncryptedPostgreSql",
            ["Secrets__LocalEncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Observability__Provider"] = "None",
            // Integration requests control when the worker processes a job.
            ["Jobs__Enabled"] = "false",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development"
        };
        if (EnableCheckpointHumanReview)
        {
            values["GitCheckpointTargetHumanReview__Enabled"] = "true";
            values["GitCheckpointTargetReReview__Enabled"] = "true";
            values["GitHubSourceCheckpointGitRefLedger__Enabled"] = "true";
            values["GitHubSourceSyncCheckpoints__Enabled"] = "true";
        }
        if (EnableCheckpointTargetReadPreflight)
        {
            values["GitCheckpointTargetReadPreflight__Enabled"] = "true";
            values["GitCheckpointTargetHumanReview__Enabled"] = "true";
            values["GitCheckpointTargetReReview__Enabled"] = "true";
            values["GitHubSourceCheckpointGitRefLedger__Enabled"] = "true";
            values["GitHubSourceSyncCheckpoints__Enabled"] = "true";
        }
        if (EnableLaterCheckpointAuditRead)
            values["GitIncrementalLaterCheckpointAudit__Enabled"] = "true";
        if (EnableLaterCheckpointWorkpack)
            values["GitIncrementalLaterCheckpointWorkpack__Enabled"] = "true";
        if (EnableLaterCheckpointHistory)
            values["GitIncrementalLaterCheckpointHistory__Enabled"] = "true";
        if (EnableLaterCheckpointBundleRead)
            values["GitIncrementalLaterCheckpointBundleRead__Enabled"] = "true";
        if (EnableLaterCheckpointBundleLease)
            values["GitIncrementalLaterCheckpointBundleLease__Enabled"] = "true";
        if (EnableLaterCheckpointLocalBundle)
            values["GitIncrementalLaterCheckpointLocalBundle__Enabled"] = "true";
        if (EnableLaterCheckpointDurableBundle)
            values["GitIncrementalLaterCheckpointDurableBundle__Enabled"] = "true";
        if (TrustedLocalBundleSourceRoot is not null &&
            TrustedLocalBundleOutputRoot is not null)
        {
            values["GitSourceBundles__Enabled"] = "true";
            values["GitSourceBundles__SourceRoot"] = TrustedLocalBundleSourceRoot;
            values["GitSourceBundles__BundleRoot"] = TrustedLocalBundleOutputRoot;
        }
        if (EnableCheckpointTargetObservation)
        {
            values["GitCheckpointTargetObservation__Enabled"] = "true";
            values["GitCheckpointTargetReadPreflight__Enabled"] = "true";
            values["GitCheckpointTargetHumanReview__Enabled"] = "true";
            values["GitCheckpointTargetReReview__Enabled"] = "true";
            values["GitHubSourceCheckpointGitRefLedger__Enabled"] = "true";
            values["GitHubSourceSyncCheckpoints__Enabled"] = "true";
        }
        foreach (var pair in values)
        {
            _savedEnvironment[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        var azure = new WireStub(request => ReplyAzure(request), Calls);
        var github = new WireStub(request => ReplyGitHub(request), Calls);
        Host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                // App constructs the actual typed readers; their outbound transport is a strict stub.
                services.AddHttpClient<AzureDevOpsWorkloadReader>()
                    .ConfigurePrimaryHttpMessageHandler(() => azure);
                services.AddHttpClient<AzureDevOpsDiscoveryReader>()
                    .ConfigurePrimaryHttpMessageHandler(() => azure);
                services.AddHttpClient<GitHubConfigurationReader>()
                    .ConfigurePrimaryHttpMessageHandler(() => github);
                services.AddHttpClient<GitHubTraceabilityReader>()
                    .ConfigurePrimaryHttpMessageHandler(() => github);
                services.AddHttpClient<GitHubTargetRepositoryProbe>()
                    .ConfigurePrimaryHttpMessageHandler(() => github);
                services.AddHttpClient<GitHubRefObservationReader>()
                    .ConfigurePrimaryHttpMessageHandler(() => github);
                services.AddHttpClient<GitHubImportRepositoryReader>()
                    .ConfigurePrimaryHttpMessageHandler(() => github);
                services.AddHttpClient<GitHubDestinationAccessReader>()
                    .ConfigurePrimaryHttpMessageHandler(() => github);
            });
        });
        Client = Host.CreateClient();
        await using var scope = Host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await AddUserAsync(users, AdminName, AdminPassword, "admin", false);
        await AddUserAsync(users, UserName, UserPassword, "user", false);
        await AddUserAsync(users, PendingName, PendingPassword, "admin", true);
        await AddUserAsync(users, SuperadminName, SuperadminPassword, "superadmin", false);
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionRepository>();
        var secrets = scope.ServiceProvider.GetRequiredService<IConnectionSecretStore>();
        var now = DateTimeOffset.UtcNow;
        await secrets.SaveAsync("mock-api-source-" + SourceConnectionId.ToString("N"), SourceToken, default);
        await secrets.SaveAsync("mock-api-target-" + DestinationConnectionId.ToString("N"), TargetToken, default);
        if (EnableCheckpointTargetObservation)
        {
            // Independent real two-epoch F-029 test fixtures have a
            // PostgreSQL-owned destination connection named "dest-secret".
            // Only this explicit test host supplies its encrypted credential.
            // The target-ref endpoint still has NO caller PAT or URL.
            await secrets.SaveAsync("dest-secret", TargetToken, default);
        }
        await connections.SaveAsync(new(SourceConnectionId, "Source Fixture", "azure-devops-services",
            ConnectionDirection.Source, "az-org", "mock-api-source-" + SourceConnectionId.ToString("N"),
            now, now, "Verified", "test", "not_verified"), default);
        await connections.SaveAsync(new(DestinationConnectionId, "Target Fixture", "github-enterprise-cloud",
            ConnectionDirection.Destination, "gh-org", "mock-api-target-" + DestinationConnectionId.ToString("N"),
            now, now, "Verified", "test", "not_verified"), default);
    }

    private static async Task AddUserAsync(UserManager<ApplicationUser> users, string name,
        string password, string role, bool pending)
    {
        var user = new ApplicationUser { UserName = name, MustChangePassword = pending };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(x => x.Code)));
        var added = await users.AddToRoleAsync(user, role);
        Assert.True(added.Succeeded, string.Join(", ", added.Errors.Select(x => x.Code)));
    }

    public async Task<string> LoginAsync(string name, string password)
    {
        // Reuse each real-login token across independent endpoint checks. The API login
        // limiter is intentionally only 8/minute; tests must not inadvertently exhaust it.
        if (_issuedTokens.TryGetValue(name, out var existing)) return existing;
        using var response = await Client.PostAsJsonAsync("/api/auth/login", new { username = name, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("accessToken").GetString()!;
        _issuedTokens[name] = token;
        return token;
    }

    private static HttpResponseMessage ReplyAzure(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        // F-008 integration: actual Azure DevOps discovery reader, fixed-host
        // paginated GETs, Graph users/groups and raw security evidence.
        if (path == "/az-org/_apis/projects")
            return Json("""{"value":[{"id":"project","name":"Project"}]}""");
        if (path == "/az-org/_apis/projects/project")
            return Json("""{"capabilities":{"versioncontrol":{"sourceControlType":"Git"}}}""");
        if (path == "/az-org/project/_apis/git/repositories")
            return Json("""{"value":[{"id":"source-git","name":"Repo"}]}""");
        if (path == "/az-org/project/_apis/git/repositories/source-git/refs")
            return Json("""{"value":[{"name":"refs/heads/main","objectId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""");
        if (path == "/az-org/_apis/graph/users")
            return Json("""{"value":[{"descriptor":"aad.test","displayName":"Test User","mailAddress":"test@example.invalid"}]}""");
        if (path == "/az-org/_apis/graph/groups")
            return Json("""{"value":[]}""");
        if (path.StartsWith("/az-org/_apis/accesscontrollists/", StringComparison.Ordinal))
            return Json("""{"value":[]}""");
        if (path.EndsWith("/_apis/pipelines/43", StringComparison.Ordinal))
            return new(HttpStatusCode.Forbidden);
        if (path.EndsWith("/_apis/pipelines/42", StringComparison.Ordinal))
            return Json("""{"id":42,"name":"Release Safe","configuration":{"type":"yaml","path":"/azure-pipelines.yml","repository":{"id":"source-git"}}}""");
        if (path.EndsWith("/_apis/build/definitions/42", StringComparison.Ordinal))
            return Json(JsonSerializer.Serialize(new
            {
                variableGroups = new[] { 7 },
                variables = new Dictionary<string, object>
                {
                    ["BUILD_REGION"] = new { value = SourceValue, isSecret = false },
                    ["DEPLOY_PASSWORD"] = new { value = SourceValue, isSecret = true }
                }
            }));
        if (path.EndsWith("/_apis/distributedtask/variablegroups/7", StringComparison.Ordinal))
            return Json("""{"name":"prod","variables":{"APP_TOKEN":{"isSecret":true,"value":"DONT_SHOW_GROUP_TOKEN"}}}""");
        if (path.EndsWith("/_apis/serviceendpoint/endpoints", StringComparison.Ordinal))
            return Json("""{"value":[{"name":"Azure subscription","type":"azurerm","authorization":{"parameters":{"serviceprincipalkey":"NEVER_SHOW_SERVICE_PASSWORD"}}}]}""");
        if (path.EndsWith("/_apis/git/repositories/source-git/items", StringComparison.Ordinal))
            return Json(JsonSerializer.Serialize(new
            {
                content = """
                    steps:
                    - task: UseDotNet@2
                      inputs:
                        password: DONT_SHOW_YAML_PASSWORD
                    - bash: |
                        echo DONT_SHOW_YAML_PASSWORD
                    """
            }));
        if (path.EndsWith("/_apis/wit/workitems", StringComparison.Ordinal))
        {
            var ids = req.RequestUri.Query;
            if (!ids.Contains("101", StringComparison.Ordinal))
                return Json("""{"value":[]}""");
            return Json(JsonSerializer.Serialize(new
            {
                value = new[] {
                    new {
                        id = 101,
                        fields = new Dictionary<string, object> {
                            ["System.Title"] = "Update build",
                            ["System.WorkItemType"] = "Bug",
                            ["System.State"] = "Active",
                            ["System.Description"] = "<b>Fix a link</b>"
                        },
                        relations = new[] {
                            new { rel = "System.LinkTypes.Hierarchy-Forward",
                                url = "https://dev.azure.com/az-org/_apis/wit/workitems/102" },
                            new { rel = "ArtifactLink", url = "vstfs:///Git/Commit/project/source-git/" + Sha },
                            new { rel = "ArtifactLink", url = "vstfs:///Git/PullRequestId/project/source-git/301" }
                        }
                    }
                }
            }));
        }
        throw new InvalidOperationException("Unmocked Azure route: " + path);
    }

    private HttpResponseMessage ReplyGitHub(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/actions/secrets", StringComparison.Ordinal))
            return Json("""{"total_count":1,"secrets":[{"name":"APP_TOKEN"}]}""");
        if (path.EndsWith("/actions/variables", StringComparison.Ordinal))
            return Json("""{"total_count":1,"variables":[{"name":"BUILD_REGION"}]}""");
        if (path.EndsWith("/issues/55", StringComparison.Ordinal))
            return Json("""{"number":55,"title":"Migrated work item"}""");
        if (path.EndsWith("/commits/" + Sha, StringComparison.Ordinal))
            return Json(JsonSerializer.Serialize(new { sha = Sha }));
        if (path.EndsWith("/pulls/9", StringComparison.Ordinal))
            return new(HttpStatusCode.NotFound);
        if (path == "/user/memberships/orgs/gh-org")
            return Json("""{"state":"active","role":"admin","user":{"login":"test"},"organization":{"login":"gh-org"}}""");
        if (path == "/orgs/gh-org")
            return Json("""{"login":"gh-org","members_can_create_private_repositories":true}""");
        if (path == "/repos/gh-org/migration-one/git/matching-refs/heads")
        {
            if (TargetObservationFault == "hidden")
                return new(HttpStatusCode.NotFound);
            if (TargetObservationFault == "denied")
                return new(HttpStatusCode.Forbidden);
            if (TargetObservationFault == "rate-limited")
                return new(HttpStatusCode.TooManyRequests);
            if (TargetObservationFault == "redirect")
                return new(HttpStatusCode.Redirect) {
                    Headers = { Location = new Uri("https://outside.invalid/secret") }
                };
            if (TargetObservationFault == "malformed")
                return Json("""{"unexpected":"response"}""");
            var result = Json(JsonSerializer.Serialize(new[] {
                new { @ref = "refs/heads/main",
                    @object = new {
                        sha = new string('c', TargetObservationFault == "wrong-oid"
                            ? (TargetObjectIdLength == 40 ? 64 : 40)
                            : TargetObjectIdLength),
                        type = "commit"
                    } }
            }));
            if (TargetObservationFault == "partial")
                result.Headers.TryAddWithoutValidation(
                    "Link", "<https://api.github.com/next>; rel=\"next\"");
            return result;
        }
        if (path == "/repos/gh-org/migration-one/git/matching-refs/tags")
            return Json(JsonSerializer.Serialize(new[] {
                new { @ref = "refs/tags/v1",
                    @object = new {
                        sha = new string('d', TargetObjectIdLength),
                        type = "tag"
                    } }
            }));
        if (path == "/repos/gh-org/occupied/git/matching-refs/heads")
            return Json(JsonSerializer.Serialize(new[] {
                new { @ref = "refs/heads/main",
                    @object = new { sha = Sha, type = "commit" } }
            }));
        if (path == "/repos/gh-org/occupied/git/matching-refs/tags")
            return Json(JsonSerializer.Serialize(new[] {
                new { @ref = "refs/tags/v1",
                    @object = new { sha = Sha, type = "tag" } }
            }));
        if (path == "/repos/gh-org/occupied")
            return Json("""{"full_name":"gh-org/occupied","owner":{"login":"gh-org"},"archived":false,"disabled":false,"permissions":{"push":true}}""");
        if (path == "/repos/gh-org/hidden")
            return new(HttpStatusCode.NotFound);
        if (path == "/repos/gh-org/denied")
            return new(HttpStatusCode.Forbidden);
        throw new InvalidOperationException("Unmocked GitHub route: " + path);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public Task DisposeAsync()
    {
        Client?.Dispose();
        Host?.Dispose();
        foreach (var pair in _savedEnvironment)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        return Task.CompletedTask;
    }

    private sealed class WireStub(Func<HttpRequestMessage, HttpResponseMessage> responder,
        ConcurrentQueue<(string Method, Uri Url, string? AuthScheme)> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains(request.RequestUri!.Host,
                new[] { "dev.azure.com", "vssps.dev.azure.com", "api.github.com" });
            calls.Enqueue((request.Method.Method, request.RequestUri, request.Headers.Authorization?.Scheme));
            return Task.FromResult(responder(request));
        }
    }
}
