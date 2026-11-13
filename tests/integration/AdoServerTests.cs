using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AdoSim;
using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.AzureDevOps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// Real Azure DevOps Server-style REST + git smart HTTP served in-process by tools/AdoSim.
// Requires only the git CLI; the page size is 50 so ref pagination is always exercised.
public sealed class AdoSimFixture : IAsyncLifetime
{
    public const string Pat = "sim-pat";
    private string _dir = "";
    public WebApplication App { get; private set; } = null!;
    public string Origin { get; private set; } = "";
    public string ServerRoot => Origin + "/tfs";
    public JsonElement Expected { get; private set; }

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adosim-" + Guid.NewGuid().ToString("N"));
        App = await AdoSimApp.StartAsync(new SimOptions(SeedSpec.Load(null), _dir, Pat, "7.0", 50, "http://127.0.0.1:0"));
        Origin = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient();
        Expected = JsonDocument.Parse(await http.GetStringAsync(Origin + "/_sim/expected")).RootElement.Clone();
    }

    public async Task DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch (IOException) { /* temp data only */ }
    }

    public int Expect(string name) => Expected.GetProperty(name).GetInt32();
}

public sealed class AdoServerProviderTests(AdoSimFixture sim) : IClassFixture<AdoSimFixture>
{
    private static readonly ServerEndpointPolicy Policy = new(["127.0.0.1"], allowHttp: true);

    private AzureDevOpsServerDiscoveryReader Reader() =>
        new(new HttpClient(), TimeProvider.System, Policy);

    [Fact]
    public async Task Discovery_reads_projects_repos_refs_identities_and_acls_from_a_server()
    {
        var inventory = await Reader().DiscoverAsync(Guid.NewGuid(), "DefaultCollection", sim.ServerRoot,
            AdoSimFixture.Pat, null, CancellationToken.None);

        Assert.Equal("azure-devops-server", inventory.SourceProvider);
        Assert.Equal(SourceSystemKind.AzureDevOpsServer, inventory.Snapshot.SourceSystem);
        Assert.Equal(sim.Expect("projects"), inventory.Snapshot.Projects.Count);
        Assert.Contains(inventory.Snapshot.Projects, p => p.Name == "Beta Ünïcode");
        var repositories = inventory.Snapshot.Projects.SelectMany(p => p.Repositories).ToArray();
        Assert.Equal(sim.Expect("gitRepositories"), repositories.Count(r => r.VersionControl == VersionControlKind.Git));
        Assert.Equal(sim.Expect("tfvcRoots"), repositories.Count(r => r.VersionControl == VersionControlKind.Tfvc));
        // 150 bulk branches + 30 tags exceed the simulator's 50-ref page: this is the continuation path.
        Assert.Equal(sim.Expect("refs"), inventory.Refs.Count);
        Assert.Contains(inventory.Refs, r => r.Name == "refs/heads/feature/ünïcode");
        Assert.Equal(sim.Expect("users"), inventory.Snapshot.Identities.Count);
        Assert.Equal(sim.Expect("groups"), inventory.Groups.Count);
        Assert.Equal(sim.Expect("memberships"), inventory.Memberships.Count);
        Assert.Equal(sim.Expect("aclEntries"), inventory.AclEvidence.Count);
        Assert.True(inventory.IdentitiesComplete);
        Assert.True(inventory.SecurityCoverage.GroupsComplete);
        Assert.True(inventory.SecurityCoverage.AclsCollected);
        Assert.False(inventory.SecurityCoverage.EffectivePermissionsVerified);
        // One repository is disabled: its refs fail, the rest of the scan must continue.
        Assert.Contains(inventory.Issues, i => i.Code == "InsufficientScope" && i.RepositoryId is not null);
        Assert.DoesNotContain(inventory.Issues, i => i.Code is "RefsTruncated" or "RefsPaginationLoop");
    }

    [Fact]
    public async Task Identity_descriptors_double_as_legacy_descriptors_so_acl_principals_resolve()
    {
        var inventory = await Reader().DiscoverAsync(Guid.NewGuid(), "DefaultCollection", sim.ServerRoot,
            AdoSimFixture.Pat, null, CancellationToken.None);
        var known = inventory.Snapshot.Identities.Select(i => i.LegacyDescriptor)
            .Concat(inventory.Groups.Select(g => g.LegacyDescriptor)).ToHashSet();
        var unresolved = inventory.AclEvidence.Select(a => a.SecurityDescriptor).Where(d => !known.Contains(d)).Distinct().ToArray();
        // Only the deliberately orphaned descriptor stays unresolved.
        Assert.Equal(["S-1-9-orphan-descriptor"], unresolved);
        Assert.Contains(inventory.Snapshot.Identities, i => i.DisplayName == "Sam Smith" && i.Email == "sam.smith@example.test");
        Assert.Contains(inventory.Snapshot.Identities, i => i.DisplayName == "Carol Verdi" && i.Email is null);
    }

    [Fact]
    public async Task Validator_accepts_a_valid_pat_and_maps_failures_to_stable_codes()
    {
        var validator = new AzureDevOpsServerConnectionValidator(new HttpClient(), Policy);
        var ok = await validator.ValidateAsync("DefaultCollection", sim.ServerRoot, AdoSimFixture.Pat, CancellationToken.None);
        Assert.True(ok.IsValid);
        Assert.Equal("Sim Administrator", ok.AuthenticatedAs);
        Assert.Equal("Unauthorized",
            (await validator.ValidateAsync("DefaultCollection", sim.ServerRoot, "wrong", CancellationToken.None)).Code);
        Assert.Equal("ServerHostNotAllowed",
            (await validator.ValidateAsync("DefaultCollection", "http://169.254.169.254/tfs", "x", CancellationToken.None)).Code);
        Assert.Equal("ServerHostNotAllowed",
            (await validator.ValidateAsync("DefaultCollection", "http://localhost:1/tfs", "x", CancellationToken.None)).Code);
    }

    [Fact]
    public async Task Reader_refuses_a_host_that_is_not_allow_listed_without_any_request()
    {
        var strict = new AzureDevOpsServerDiscoveryReader(new HttpClient(), TimeProvider.System,
            new ServerEndpointPolicy(["only.example.test"], allowHttp: true));
        var error = await Assert.ThrowsAsync<DiscoveryProviderException>(() => strict.DiscoverAsync(
            Guid.NewGuid(), "DefaultCollection", sim.ServerRoot, AdoSimFixture.Pat, null, CancellationToken.None));
        Assert.Equal("ServerHostNotAllowed", error.Code);
    }

    [Theory]
    [InlineData("https://ado.corp.test/tfs", "ado.corp.test", false, true)]
    [InlineData("https://ado.corp.test/tfs/", "ado.corp.test", false, true)]
    [InlineData("https://eu.ado.corp.test", "*.ado.corp.test", false, true)]
    [InlineData("http://ado.corp.test/tfs", "ado.corp.test", false, false)]
    [InlineData("http://ado.corp.test/tfs", "ado.corp.test", true, true)]
    [InlineData("https://user:pw@ado.corp.test/tfs", "ado.corp.test", false, false)]
    [InlineData("https://ado.corp.test/tfs?x=1", "ado.corp.test", false, false)]
    [InlineData("https://ado.corp.test/tfs#frag", "ado.corp.test", false, false)]
    [InlineData("https://evil.test/tfs", "ado.corp.test", false, false)]
    [InlineData("https://ado.corp.test.evil.test/tfs", "ado.corp.test", false, false)]
    [InlineData("https://169.254.169.254/latest", "169.254.169.254", false, false)]
    [InlineData("https://[fe80::1]/tfs", "[fe80::1]", false, false)]
    [InlineData("https://0.0.0.0/tfs", "0.0.0.0", false, false)]
    [InlineData("ftp://ado.corp.test/tfs", "ado.corp.test", true, false)]
    [InlineData("not a url", "ado.corp.test", false, false)]
    public void Endpoint_policy_enforces_the_egress_allow_list(string url, string allowed, bool allowHttp, bool accepted)
    {
        var policy = new ServerEndpointPolicy([allowed], allowHttp);
        if (accepted) Assert.DoesNotContain("?", policy.Normalize(url));
        else Assert.Throws<ArgumentException>(() => policy.Normalize(url));
    }

    [Fact]
    public void Endpoint_policy_without_hosts_disables_self_hosted_providers()
    {
        Assert.False(new ServerEndpointPolicy([]).Enabled);
        Assert.Throws<ArgumentException>(() => new ServerEndpointPolicy([]).Normalize("https://ado.corp.test"));
    }
}

public sealed class AdoServerApiTests(ApiHarness harness, AdoSimFixture sim)
    : IClassFixture<ApiHarness>, IClassFixture<AdoSimFixture>
{
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await harness.LoginAsync(harness.AdminName, ApiHarness.AdminPassword));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await harness.Client.SendAsync(request);
    }

    [Fact]
    public async Task Server_connection_is_validated_stored_without_the_pat_and_discovered_through_the_durable_job()
    {
        var draft = new
        {
            name = "Sim server", providerKey = "azure-devops-server", organization = "DefaultCollection",
            personalAccessToken = AdoSimFixture.Pat, baseUrl = sim.ServerRoot
        };

        using var blocked = await SendAsync(HttpMethod.Post, "/api/connections",
            draft with { baseUrl = "http://169.254.169.254/tfs" });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        using var rejected = await SendAsync(HttpMethod.Post, "/api/connections",
            draft with { personalAccessToken = "wrong-pat" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        using var missingUrl = await SendAsync(HttpMethod.Post, "/api/connections", draft with { baseUrl = (string)null! });
        Assert.Equal(HttpStatusCode.BadRequest, missingUrl.StatusCode);

        using var created = await SendAsync(HttpMethod.Post, "/api/connections", draft);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(AdoSimFixture.Pat, createdText);
        using var connection = JsonDocument.Parse(createdText);
        var connectionId = connection.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(sim.ServerRoot, connection.RootElement.GetProperty("baseUrl").GetString());
        Assert.Equal("source", connection.RootElement.GetProperty("direction").GetString());

        using var verify = await SendAsync(HttpMethod.Post, $"/api/connections/{connectionId}/verify");
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Contains("Verified", await verify.Content.ReadAsStringAsync());

        using var enqueue = await SendAsync(HttpMethod.Post, "/api/jobs/discovery",
            new { connectionId, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        using var accepted = JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync());
        var jobId = accepted.RootElement.GetProperty("id").GetGuid();

        await using (var scope = harness.Host.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MigrationJobProcessor>().RunOnceAsync(default));

        using var done = await SendAsync(HttpMethod.Get, $"/api/jobs/{jobId}");
        using var job = JsonDocument.Parse(await done.Content.ReadAsStringAsync());
        Assert.True(job.RootElement.GetProperty("state").GetString() == "Succeeded",
            "job: " + job.RootElement.GetRawText());

        using var list = await SendAsync(HttpMethod.Get, "/api/discoveries");
        using var summaries = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var summary = summaries.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == jobId);
        Assert.Equal("azure-devops-server", summary.GetProperty("sourceProvider").GetString());
        Assert.Equal(sim.Expect("projects"), summary.GetProperty("projectCount").GetInt32());
        Assert.Equal(sim.Expect("gitRepositories") + sim.Expect("tfvcRoots"), summary.GetProperty("repositoryCount").GetInt32());
        Assert.Equal(sim.Expect("refs"), summary.GetProperty("refCount").GetInt32());
        Assert.Equal(sim.Expect("users"), summary.GetProperty("identityCount").GetInt32());
        Assert.Equal(sim.Expect("groups"), summary.GetProperty("groupCount").GetInt32());
        Assert.Equal(sim.Expect("memberships"), summary.GetProperty("membershipCount").GetInt32());
        Assert.Equal(sim.Expect("distinctAclEntries"), summary.GetProperty("aclEntryCount").GetInt32());
    }
}
