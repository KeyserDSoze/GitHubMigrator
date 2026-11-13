using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AdoSim;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.AzureDevOps;
using GitHubMigrator.Infrastructure.GitObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// tools/AdoSim as a stand-in for Azure DevOps SERVICES: organization-rooted URLs, Graph identities, api 7.1.
// The product reaches it only through Providers:AzureDevOpsServices (Development-only overrides).
public sealed class ServicesSimFixture : IAsyncLifetime
{
    public const string Pat = "sim-pat";
    private string _dir = "";
    public WebApplication App { get; private set; } = null!;
    public string Origin { get; private set; } = "";
    public JsonElement Expected { get; private set; }
    public AzureDevOpsServicesEndpoints Endpoints => new(Origin, Origin + "/vssps", Origin);

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adosim-services-" + Guid.NewGuid().ToString("N"));
        App = await AdoSimApp.StartAsync(new SimOptions(SeedSpec.Load(null), _dir, Pat, "7.1", 50, "http://127.0.0.1:0",
            SimFlavors.Services, GraphPageSize: 3));
        Origin = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient();
        Expected = JsonDocument.Parse(await http.GetStringAsync(Origin + "/_sim/expected")).RootElement.Clone();
    }

    public async Task DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        GitTestSupport.DeleteQuietly(_dir);
    }

    public int Expect(string name) => Expected.GetProperty(name).GetInt32();
}

public sealed class AzureDevOpsServicesReadersAgainstSimTests(ServicesSimFixture sim, AdoSimFixture server)
    : IClassFixture<ServicesSimFixture>, IClassFixture<AdoSimFixture>
{
    private const string Collection = "DefaultCollection";

    [Fact]
    public async Task The_services_validator_checks_the_pat_against_the_configured_core_root()
    {
        var validator = new AzureDevOpsConnectionValidator(new HttpClient(), sim.Endpoints);
        var ok = await validator.ValidateAsync(Collection, ServicesSimFixture.Pat, CancellationToken.None);
        Assert.True(ok.IsValid);
        Assert.Equal("Unauthorized", (await validator.ValidateAsync(Collection, "wrong", CancellationToken.None)).Code);
        // without configuration the very same validator talks to Microsoft's cloud, never to the simulator
        Assert.Equal("https://dev.azure.com/org", AzureDevOpsServicesEndpoints.Cloud.Core("org"));
    }

    [Fact]
    public async Task Services_discovery_reads_projects_repositories_refs_graph_identities_and_acls()
    {
        var reader = new AzureDevOpsDiscoveryReader(new HttpClient(), TimeProvider.System, null, sim.Endpoints);
        var inventory = await reader.DiscoverAsync(Guid.NewGuid(), Collection, ServicesSimFixture.Pat, CancellationToken.None);

        Assert.Equal("azure-devops-services", inventory.SourceProvider);
        Assert.Equal(SourceSystemKind.AzureDevOpsServices, inventory.Snapshot.SourceSystem);
        Assert.Equal(sim.Expect("projects"), inventory.Snapshot.Projects.Count);
        var repositories = inventory.Snapshot.Projects.SelectMany(p => p.Repositories).ToArray();
        Assert.Equal(sim.Expect("gitRepositories"), repositories.Count(r => r.VersionControl == VersionControlKind.Git));
        Assert.Equal(sim.Expect("tfvcRoots"), repositories.Count(r => r.VersionControl == VersionControlKind.Tfvc));
        Assert.Equal(sim.Expect("refs"), inventory.Refs.Count);
        // the Graph API pages with a page size of 3: continuation tokens must have been followed
        Assert.Equal(sim.Expect("users"), inventory.Snapshot.Identities.Count);
        Assert.Equal(sim.Expect("groups"), inventory.Groups.Count);
        Assert.Equal(sim.Expect("memberships"), inventory.Memberships.Count);
        Assert.Equal(sim.Expect("aclEntries"), inventory.AclEvidence.Count);
        Assert.True(inventory.IdentitiesComplete);
        Assert.True(inventory.SecurityCoverage.GroupsComplete);
        Assert.True(inventory.SecurityCoverage.AclsCollected);
        Assert.False(inventory.SecurityCoverage.EffectivePermissionsVerified);

        // ACL principals resolve through the legacy descriptors Graph publishes; only the orphan stays open
        var known = inventory.Snapshot.Identities.Select(i => i.LegacyDescriptor)
            .Concat(inventory.Groups.Select(g => g.LegacyDescriptor)).ToHashSet();
        var unresolved = inventory.AclEvidence.Select(a => a.SecurityDescriptor).Where(d => !known.Contains(d)).Distinct().ToArray();
        Assert.Equal(["S-1-9-orphan-descriptor"], unresolved);
    }

    [Fact]
    public async Task Services_and_server_flavours_of_the_same_estate_give_the_same_inventory()
    {
        var services = await new AzureDevOpsDiscoveryReader(new HttpClient(), TimeProvider.System, null, sim.Endpoints)
            .DiscoverAsync(Guid.NewGuid(), Collection, ServicesSimFixture.Pat, CancellationToken.None);
        var onPrem = await new AzureDevOpsServerDiscoveryReader(new HttpClient(), TimeProvider.System, new ServerEndpointPolicy(["127.0.0.1"], true))
            .DiscoverAsync(Guid.NewGuid(), Collection, server.ServerRoot, AdoSimFixture.Pat, null, CancellationToken.None);

        Assert.Equal(onPrem.Snapshot.Projects.Select(p => p.Name).Order(), services.Snapshot.Projects.Select(p => p.Name).Order());
        Assert.Equal(onPrem.Refs.Select(r => (r.Name, r.ObjectId)).Order(), services.Refs.Select(r => (r.Name, r.ObjectId)).Order());
        Assert.Equal(onPrem.Snapshot.Identities.Count, services.Snapshot.Identities.Count);
        Assert.Equal(onPrem.Memberships.Count, services.Memberships.Count);
        Assert.Equal(onPrem.AclEvidence.Count, services.AclEvidence.Count);
    }

    [Fact]
    public async Task Services_workload_reader_resolves_pipelines_variable_groups_and_service_connections()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + ServicesSimFixture.Pat)));
        using var projects = JsonDocument.Parse(await http.GetStringAsync($"{sim.Origin}/{Collection}/_apis/projects?api-version=7.1"));
        var projectId = projects.RootElement.GetProperty("value").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Alpha Platform").GetProperty("id").GetString()!;

        var reader = new AzureDevOpsWorkloadReader(new HttpClient(), null, null, sim.Endpoints);
        var pipeline = await reader.GetPipelineAsync(Collection, projectId, 11, ServicesSimFixture.Pat, default);
        Assert.Equal("yaml", pipeline.Kind);
        Assert.NotNull(pipeline.Structure);
        Assert.Contains(pipeline.Variables, v => v.Name == "DEPLOY_PASSWORD" && v.IsSecret);
        Assert.Contains(pipeline.Variables, v => v.Group != "pipeline");
        Assert.NotEmpty(pipeline.ServiceConnections);
    }

    [Fact]
    public async Task Services_git_fetch_uses_the_configured_git_root_and_produces_an_exact_mirror()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + ServicesSimFixture.Pat)));
        var root = $"{sim.Origin}/{Collection}/{Uri.EscapeDataString("Alpha Platform")}/_apis/git/repositories";
        using var list = JsonDocument.Parse(await http.GetStringAsync($"{root}?api-version=7.1"));
        var id = list.RootElement.GetProperty("value").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "alpha-api")
            .GetProperty("id").GetString()!;
        using var refs = JsonDocument.Parse(await http.GetStringAsync($"{root}/{id}/refs?api-version=7.1"));
        var expected = GitMirrorRefReconciler.CanonicalizeObservedRefs(refs.RootElement.GetProperty("value").EnumerateArray()
            .Select(r => new GitMirrorRef(r.GetProperty("name").GetString()!, r.GetProperty("objectId").GetString()!)).ToArray());
        var location = new AzureGitSourceLocation(Guid.NewGuid(), Collection, "Alpha Platform", "alpha-api", id);

        var sourceRoot = GitTestSupport.NewTempDirectory("services-fetch-");
        try
        {
            await new AzureDevOpsGitMirrorFetcher(sourceRoot, null, new AzureGitRemotePolicy(sim.Endpoints, null)).FetchAsync(
                new AzureGitMirrorFetchRequest(location, ServicesSimFixture.Pat, expected), default);
            Assert.Equal(expected, GitTestSupport.MirrorRefs(
                LocalGitSourceBundleAcquirer.LocalMirrorDirectory(sourceRoot, location.ConnectionId, id)));

            // without the (Development-only) override the same location can only mean dev.azure.com
            var cloudOnly = GitTestSupport.NewTempDirectory("services-fetch-cloud-");
            try
            {
                var plan = new AzureDevOpsGitMirrorFetcher(cloudOnly, new RecordingTransport(), AzureGitRemotePolicy.Cloud);
                // the recording transport fetches nothing, so verification fails: only the remote it was given matters
                await Assert.ThrowsAsync<GitSourceBundleException>(() =>
                    plan.FetchAsync(new AzureGitMirrorFetchRequest(location, ServicesSimFixture.Pat, expected), default));
                Assert.Equal("https://dev.azure.com/DefaultCollection/Alpha%20Platform/_git/alpha-api", RecordingTransport.LastUri!.AbsoluteUri);
            }
            finally { GitTestSupport.DeleteQuietly(cloudOnly); }
        }
        finally { GitTestSupport.DeleteQuietly(sourceRoot); }
    }

    private sealed class RecordingTransport : IAzureGitReadOnlyTransport
    {
        public static Uri? LastUri { get; private set; }
        public Task FetchAsync(string bareDirectory, Uri azureUri, string personalAccessToken, CancellationToken ct)
        {
            LastUri = azureUri;
            return Task.CompletedTask;
        }
    }
}

// The real API host (PostgreSQL, durable jobs) configured only through Providers:AzureDevOpsServices.
public sealed class AzureDevOpsServicesApiAgainstSimTests : IAsyncLifetime
{
    private readonly ServicesSimFixture _sim = new();
    private ApiHarness _api = null!;

    public async Task InitializeAsync()
    {
        await _sim.InitializeAsync();
        _api = new ApiHarness { AzureServicesSimOrigin = _sim.Origin };
        await _api.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _sim.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _api.LoginAsync(_api.AdminName, ApiHarness.AdminPassword));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _api.Client.SendAsync(request);
    }

    [Fact]
    public async Task A_services_connection_is_verified_and_discovered_through_the_durable_job()
    {
        var draft = new
        {
            name = "Sim Services", providerKey = "azure-devops-services", organization = "DefaultCollection",
            personalAccessToken = ServicesSimFixture.Pat
        };
        using var rejected = await SendAsync(HttpMethod.Post, "/api/connections", draft with { personalAccessToken = "wrong-pat" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        using var created = await SendAsync(HttpMethod.Post, "/api/connections", draft);
        var text = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(ServicesSimFixture.Pat, text);
        using var connection = JsonDocument.Parse(text);
        var connectionId = connection.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("source", connection.RootElement.GetProperty("direction").GetString());

        using var enqueue = await SendAsync(HttpMethod.Post, "/api/jobs/discovery", new { connectionId, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        using var accepted = JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync());
        var jobId = accepted.RootElement.GetProperty("id").GetGuid();
        await using (var scope = _api.Host.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MigrationJobProcessor>().RunOnceAsync(default));

        using var done = await SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}");
        using var job = JsonDocument.Parse(await done.Content.ReadAsStringAsync());
        Assert.True(job.RootElement.GetProperty("state").GetString() == "Succeeded", "job: " + job.RootElement.GetRawText());

        using var list = await SendAsync(HttpMethod.Get, "/api/discoveries");
        using var summaries = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var summary = summaries.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == jobId);
        Assert.Equal("azure-devops-services", summary.GetProperty("sourceProvider").GetString());
        Assert.Equal(_sim.Expect("projects"), summary.GetProperty("projectCount").GetInt32());
        Assert.Equal(_sim.Expect("refs"), summary.GetProperty("refCount").GetInt32());
        Assert.Equal(_sim.Expect("users"), summary.GetProperty("identityCount").GetInt32());
        Assert.Equal(_sim.Expect("groups"), summary.GetProperty("groupCount").GetInt32());
        Assert.Equal(_sim.Expect("memberships"), summary.GetProperty("membershipCount").GetInt32());
        Assert.Equal(_sim.Expect("distinctAclEntries"), summary.GetProperty("aclEntryCount").GetInt32());
    }
}
