using System.Text.Json;
using Xunit;
using static LlmSim.Tests.SimHost;

namespace LlmSim.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "llmsim-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Write(string name, string text)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public sealed class ScriptQueueTests(LlmSimFixture fixture) : SimTests(fixture), IClassFixture<LlmSimFixture>
{
    [Fact]
    public async Task Scripted_replies_are_consumed_first_in_first_out_one_shot()
    {
        await Sim.ScriptAsync(new { content = "first" }, new { content = "second" }, new { content = "third" });
        Assert.Equal(3, (await Sim.Get("/_sim/script")).Json.GetProperty("remaining").GetInt32());

        Assert.Equal("first", (await Sim.Chat(Req("a"))).Content);
        Assert.Equal(2, (await Sim.Get("/_sim/script")).Json.GetProperty("remaining").GetInt32());
        Assert.Equal("second", (await Sim.Chat(Req("a"))).Content);
        Assert.Equal("third", (await Sim.Chat(Req("a"))).Content);
        Assert.Equal(0, (await Sim.Get("/_sim/script")).Json.GetProperty("remaining").GetInt32());
        Assert.Contains("simulated answer", (await Sim.Chat(Req("a"))).Content);

        Assert.Equal(["queue", "queue", "queue", "default"], (await Sim.LogAsync()).Select(e => e.GetProperty("matched").GetString()!).ToArray());
    }

    [Fact]
    public async Task The_queue_shows_what_is_left_and_posts_append()
    {
        var first = await Sim.ScriptAsync(new { content = "a" }, new { content = "b", repeat = 3, status = 429 });
        Assert.Equal(2, first.Json.GetProperty("queued").GetInt32());
        Assert.Equal(2, first.Json.GetProperty("remaining").GetInt32());
        var second = await Sim.ScriptAsync(new { content = "c" });
        Assert.Equal(1, second.Json.GetProperty("queued").GetInt32());
        Assert.Equal(3, second.Json.GetProperty("remaining").GetInt32());

        var shown = (await Sim.Get("/_sim/script")).Json;
        Assert.Equal(3, shown.GetProperty("remaining").GetInt32());
        var responses = shown.GetProperty("responses");
        Assert.Equal(["a", "b", "c"], responses.EnumerateArray().Select(r => r.GetProperty("reply").GetProperty("content").GetString()!).ToArray());
        Assert.Equal([1, 3, 1], responses.EnumerateArray().Select(r => r.GetProperty("remaining").GetInt32()).ToArray());
        Assert.Equal(429, responses[1].GetProperty("reply").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Repeat_serves_the_same_scripted_reply_several_times()
    {
        await Sim.ScriptAsync(new { content = "twice", repeat = 2 }, new { content = "once" });
        Assert.Equal(["twice", "twice", "once"], new[] { (await Sim.Chat(Req("a"))).Content, (await Sim.Chat(Req("a"))).Content, (await Sim.Chat(Req("a"))).Content });
        Assert.Contains("simulated", (await Sim.Chat(Req("a"))).Content);
    }

    [Fact]
    public async Task The_queue_can_be_cleared()
    {
        await Sim.ScriptAsync(new { content = "never seen" });
        Assert.Equal(204, (await Sim.Delete("/_sim/script")).Status);
        Assert.Equal(0, (await Sim.Get("/_sim/script")).Json.GetProperty("remaining").GetInt32());
        Assert.Contains("simulated", (await Sim.Chat(Req("a"))).Content);
    }

    [Fact]
    public async Task The_queue_wins_over_recipes_and_a_faulted_reply_is_consumed_too()
    {
        await Sim.AddRecipesAsync(new { name = "all", reply = new { content = "from recipe" } });
        await Sim.ScriptAsync(new { status = 500 }, new { content = "queued" });
        Assert.Equal(500, (await Sim.Chat(Req("a"))).Status);
        Assert.Equal("queued", (await Sim.Chat(Req("a"))).Content);
        Assert.Equal("from recipe", (await Sim.Chat(Req("a"))).Content);
    }

    public static TheoryData<string, string> BadScripts => new()
    {
        { "", "A JSON body is required." },
        { "{}", "'responses' must be a non-empty array of replies." },
        { "{\"responses\":[]}", "'responses' must be a non-empty array of replies." },
        { "{\"responses\":[{\"contnet\":\"typo\"}]}", "Invalid JSON body:" },
        { "{\"responses\":[{\"content\":\"a\"},{\"status\":418}]}", "responses[1]: status must be one of: 400, 401, 403, 404, 408, 413, 429, 500, 502, 503." },
        { "{\"responses\":[{\"finishReason\":\"stopped\"}]}", "responses[0]: finishReason must be one of:" },
        { "{\"responses\":[{\"content\":\"a\",\"contentFrom\":\"good/minimal.yml\"}]}", "responses[0]: Use either 'content' or 'contentFrom', not both." },
        { "{\"responses\":[{\"contentFrom\":\"nope.txt\"}]}", "responses[0]: contentFrom 'nope.txt' is not an embedded file and no recipes file directory is configured." },
        { "{\"responses\":[{\"repeat\":0}]}", "responses[0]: repeat must be >= 1." },
        { "{\"responses\":[{\"delayMs\":-1}]}", "responses[0]: delayMs must be between 0 and 600000." },
        { "{\"responses\":[{\"headers\":{\"Content-Length\":\"5\"}}]}", "responses[0]: header 'Content-Length' is set by the simulator" },
        { "{\"responses\":[{\"headers\":{\"X-A\":\"a\\r\\nX-B: b\"}}]}", "responses[0]: header 'X-A' is not a valid header." },
        { "{\"responses\":[{\"contentType\":\"not a media type\"}]}", "responses[0]: contentType 'not a media type' is not a valid media type." },
        { "{not json", "Invalid JSON body:" },
    };

    [Theory]
    [MemberData(nameof(BadScripts))]
    public async Task Invalid_scripts_are_rejected_and_queue_nothing(string body, string message)
    {
        var response = await Sim.Post("/_sim/script", body);
        Assert.Equal(400, response.Status);
        Assert.StartsWith(message, response.Error.GetProperty("message").GetString());
        Assert.Equal(0, (await Sim.Get("/_sim/script")).Json.GetProperty("remaining").GetInt32());
    }
}

public sealed class RecipeTests(LlmSimFixture fixture) : SimTests(fixture), IClassFixture<LlmSimFixture>
{
    private async Task<string> Ask(object request) => (await Sim.Chat(request)).Content;

    [Fact]
    public async Task Contains_needs_every_substring_in_the_concatenated_messages()
    {
        await Sim.AddRecipesAsync(new { name = "all", when = new { contains = new[] { "alpha", "beta" } }, reply = new { content = "matched" } });
        Assert.Contains("simulated", await Ask(Req("alpha only")));
        Assert.Equal("matched", await Ask(Req("alpha and beta")));
        Assert.Equal("matched", await Ask(ReqMessages("gpt-sim", ("system", "beta"), ("user", "alpha"))));
        Assert.Contains("simulated", await Ask(Req("ALPHA BETA")));
    }

    [Fact]
    public async Task ContainsAny_needs_one_substring()
    {
        await Sim.AddRecipesAsync(new { name = "any", when = new { containsAny = new[] { "alpha", "beta" } }, reply = new { content = "matched" } });
        Assert.Equal("matched", await Ask(Req("only beta here")));
        Assert.Equal("matched", await Ask(Req("alpha")));
        Assert.Contains("simulated", await Ask(Req("gamma")));
    }

    [Fact]
    public async Task Regex_is_matched_against_the_concatenated_messages()
    {
        await Sim.AddRecipesAsync(new { name = "rx", when = new { regex = @"(?i)pipeline-\d{3}\b" }, reply = new { content = "matched" } });
        Assert.Equal("matched", await Ask(Req("please convert Pipeline-042 now")));
        Assert.Contains("simulated", await Ask(Req("pipeline-42")));
    }

    [Fact]
    public async Task System_matches_only_system_and_developer_messages()
    {
        await Sim.AddRecipesAsync(new { name = "sys", when = new { system = "You are a YAML expert" }, reply = new { content = "matched" } });
        Assert.Equal("matched", await Ask(ReqMessages("gpt-sim", ("system", "You are a YAML expert."), ("user", "hi"))));
        Assert.Equal("matched", await Ask(ReqMessages("gpt-sim", ("developer", "You are a YAML expert."), ("user", "hi"))));
        Assert.Contains("simulated", await Ask(ReqMessages("gpt-sim", ("user", "You are a YAML expert."))));
    }

    [Fact]
    public async Task Model_and_minMessages_and_lastUser_are_checked()
    {
        await Sim.AddRecipesAsync(
            new { name = "llama-only", when = new { model = "llama-sim" }, reply = new { content = "llama" } },
            new { name = "long", when = new { minMessages = 4 }, reply = new { content = "long conversation" } },
            new { name = "last", when = new { lastUser = "[[go]]" }, reply = new { content = "last user" } });
        Assert.Equal("llama", await Ask(Req("hi", "llama-sim")));
        Assert.Contains("simulated", await Ask(Req("hi", "gpt-sim")));
        Assert.Equal("long conversation", await Ask(ReqMessages("gpt-sim", ("user", "1"), ("assistant", "2"), ("user", "3"), ("assistant", "4"))));
        Assert.Contains("simulated", await Ask(ReqMessages("gpt-sim", ("user", "1"), ("assistant", "2"), ("user", "3"))));
        Assert.Equal("last user", await Ask(ReqMessages("gpt-sim", ("user", "x"), ("assistant", "y"), ("user", "do it [[go]]"))));
        Assert.Contains("simulated", await Ask(ReqMessages("gpt-sim", ("user", "[[go]]"), ("assistant", "y"), ("user", "no marker here"))));
        Assert.Contains("simulated", await Ask(ReqMessages("gpt-sim", ("system", "[[go]]"), ("user", "hi"))));
    }

    [Fact]
    public async Task All_conditions_of_one_recipe_must_hold_and_an_empty_when_matches_everything()
    {
        await Sim.AddRecipesAsync(
            new { name = "strict", when = new { contains = new[] { "x" }, model = "llama-sim", system = "sys" }, reply = new { content = "strict" } },
            new { name = "catch-all", reply = new { content = "catch-all" } });
        Assert.Equal("strict", await Ask(ReqMessages("llama-sim", ("system", "sys"), ("user", "x"))));
        Assert.Equal("catch-all", await Ask(ReqMessages("gpt-sim", ("system", "sys"), ("user", "x"))));
        Assert.Equal("catch-all", await Ask(ReqMessages("llama-sim", ("user", "x"))));
    }

    [Fact]
    public async Task The_first_matching_recipe_wins_in_registration_order()
    {
        await Sim.AddRecipesAsync(
            new { name = "one", when = new { contains = new[] { "k" } }, reply = new { content = "one" } },
            new { name = "two", when = new { contains = new[] { "k" } }, reply = new { content = "two" } });
        Assert.Equal("one", await Ask(Req("k")));
        Assert.Equal(["recipe:one"], (await Sim.LogAsync()).Select(e => e.GetProperty("matched").GetString()!).ToArray());
    }

    [Fact]
    public async Task Repeat_limits_how_often_a_recipe_answers_and_then_the_next_one_takes_over()
    {
        await Sim.AddRecipesAsync(
            new { name = "limited", when = new { contains = new[] { "k" } }, reply = new { content = "limited", repeat = 2 } },
            new { name = "fallback", when = new { contains = new[] { "k" } }, reply = new { content = "fallback" } });
        Assert.Equal(["limited", "limited", "fallback", "fallback"],
            new[] { await Ask(Req("k")), await Ask(Req("k")), await Ask(Req("k")), await Ask(Req("k")) });
        var shown = (await Sim.Get("/_sim/recipes")).Json.EnumerateArray().Where(r => r.GetProperty("source").GetString() == "runtime").ToList();
        Assert.Equal([2, 2], shown.Select(r => r.GetProperty("used").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Recipes_are_persistent_by_default()
    {
        await Sim.AddRecipesAsync(new { name = "forever", when = new { contains = new[] { "k" } }, reply = new { content = "again" } });
        for (var i = 0; i < 5; i++) Assert.Equal("again", await Ask(Req("k")));
    }

    [Fact]
    public async Task Runtime_recipes_accept_one_or_many_are_listed_and_can_be_removed_without_touching_the_profile()
    {
        var fileRecipes = (await Sim.Get("/_sim/recipes")).Json.GetArrayLength();
        Assert.Equal(12, fileRecipes);

        var one = await Sim.Post("/_sim/recipes", new { name = "single", when = new { contains = new[] { "s" } }, reply = new { content = "single" } });
        Assert.Equal(201, one.Status);
        Assert.Equal(1, one.Json.GetProperty("added").GetInt32());
        Assert.Equal(13, one.Json.GetProperty("total").GetInt32());
        await Sim.AddRecipesAsync(new { name = "m1", reply = new { content = "m1" }, when = new { contains = new[] { "m1" } } }, new { name = "m2", when = new { contains = new[] { "m2" } }, reply = new { content = "m2" } });

        var listed = (await Sim.Get("/_sim/recipes")).Json.EnumerateArray().ToList();
        Assert.Equal(15, listed.Count);
        Assert.Equal(12, listed.Count(r => r.GetProperty("source").GetString() == "file"));
        Assert.Equal(["single", "m1", "m2"], listed.Where(r => r.GetProperty("source").GetString() == "runtime").Select(r => r.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("m2", await Ask(Req("m2")));

        Assert.Equal(204, (await Sim.Delete("/_sim/recipes")).Status);
        var remaining = (await Sim.Get("/_sim/recipes")).Json.EnumerateArray().ToList();
        Assert.Equal(12, remaining.Count);
        Assert.All(remaining, r => Assert.Equal("file", r.GetProperty("source").GetString()));
        Assert.Contains("simulated", await Ask(Req("m2")));
        // the built-in hostile recipes still work after the runtime ones are gone
        Assert.Contains("evil.example", await Ask(Req("[[llmsim:hostile:pipe-to-shell]]")));
    }

    public static TheoryData<string, string> BadRecipes => new()
    {
        { "", "Send one recipe or a non-empty array of recipes." },
        { "\"text\"", "Send one recipe or a non-empty array of recipes." },
        { "[]", "Send one recipe or a non-empty array of recipes." },
        { "{\"reply\":{\"content\":\"a\"}}", "A recipe needs a 'name'." },
        { "{\"name\":\"x\"}", "Recipe 'x' needs a 'reply'." },
        { "{\"name\":\"x\",\"when\":{\"regex\":\"(\"},\"reply\":{\"content\":\"a\"}}", "Recipe 'x': invalid regex:" },
        { "{\"name\":\"x\",\"when\":{\"containz\":[\"a\"]},\"reply\":{\"content\":\"a\"}}", "Invalid JSON body:" },
        { "{\"name\":\"x\",\"reply\":{\"status\":418}}", "Recipe 'x': status must be one of:" },
        { "{\"name\":\"x\",\"when\":{\"minMessages\":-1},\"reply\":{\"content\":\"a\"}}", "Recipe 'x': minMessages must be >= 0." },
        { "[{\"name\":\"ok\",\"reply\":{\"content\":\"a\"}},{\"name\":\"bad\"}]", "Recipe 'bad' needs a 'reply'." },
    };

    [Theory]
    [MemberData(nameof(BadRecipes))]
    public async Task Invalid_recipes_are_rejected_and_nothing_is_added(string body, string message)
    {
        var response = await Sim.Post("/_sim/recipes", body);
        Assert.Equal(400, response.Status);
        Assert.StartsWith(message, response.Error.GetProperty("message").GetString());
        Assert.Equal(12, (await Sim.Get("/_sim/recipes")).Json.GetArrayLength());
    }
}

public sealed class RecipeFileTests
{
    [Fact]
    public async Task A_recipes_file_replaces_the_default_profile_and_its_order_beats_runtime_recipes()
    {
        using var dir = new TempDir();
        var file = dir.Write("recipes.json", """
            {
              // comments and trailing commas are allowed in recipe files
              "recipes": [
                { "name": "file-one", "when": { "contains": ["k"] }, "reply": { "content": "from file" } },
              ]
            }
            """);
        await using var host = await SimHost.StartAsync(c => Options(c, recipes: file));
        await host.AddRecipesAsync(new { name = "runtime-one", when = new { contains = new[] { "k" } }, reply = new { content = "from runtime" } });
        Assert.Equal("from file", (await host.Chat(Req("k"))).Content);
        // the hostile library belongs to the default profile only
        Assert.Contains("simulated", (await host.Chat(Req("[[llmsim:hostile:pipe-to-shell]]"))).Content);
        Assert.Equal(["file-one", "runtime-one"], (await host.Get("/_sim/recipes")).Json.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_recipes_file_may_be_a_bare_array_and_the_default_profile_can_be_named()
    {
        using var dir = new TempDir();
        var file = dir.Write("recipes.json", """[ { "name": "bare", "reply": { "content": "bare array" } } ]""");
        await using var host = await SimHost.StartAsync(c => Options(c, recipes: file));
        Assert.Equal("bare array", (await host.Chat(Req("anything"))).Content);

        await using var named = await SimHost.StartAsync(c => Options(c, recipes: "default"));
        Assert.Equal(12, (await named.Get("/_sim/recipes")).Json.GetArrayLength());
    }

    [Fact]
    public async Task A_broken_recipes_file_stops_the_simulator_with_a_clear_error()
    {
        using var dir = new TempDir();
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => SimHost.StartAsync(c => Options(c, recipes: Path.Combine(dir.Path, "nope.json"))));
        Assert.Contains("was not found", missing.Message);
        var badRegex = dir.Write("regex.json", """[ { "name": "bad", "when": { "regex": "(" }, "reply": { "content": "x" } } ]""");
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => SimHost.StartAsync(c => Options(c, recipes: badRegex)));
        Assert.Contains("Recipe 'bad': invalid regex", invalid.Message);
        var typo = dir.Write("typo.json", """[ { "name": "typo", "replyy": {} } ]""");
        await Assert.ThrowsAsync<JsonException>(() => SimHost.StartAsync(c => Options(c, recipes: typo)));
        var missingContent = dir.Write("content.json", """[ { "name": "c", "reply": { "contentFrom": "absent.txt" } } ]""");
        var absent = await Assert.ThrowsAsync<InvalidDataException>(() => SimHost.StartAsync(c => Options(c, recipes: missingContent)));
        Assert.Contains("contentFrom 'absent.txt' was not found", absent.Message);
    }

    [Fact]
    public async Task ContentFrom_reads_a_file_next_to_the_recipes_file_and_substitutes_only_the_three_tokens()
    {
        using var dir = new TempDir();
        dir.Write("answers/echo.txt", "model={{model}} messages={{messageCount}} last=[{{lastUserMessage}}] keep={{other}} gh=${{ secrets.X }} again={{model}}");
        var file = dir.Write("recipes.json", """
            [
              { "name": "echo", "when": { "contains": ["echo"] }, "reply": { "contentFrom": "answers/echo.txt" } },
              { "name": "inline", "when": { "contains": ["inline"] }, "reply": { "content": "verbatim {{model}} {{messageCount}}" } }
            ]
            """);
        await using var host = await SimHost.StartAsync(c => Options(c, recipes: file));

        var echo = await host.Chat(ReqMessages("llama-sim", ("system", "s"), ("user", "echo please")));
        Assert.Equal("model=llama-sim messages=2 last=[echo please] keep={{other}} gh=${{ secrets.X }} again=llama-sim", echo.Content);

        // substituted values are not scanned again, and the file is read at answer time
        Assert.Equal("model=gpt-sim messages=1 last=[echo {{model}}] keep={{other}} gh=${{ secrets.X }} again=gpt-sim", (await host.Chat(Req("echo {{model}}"))).Content);
        dir.Write("answers/echo.txt", "changed {{model}}");
        Assert.Equal("changed gpt-sim", (await host.Chat(Req("echo"))).Content);

        // inline content is returned verbatim
        Assert.Equal("verbatim {{model}} {{messageCount}}", (await host.Chat(Req("inline"))).Content);
    }

    [Fact]
    public async Task Runtime_contentFrom_may_use_embedded_files_and_files_inside_the_recipes_directory_only()
    {
        using var dir = new TempDir();
        dir.Write("ok.txt", "from disk");
        var file = dir.Write("recipes.json", "[]");
        await using var host = await SimHost.StartAsync(c => Options(c, recipes: file));

        await host.AddRecipesAsync(new { name = "disk", when = new { contains = new[] { "disk" } }, reply = new { contentFrom = "ok.txt" } },
            new { name = "embedded", when = new { contains = new[] { "embedded" } }, reply = new { contentFrom = "good/minimal.yml" } });
        Assert.Equal("from disk", (await host.Chat(Req("disk"))).Content);
        Assert.StartsWith("name: CI", (await host.Chat(Req("embedded"))).Content);

        foreach (var escape in new[] { "../outside.txt", "/etc/hostname", "answers/../../x", "absent.txt" })
        {
            var rejected = await host.Post("/_sim/recipes", new { name = "x", reply = new { contentFrom = escape } });
            Assert.Equal(400, rejected.Status);
            Assert.Contains("contentFrom", rejected.Error.GetProperty("message").GetString());
        }

        // without a recipes file only embedded names exist
        await using var plain = await SimHost.StartAsync();
        Assert.Equal(400, (await plain.Post("/_sim/recipes", new { name = "x", reply = new { contentFrom = "/etc/hostname" } })).Status);
        Assert.Equal(201, (await plain.Post("/_sim/recipes", new { name = "x", reply = new { contentFrom = "hostile/wrong-schema.yml" } })).Status);
    }

    [Fact]
    public async Task Embedded_template_substitutions_reach_scripted_replies_too()
    {
        using var dir = new TempDir();
        dir.Write("t.txt", "{{model}}/{{messageCount}}");
        var file = dir.Write("recipes.json", "[]");
        await using var host = await SimHost.StartAsync(c => Options(c, recipes: file));
        await host.ScriptAsync(new { contentFrom = "t.txt" });
        Assert.Equal("llama-sim/3", (await host.Chat(ReqMessages("llama-sim", ("user", "a"), ("assistant", "b"), ("user", "c")))).Content);
    }
}
