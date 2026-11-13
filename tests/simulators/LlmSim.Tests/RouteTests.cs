using System.Text.Json;
using Xunit;
using static LlmSim.Tests.SimHost;

namespace LlmSim.Tests;

public sealed class RouteTests(LlmSimFixture fixture) : SimTests(fixture), IClassFixture<LlmSimFixture>
{
    private const string Classic = "/openai/deployments/gpt-sim/chat/completions";

    [Fact]
    public async Task The_azure_v1_surface_answers_exactly_like_the_openai_one()
    {
        var body = Req("same on both", "gpt-sim", ("temperature", 0));
        var openai = await Sim.Chat(body);
        var azure = await Sim.Chat(body, "/openai/v1/chat/completions");
        Assert.Equal(200, azure.Status);
        Assert.Equal(openai.Text, azure.Text);

        // api-version is not needed on the v1 surface, and the api-key header works
        Assert.Equal(200, (await Sim.Chat(body, "/openai/v1/chat/completions?api-version=preview", key: Key, keyHeader: "api-key")).Status);
        // validation is the OpenAI one on both
        Assert.Equal("model_not_found", (await Sim.Chat(Req("x", "nope"), "/openai/v1/chat/completions")).Error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Azure_classic_requires_an_api_version_but_accepts_any_value()
    {
        var missing = await Sim.Chat(new { messages = new[] { new { role = "user", content = "hi" } } }, Classic);
        Assert.Equal(400, missing.Status);
        Assert.Equal(["error"], missing.Json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("BadRequest", missing.Error.GetProperty("code").GetString());
        Assert.Equal("Missing required query parameter: api-version", missing.Error.GetProperty("message").GetString());
        Assert.Equal(400, (await Sim.Chat(Req("hi"), Classic + "?api-version=")).Status);

        foreach (var version in new[] { "2024-10-21", "2025-04-01-preview", "banana", "v1" })
            Assert.Equal(200, (await Sim.Chat(Req("hi"), $"{Classic}?api-version={version}")).Status);
    }

    [Fact]
    public async Task The_deployment_name_acts_as_the_model_and_the_body_model_is_ignored()
    {
        var body = new { messages = new[] { new { role = "user", content = "hi" } } };
        var response = await Sim.Chat(body, "/openai/deployments/llama-sim/chat/completions?api-version=2024-10-21");
        Assert.Equal(200, response.Status);
        Assert.Equal("llama-sim", response.Json.GetProperty("model").GetString());
        Assert.Contains("model llama-sim", response.Content);

        var ignored = await Sim.Chat(Req("hi", "does-not-matter"), $"{Classic}?api-version=2024-10-21");
        Assert.Equal("gpt-sim", ignored.Json.GetProperty("model").GetString());

        var unknown = await Sim.Chat(body, "/openai/deployments/nope/chat/completions?api-version=2024-10-21");
        Assert.Equal(404, unknown.Status);
        Assert.Equal(["code", "message"], unknown.Error.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("DeploymentNotFound", unknown.Error.GetProperty("code").GetString());
        Assert.StartsWith("The API deployment for this resource does not exist.", unknown.Error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Azure_classic_works_with_the_api_key_header_and_a_recipe_can_select_on_the_deployment()
    {
        await Sim.AddRecipesAsync(new { name = "only-llama", when = new { model = "llama-sim" }, reply = new { content = "llama answer" } });
        var body = new { messages = new[] { new { role = "user", content = "hi" } } };
        var llama = await Sim.Chat(body, "/openai/deployments/llama-sim/chat/completions?api-version=1", key: Key, keyHeader: "api-key");
        Assert.Equal("llama answer", llama.Content);
        var gpt = await Sim.Chat(body, "/openai/deployments/gpt-sim/chat/completions?api-version=1", key: Key, keyHeader: "api-key");
        Assert.Contains("simulated answer", gpt.Content);
    }

    [Fact]
    public async Task Unknown_paths_are_404_and_wrong_methods_on_chat_are_405()
    {
        var unknown = await Sim.Get("/v1/nope");
        Assert.Equal(404, unknown.Status);
        Assert.Equal("Invalid URL (GET /v1/nope)", unknown.Error.GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", unknown.Error.GetProperty("type").GetString());
        Assert.Equal(404, (await Sim.Post("/v1/completions", Req("hi"))).Status);
        Assert.Equal(405, (await Sim.Get("/v1/chat/completions")).Status);
        Assert.Equal(405, (await Sim.Get(Classic + "?api-version=1")).Status);
        Assert.Equal(404, (await Sim.Get("/_sim/unknown")).Status);
    }
}
