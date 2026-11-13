using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

// What every test of the publisher needs: the real API (PostgreSQL ledger, JWT, encrypted signing key, production DI) against the GitHub simulator over real HTTP,
// a GitHub App registered as an operator would, a repository on the allow-list, and the calls a person makes to plan, confirm and publish.
public abstract class PublicationApiTestBase() : ProposalApiTestBase(singleReviewer: false, githubSim: true, publication: true)
{
    protected const string WorkflowPath = ".github/workflows/azdo-42-release-safe.yml";
    private RSA _key = null!;
    protected long InstallationId => PublicationAppId + 1;

    // the app is registered with the simulator and its signing key saved where the API reads it, as an operator would do
    protected async Task RegisterAppAsync(string workflows = "write", string pullRequests = "write", string contents = "write")
    {
        _key ??= RSA.Create(2048);
        var registered = await GitHub!.SendAsync("POST", "/_sim/apps", new
        {
            id = PublicationAppId, slug = "migrator-publisher", publicKeyPem = _key.ExportSubjectPublicKeyInfoPem(),
            installations = new[] { new { id = InstallationId, org = "orion-org", repositorySelection = "selected", contents, pullRequests, workflows } }
        });
        Assert.Equal(PublicationAppId, registered.GetProperty("id").GetInt64());
        await using var scope = Api.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IConnectionSecretStore>().SaveAsync(PublicationKeyReference, _key.ExportPkcs8PrivateKeyPem(), default);
    }

    protected async Task<string> RepoAsync(string name)
    {
        await GitHub!.SendAsync("POST", "/orgs/orion-org/repos", new { name, @private = true });
        await GitHub.SendAsync("PUT", $"/repos/orion-org/{name}/contents/README.md", new { message = "init", content = Convert.ToBase64String("# r\n"u8.ToArray()) });
        return name;
    }

    protected async Task<string> TipAsync(string repo, string branch = "main") =>
        (await GitHub!.SendAsync("GET", $"/repos/orion-org/{repo}/branches/{branch}")).GetProperty("commit").GetProperty("sha").GetString()!;

    protected async Task<JsonElement> ApprovedAsync()
    {
        var conversion = await ReadyProposalAsync();
        using var approved = await SendAsync(HttpMethod.Post, Url(conversion, "/approve"), Who.Superadmin,
            new { workflowSha256 = conversion.GetProperty("workflowSha256").GetString(), acknowledgedWarnings = Warned(conversion), note = "Reviewed." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return conversion;
    }

    protected async Task<(HttpStatusCode Status, JsonElement Body)> SendJsonAsync(string url, object body, Who who = Who.Superadmin)
    {
        using var response = await SendAsync(HttpMethod.Post, url, who, body);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    protected Task<(HttpStatusCode Status, JsonElement Body)> PlanAsync(JsonElement conversion, string repo, string? baseBranch = null) =>
        SendJsonAsync(Url(conversion, "/publication-plan"), new { destinationConnectionId = Api.DestinationConnectionId, repository = repo, baseBranch });

    protected Task<(HttpStatusCode Status, JsonElement Body)> PublishAsync(JsonElement conversion, string repo, string? planSha256, Who who = Who.Superadmin) =>
        SendJsonAsync(Url(conversion, "/publish"), new { destinationConnectionId = Api.DestinationConnectionId, repository = repo, planSha256 }, who);

    protected async Task<(string Method, string Path)[]> WritesAsync(string repo) =>
        (await GitHub!.SendAsync("GET", "/_sim/requests?limit=500")).EnumerateArray()
            .Select(e => (Method: e.GetProperty("Method").GetString()!, Path: e.GetProperty("Path").GetString()!))
            // the test's own setup writes (README, CHANGES) are not the product's
            .Where(c => c.Method != "GET" && c.Path.Contains($"/repos/orion-org/{repo}") && !c.Path.EndsWith("/README.md") && !c.Path.EndsWith("/CHANGES.md")).ToArray();

    // the table is shared by every test of the run: only this proposal's rows are this test's
    protected Task<List<GitHubMigrator.Infrastructure.PostgreSql.WorkflowPublicationRecord>> RowsAsync(JsonElement conversion)
    {
        var id = conversion.GetProperty("proposalId").GetGuid();
        return InDbAsync(db => db.WorkflowPublications.AsNoTracking().Where(p => p.ProposalId == id).OrderBy(p => p.StartedAtUtc).ToListAsync());
    }

    protected async Task<string> ReadyPlanHashAsync(JsonElement conversion, string repo)
    {
        var (status, plan) = await PlanAsync(conversion, repo);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(plan.GetProperty("ready").GetBoolean(), plan.ToString());
        return plan.GetProperty("planSha256").GetString()!;
    }

}
