using System.Net;

namespace AdoSim.Tests;

// What a real `git clone` over smart HTTP returns for the orion repositories.
[Collection("orion")]
public sealed class OrionRepositoryTests(OrionSim sim)
{
    private SimHost Sim => sim.Host;

    public static IEnumerable<object[]> Repositories() => new[]
    {
        new object[] { "Orion Commerce", "orion-api", 14, "main", new[] { "azure-pipelines.yml", ".azuredevops/pull_request_template.md", "Dockerfile", "Orion.sln", "src/Orion.Api/Program.cs", "tests/Orion.Api.Tests/OrderTests.cs", "pipelines/templates/variables.yml", "pipelines/nightly-scan.yml" } },
        new object[] { "Orion Commerce", "orion-web", 40, "main", new[] { "azure-pipelines.yml", "package.json", "src/App.tsx", "public/images/hero.png", "data/catalog-snapshot.bin", ".npmrc" } },
        new object[] { "Orion Commerce", "orion-lib", 15, "main", new[] { "azure-pipelines.yml", "src/Orion.Core/Orion.Core.csproj", "tests/Orion.Core.Tests/MoneyTests.cs" } },
        new object[] { "Orion Commerce", "Orion.Contracts", 5, "main", new[] { "azure-pipelines.yml", "src/Orion.Contracts/OrderPlaced.cs" } },
        new object[] { "Orion Commerce", "orion-windows", 8, "main", new[] { "build/windows-ci.yml", "scripts/sign.ps1", "Orion.Desktop.sln" } },
        new object[] { "Orion Commerce", "orion-legacy", 6, "master", new[] { "Orion.Legacy.sln", "build/package.ps1", "src/Orion.Legacy.Web/Global.asax.cs" } },
        new object[] { "Orion Commerce", "orion mobile app", 9, "develop/mobile/trunk-based-development-integration-line-for-release-train-2024-q2", new[] { "package.json", "README.md" } },
        new object[] { "Orion Platform", "orion-templates", 7, "main", new[] { "steps/security-scan.yml", "jobs/docker-build.yml", "stages/deploy-environment.yml", "variables/common.yml" } },
        new object[] { "Orion Platform", "orion-infra", 10, "main", new[] { "pipelines/terraform.yml", "pipelines/bicep.yml", "terraform/main.tf", "bicep/main.bicep" } },
        new object[] { "Orion Sandbox", "sandbox-playground", 3, "main", new[] { "azure-pipelines.yml" } },
        new object[] { "Orion Sandbox", "orion data tools", 4, "main", new[] { "pipelines/etl.yml", "etl/transform.py", "requirements.txt" } }
    };

    [Theory]
    [MemberData(nameof(Repositories))]
    public async Task Clone_contains_the_seed_files_history_and_default_branch(string project, string repo, int commits, string branch, string[] files)
    {
        var dir = await Sim.CloneAsync(project, repo);
        Assert.Equal(branch, await Git.MustAsync(dir, null, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(commits.ToString(), await Git.MustAsync(dir, null, "rev-list", "--count", "HEAD"));
        var tracked = (await Git.MustAsync(dir, null, "ls-files")).Split('\n').ToHashSet();
        foreach (var file in files) Assert.Contains(file, tracked);
        Assert.Equal(commits, tracked.Count(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"/file\d+\.txt$")));
        foreach (var yaml in tracked.Where(f => f.EndsWith(".yml")))
            Assert.DoesNotContain('\r', await File.ReadAllTextAsync(Path.Combine(dir, yaml)));
        // the history is authored by the seed users, not by a placeholder
        var authors = (await Git.MustAsync(dir, null, "log", "--format=%an")).Split('\n').Distinct().ToList();
        Assert.DoesNotContain("Sim Author", authors);
        Assert.True(authors.Count >= 2, string.Join(",", authors));
    }

    [Fact]
    public async Task Legacy_repository_has_no_yaml_and_the_docs_repository_is_empty()
    {
        var legacy = await Sim.CloneAsync("Orion Commerce", "orion-legacy");
        Assert.DoesNotContain(".yml", await Git.MustAsync(legacy, null, "ls-files"));
        Assert.DoesNotContain(".yaml", await Git.MustAsync(legacy, null, "ls-files"));

        var docs = await Sim.CloneAsync("Orion Commerce", "orion-docs");
        Assert.NotEqual(0, (await Git.RunAsync(docs, null, "rev-parse", "--verify", "HEAD")).Exit);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(docs).Select(p => Path.GetFileName(p)), name => name != ".git");
        Assert.Empty(await Sim.RefsAsync("Orion Commerce", "orion-docs"));
    }

    [Fact]
    public async Task Refs_cover_tags_nested_branches_and_unicode_names()
    {
        var api = await Sim.RefsAsync("Orion Commerce", "orion-api");
        Assert.Superset(new HashSet<string>
        {
            "refs/heads/main", "refs/heads/develop", "refs/heads/feature/checkout-v2", "refs/heads/feature/loyalty/points", "refs/heads/release/2024.1",
            "refs/heads/release/2024.2", "refs/heads/hotfix/payment-timeout", "refs/tags/v1.0.0", "refs/tags/v1.1.0", "refs/tags/v1.2.0", "refs/tags/v2.0.0"
        }, api.Keys.ToHashSet());

        var dir = await Sim.CloneAsync("Orion Commerce", "orion-api");
        Assert.Equal("commit", await Git.MustAsync(dir, null, "cat-file", "-t", "refs/tags/v1.0.0"));
        Assert.Equal("tag", await Git.MustAsync(dir, null, "cat-file", "-t", "refs/tags/v2.0.0"));
        Assert.Contains("Release v2.0.0", await Git.MustAsync(dir, null, "tag", "-l", "-n1", "v2.0.0"));

        var mobile = await Sim.RefsAsync("Orion Commerce", "orion mobile app");
        Assert.Contains("refs/heads/feature/приветствие", mobile.Keys);
        Assert.Contains("refs/heads/feature/日本語-ロケール", mobile.Keys);
        Assert.Contains("refs/heads/feature/rocket-🚀", mobile.Keys);
        Assert.Contains("refs/heads/release/ünïcode-2024", mobile.Keys);
        Assert.Contains("refs/tags/v0.9.0-β", mobile.Keys);
        Assert.Contains(mobile.Keys, k => k.Length > 80 && k.StartsWith("refs/heads/develop/mobile/"));
    }

    [Fact]
    public async Task Files_that_exist_only_on_a_branch_stay_on_that_branch()
    {
        var dir = await Sim.CloneAsync("Orion Commerce", "orion-api");
        var main = (await Git.MustAsync(dir, null, "ls-tree", "-r", "--name-only", "origin/main")).Split('\n').ToHashSet();
        var loyalty = (await Git.MustAsync(dir, null, "ls-tree", "-r", "--name-only", "origin/feature/loyalty/points")).Split('\n').ToHashSet();
        var checkout = (await Git.MustAsync(dir, null, "ls-tree", "-r", "--name-only", "origin/feature/checkout-v2")).Split('\n').ToHashSet();
        Assert.DoesNotContain("azure-pipelines-experimental.yml", main);
        Assert.Contains("azure-pipelines-experimental.yml", loyalty);
        Assert.DoesNotContain("docs/design/checkout-v2.md", main);
        Assert.Contains("docs/design/checkout-v2.md", checkout);
        Assert.Contains("azure-pipelines.yml", loyalty);
    }

    [Fact]
    public async Task Binary_and_large_files_survive_a_clone_byte_for_byte()
    {
        var dir = await Sim.CloneAsync("Orion Commerce", "orion-web");
        var big = new FileInfo(Path.Combine(dir, "data", "catalog-snapshot.bin"));
        Assert.Equal(1_572_864, big.Length);
        Assert.True(big.Length > 1_000_000);
        var png = await File.ReadAllBytesAsync(Path.Combine(dir, "public", "images", "hero.png"));
        Assert.Equal(65_536, png.Length);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(dir, "data", "catalog-snapshot.bin")),
            await (await Sim.GetAsync("/Orion%20Commerce/_apis/git/repositories/orion-web/items?path=/data/catalog-snapshot.bin&download=true")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task History_is_chronological_and_uses_the_configured_start_date()
    {
        var dir = await Sim.CloneAsync("Orion Commerce", "orion-api");
        var dates = (await Git.MustAsync(dir, null, "log", "--format=%cI", "origin/main")).Split('\n').Reverse().Select(DateTimeOffset.Parse).ToList();
        Assert.Equal(14, dates.Count);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 10, 0, 0, TimeSpan.Zero), dates[0]);
        Assert.Equal(dates.Order(), dates);
    }

    [Fact]
    public async Task The_disabled_repository_refuses_rest_and_push_but_exists()
    {
        using var refs = await Sim.GetAsync("/Orion%20Commerce/_apis/git/repositories/orion-archive/refs");
        Assert.Equal(HttpStatusCode.Forbidden, refs.StatusCode);
        var repos = (await Sim.JsonAsync("/Orion%20Commerce/_apis/git/repositories")).Items();
        Assert.True(repos.Single(r => r.Str("name") == "orion-archive").GetProperty("isDisabled").GetBoolean());
        Assert.Equal(13, (await Sim.ExpectedAsync()).GetProperty("gitRepositories").GetInt32());
    }

    [Fact]
    public async Task Identities_acls_and_groups_describe_nested_membership()
    {
        var identities = (await Sim.JsonAsync("/_apis/identities?queryMembership=Direct")).Items();
        var releaseManagers = identities.Single(i => i.Str("providerDisplayName") == "[Orion Platform]\\Release Managers");
        Assert.Equal(2, releaseManagers.GetProperty("members").GetArrayLength());
        Assert.Contains(identities.Single(i => i.Str("providerDisplayName") == "[Orion Commerce]\\Release Managers").Str("descriptor"),
            releaseManagers.GetProperty("members").EnumerateArray().Select(m => m.GetString()));
        var project = (await Sim.JsonAsync("/_apis/projects")).Items().Single(p => p.Str("name") == "Orion Platform").Str("id");
        var infra = (await Sim.JsonAsync("/Orion%20Platform/_apis/git/repositories")).Items().Single(r => r.Str("name") == "orion-infra").Str("id");
        var acl = (await Sim.JsonAsync($"/_apis/accesscontrollists/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87?token=repoV2/{project}/{infra}")).Items().Single();
        Assert.False(acl.GetProperty("inheritPermissions").GetBoolean());
        Assert.Equal(3, acl.GetProperty("acesDictionary").EnumerateObject().Count());
        var api = (await Sim.JsonAsync("/Orion%20Commerce/_apis/git/repositories")).Items().Single(r => r.Str("name") == "orion-api").Str("id");
        var commerce = (await Sim.JsonAsync("/_apis/projects")).Items().Single(p => p.Str("name") == "Orion Commerce").Str("id");
        var denied = (await Sim.JsonAsync($"/_apis/accesscontrollists/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87?token=repoV2/{commerce}/{api}")).Items().Single();
        Assert.Contains(denied.GetProperty("acesDictionary").EnumerateObject(), a => a.Value.GetProperty("deny").GetInt64() != 0);
    }
}
