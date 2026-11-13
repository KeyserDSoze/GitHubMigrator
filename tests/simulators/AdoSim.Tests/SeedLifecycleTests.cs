using System.Net;
using System.Text.Json;

namespace AdoSim.Tests;

public sealed class SeedLifecycleTests
{
    private static string NewDir() => Path.Combine(Path.GetTempPath(), "adosim-persist-" + Guid.NewGuid().ToString("N"));
    private static string Marker(string dir) => Path.Combine(dir, Estate.MarkerFile);

    [Fact]
    public async Task An_unchanged_seed_reuses_the_repositories_and_keeps_api_commits()
    {
        var dir = NewDir();
        try
        {
            string commit;
            await using (var first = await SimHost.StartAsync("default", dir: dir))
            {
                Assert.Equal(first.Seed.ComputeHash(), (await File.ReadAllTextAsync(Marker(dir))).Trim());
                using var response = await first.PostCommitAsync("Alpha Platform", "alpha-api", new { branch = "main", message = "survives a restart", files = new Dictionary<string, string> { ["kept.txt"] = "kept\n" } });
                commit = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Str("commitId");
            }
            var before = File.GetLastWriteTimeUtc(Marker(dir));
            await using var second = await SimHost.StartAsync("default", dir: dir);
            Assert.Equal(commit, (await second.RefsAsync("Alpha Platform", "alpha-api"))["refs/heads/main"]);
            Assert.Equal("kept\n", (await second.JsonAsync("/Alpha%20Platform/_apis/git/repositories/alpha-api/items?path=/kept.txt")).Str("content"));
            Assert.Equal(before, File.GetLastWriteTimeUtc(Marker(dir)));
        }
        finally { SimHost.DeleteDir(dir); }
    }

    [Fact]
    public async Task A_changed_seed_wipes_and_rebuilds_the_repositories_on_a_persisted_volume()
    {
        var dir = NewDir();
        try
        {
            var original = SeedSpec.Load("default");
            string pristine;
            await using (var first = await SimHost.StartAsync("default", dir: dir))
            {
                pristine = (await first.RefsAsync("Alpha Platform", "alpha-api"))["refs/heads/main"];
                using var response = await first.PostCommitAsync("Alpha Platform", "alpha-api", new { branch = "main", message = "will be wiped" });
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Directory.CreateDirectory(Path.Combine(dir, "work", "leftover"));
            }
            Assert.Equal(original.ComputeHash(), (await File.ReadAllTextAsync(Marker(dir))).Trim());

            // the same persisted directory, an enriched seed: before the fix nothing would have changed
            await using (var changed = await SimHost.StartAsync("default", "2,1,1", dir))
            {
                Assert.NotEqual(original.ComputeHash(), (await File.ReadAllTextAsync(Marker(dir))).Trim());
                Assert.Equal(changed.Seed.ComputeHash(), (await File.ReadAllTextAsync(Marker(dir))).Trim());
                Assert.Equal(pristine, (await changed.RefsAsync("Alpha Platform", "alpha-api"))["refs/heads/main"]);
                Assert.False(Directory.Exists(Path.Combine(dir, "work", "leftover")));
                Assert.Contains((await changed.JsonAsync("/_apis/projects")).Items(), p => p.Str("name") == "Bulk 002");
                Assert.Equal("Bulk 001", (await changed.JsonAsync("/_apis/projects?$top=1&$skip=5")).Items().Single().Str("name"));
                Assert.Equal(1, (await changed.JsonAsync("/Bulk%20001/_apis/git/repositories")).GetProperty("count").GetInt32());
            }
            await using var back = await SimHost.StartAsync("default", dir: dir);
            Assert.Equal(original.ComputeHash(), (await File.ReadAllTextAsync(Marker(dir))).Trim());
            Assert.Equal(5, (await back.JsonAsync("/_apis/projects")).GetProperty("count").GetInt32());
        }
        finally { SimHost.DeleteDir(dir); }
    }

    [Fact]
    public async Task A_changed_repository_file_in_the_seed_is_picked_up_and_a_missing_marker_forces_a_rebuild()
    {
        var dir = NewDir();
        var seedDir = NewDir();
        Directory.CreateDirectory(Path.Combine(seedDir, "files", "app"));
        var seedPath = Path.Combine(seedDir, "seed.json");
        await File.WriteAllTextAsync(seedPath, """{ "collection": "DefaultCollection", "users": [], "groups": [], "projects": [ { "name": "P", "sourceControl": "Git", "repos": [ { "name": "app", "commits": 1, "filesFrom": "app" } ] } ] }""");
        var file = Path.Combine(seedDir, "files", "app", "hello.txt");
        try
        {
            await File.WriteAllTextAsync(file, "v1\n");
            var hash1 = SeedSpec.Load(seedPath).ComputeHash();
            await using (var one = await SimHost.StartAsync(seedPath, dir: dir))
                Assert.Equal("v1\n", (await one.JsonAsync("/P/_apis/git/repositories/app/items?path=/hello.txt")).Str("content"));

            await File.WriteAllTextAsync(file, "v2\n");
            var hash2 = SeedSpec.Load(seedPath).ComputeHash();
            Assert.NotEqual(hash1, hash2);
            await using (var two = await SimHost.StartAsync(seedPath, dir: dir))
            {
                Assert.Equal("v2\n", (await two.JsonAsync("/P/_apis/git/repositories/app/items?path=/hello.txt")).Str("content"));
                using var response = await two.PostCommitAsync("P", "app", new { branch = "main", message = "api" });
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }
            // same hash: the API commit is still there; without the marker everything is rebuilt
            await using (var three = await SimHost.StartAsync(seedPath, dir: dir))
                Assert.Equal(2, (await three.JsonAsync("/P/_apis/git/repositories/app/commits")).GetProperty("count").GetInt32());
            File.Delete(Marker(dir));
            await using var four = await SimHost.StartAsync(seedPath, dir: dir);
            Assert.Equal(1, (await four.JsonAsync("/P/_apis/git/repositories/app/commits")).GetProperty("count").GetInt32());
            Assert.Equal(hash2, (await File.ReadAllTextAsync(Marker(dir))).Trim());
        }
        finally { SimHost.DeleteDir(dir); SimHost.DeleteDir(seedDir); }
    }
}

public sealed class DeterminismTests
{
    [Theory]
    [InlineData("default", null)]
    [InlineData("orion", null)]
    [InlineData("default", "2,2,1")]
    public async Task The_same_seed_yields_identical_refs_and_shas_in_every_repository(string profile, string? bulk)
    {
        await using var one = await SimHost.StartAsync(profile, bulk);
        await using var two = await SimHost.StartAsync(profile, bulk);
        var first = await one.AllRefsAsync();
        var second = await two.AllRefsAsync();
        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.Equal(one.Seed.ComputeHash(), two.Seed.ComputeHash());
    }

    [Fact]
    public async Task Default_seed_shas_are_the_ones_the_original_simulator_produced()
    {
        // Pinned from the pre-fast-import implementation: existing consumers of the default seed rely on them.
        await using var sim = await SimHost.StartAsync("default");
        var alpha = await sim.RefsAsync("Alpha Platform", "alpha-api");
        Assert.Equal("eaeec3b0f6c22ff67f3f8c33a69678b160e3d8dc", alpha["refs/heads/main"]);
        Assert.Equal("3637365f297ee67d5e1dd3dea5d4a9cc31cc35d8", alpha["refs/heads/feature/auth/login"]);
        Assert.Equal("8b1d684c5f473651eca6b11fc2e2f5c14303e33a", alpha["refs/tags/v2.0.0"]);
        Assert.Equal("689f3781b85b00e760161efb0af61a541afea4a9", (await sim.RefsAsync("Beta Ünïcode", "beta-svc"))["refs/heads/main"]);
    }
}
