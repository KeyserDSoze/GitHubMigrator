using System.Text.Json;
using Xunit;
using static LlmSim.Tests.SimHost;

namespace LlmSim.Tests;

public sealed class ValidationTests(LlmSimFixture fixture) : SimTests(fixture), IClassFixture<LlmSimFixture>
{
    private const string User = """[{"role":"user","content":"x"}]""";
    private static string Body(string extra, string messages = User, string model = "\"gpt-sim\"") =>
        "{\"model\":" + model + ",\"messages\":" + messages + (extra.Length == 0 ? "" : "," + extra) + "}";

    // body, expected status, error.code (null = JSON null), error.param (null = JSON null), message prefix
    public static TheoryData<string, int, string?, string?, string> Rejected => new()
    {
        { "{\"model\":\"gpt-sim\",\"messages\":", 400, null, null, "We could not parse the JSON body of your request." },
        { "[1,2]", 400, null, null, "We could not parse the JSON body of your request." },
        { "", 400, null, null, "We could not parse the JSON body of your request." },
        { "{\"messages\":" + User + "}", 400, null, null, "you must provide a model parameter" },
        { "{\"model\":null,\"messages\":" + User + "}", 400, null, null, "you must provide a model parameter" },
        { Body("", model: "5"), 400, "invalid_type", "model", "Invalid type for 'model': expected a string, but got an integer instead." },
        { "{\"model\":\"gpt-sim\"}", 400, "missing_required_parameter", "messages", "Missing required parameter: 'messages'." },
        { Body("", "[]"), 400, "empty_array", "messages", "Invalid 'messages': empty array. Expected an array with minimum length 1, but got an empty array instead." },
        { Body("", "\"hi\""), 400, "invalid_type", "messages", "Invalid type for 'messages': expected an array of objects, but got a string instead." },
        { Body("", "[\"hi\"]"), 400, "invalid_type", "messages[0]", "Invalid type for 'messages[0]': expected an object, but got a string instead." },
        { Body("", "[{\"content\":\"x\"}]"), 400, "missing_required_parameter", "messages[0].role", "Missing required parameter: 'messages[0].role'." },
        { Body("", "[{\"role\":\"user\",\"content\":\"x\"},{\"role\":\"wizard\",\"content\":\"x\"}]"), 400, "invalid_value", "messages[1].role", "Invalid value: 'wizard'. Supported values are:" },
        { Body("", "[{\"role\":\"user\"}]"), 400, "missing_required_parameter", "messages[0].content", "Missing required parameter: 'messages[0].content'." },
        { Body("", "[{\"role\":\"user\",\"content\":5}]"), 400, "invalid_type", "messages[0].content", "Invalid type for 'messages[0].content': expected one of a string or array of objects, but got an integer instead." },
        { Body("", "[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"x\"}}]}]"), 400, "invalid_value", "messages[0].content[0].type", "Invalid value: 'image_url'. Supported values are: 'text'." },
        { Body("", "[{\"role\":\"user\",\"content\":[{\"type\":\"text\"}]}]"), 400, "missing_required_parameter", "messages[0].content[0].text", "Missing required parameter: 'messages[0].content[0].text'." },
        { Body("\"n\":2"), 400, "unsupported_value", "n", "Unsupported value: 'n' must be 1" },
        { Body("\"n\":0"), 400, "integer_below_min_value", "n", "Invalid 'n': integer below minimum value." },
        { Body("\"n\":\"1\""), 400, "invalid_type", "n", "Invalid type for 'n': expected an integer, but got a string instead." },
        { Body("\"max_tokens\":0"), 400, "integer_below_min_value", "max_tokens", "Invalid 'max_tokens': integer below minimum value. Expected a value >= 1, but got 0 instead." },
        { Body("\"max_tokens\":\"5\""), 400, "invalid_type", "max_tokens", "Invalid type for 'max_tokens': expected an integer, but got a string instead." },
        { Body("\"max_tokens\":1.5"), 400, "invalid_type", "max_tokens", "Invalid type for 'max_tokens': expected an integer, but got a decimal number instead." },
        { Body("\"max_completion_tokens\":-3"), 400, "integer_below_min_value", "max_completion_tokens", "Invalid 'max_completion_tokens': integer below minimum value." },
        { Body("\"temperature\":3"), 400, "decimal_above_max_value", "temperature", "Invalid 'temperature': decimal above maximum value. Expected a value <= 2, but got 3 instead." },
        { Body("\"temperature\":-0.5"), 400, "decimal_below_min_value", "temperature", "Invalid 'temperature': decimal below minimum value." },
        { Body("\"temperature\":\"hot\""), 400, "invalid_type", "temperature", "Invalid type for 'temperature': expected a decimal number, but got a string instead." },
        { Body("\"seed\":\"abc\""), 400, "invalid_type", "seed", "Invalid type for 'seed': expected an integer, but got a string instead." },
        { Body("\"stream\":\"yes\""), 400, "invalid_type", "stream", "Invalid type for 'stream': expected a boolean, but got a string instead." },
        { Body("\"stream_options\":{\"include_usage\":true}"), 400, null, "stream_options", "The 'stream_options' parameter is only allowed when 'stream' is enabled." },
        { Body("\"stream\":true,\"stream_options\":{\"include_usage\":\"y\"}"), 400, "invalid_type", "stream_options.include_usage", "Invalid type for 'stream_options.include_usage'" },
        { Body("\"response_format\":\"json\""), 400, "invalid_type", "response_format", "Invalid type for 'response_format': expected an object, but got a string instead." },
        { Body("\"response_format\":{}"), 400, "missing_required_parameter", "response_format.type", "Missing required parameter: 'response_format.type'." },
        { Body("\"response_format\":{\"type\":\"yaml\"}"), 400, "invalid_value", "response_format.type", "Invalid value: 'yaml'. Supported values are: 'text', 'json_object', and 'json_schema'." },
        { Body("\"response_format\":{\"type\":\"json_schema\"}"), 400, "missing_required_parameter", "response_format.json_schema", "Missing required parameter: 'response_format.json_schema'." },
        { Body("\"response_format\":{\"type\":\"json_object\"}"), 400, null, "messages", "'messages' must contain the word 'json' in some form, to use 'response_format' of type 'json_object'." },
        { Body("", model: "\"gpt-9000\""), 404, "model_not_found", null, "The model `gpt-9000` does not exist or you do not have access to it." },
        { Body("", model: "\"\""), 404, "model_not_found", null, "The model `` does not exist or you do not have access to it." },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Invalid_requests_are_rejected_with_openais_error_shape(string body, int status, string? code, string? param, string message)
    {
        var response = await Sim.Chat(body);
        Assert.Equal(status, response.Status);
        Assert.Equal(["message", "type", "param", "code"], response.Error.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.StartsWith(message, response.Error.GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", response.Error.GetProperty("type").GetString());
        AssertNullable(param, response.Error.GetProperty("param"));
        AssertNullable(code, response.Error.GetProperty("code"));
    }

    private static void AssertNullable(string? expected, JsonElement actual)
    {
        if (expected is null) Assert.Equal(JsonValueKind.Null, actual.ValueKind);
        else Assert.Equal(expected, actual.GetString());
    }

    public static TheoryData<string> Accepted => new()
    {
        Body(""),
        Body("", "[{\"role\":\"system\",\"content\":\"s\"},{\"role\":\"developer\",\"content\":\"d\"},{\"role\":\"user\",\"content\":\"u\"},{\"role\":\"assistant\",\"content\":\"a\"},{\"role\":\"tool\",\"content\":\"t\"}]"),
        Body("", "[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"},{\"type\":\"text\",\"text\":\"two\"}]}]"),
        Body("", "[{\"role\":\"user\",\"content\":\"x\"},{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[]}]"),
        Body("\"temperature\":0,\"seed\":42,\"n\":1,\"max_tokens\":50"),
        Body("\"temperature\":2,\"top_p\":0.1,\"user\":\"u-1\",\"tools\":[],\"max_completion_tokens\":10"),
        Body("\"temperature\":null,\"max_tokens\":null,\"stop\":null,\"stream\":false,\"stream_options\":null"),
        Body("\"response_format\":{\"type\":\"text\"}"),
        Body("\"response_format\":{\"type\":\"json_object\"}", "[{\"role\":\"system\",\"content\":\"Reply in JSON.\"},{\"role\":\"user\",\"content\":\"x\"}]"),
        Body("\"response_format\":{\"type\":\"json_object\"}", "[{\"role\":\"user\",\"content\":\"give me Json please\"}]"),
        Body("\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"r\",\"schema\":{\"type\":\"object\"}}}"),
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task Valid_requests_are_accepted(string body)
    {
        var response = await Sim.Chat(body);
        Assert.Equal(200, response.Status);
        Assert.Equal("chat.completion", response.Json.GetProperty("object").GetString());
    }

    [Fact]
    public async Task Multi_part_content_is_joined_and_counted_like_a_plain_string()
    {
        await Sim.AddRecipesAsync(new { name = "parts", when = new { contains = new[] { "one\ntwo" } }, reply = new { content = "joined" } });
        var response = await Sim.Chat(Body("", "[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"},{\"type\":\"text\",\"text\":\"two\"}]}]"));
        Assert.Equal("joined", response.Content);
    }

    [Fact]
    public async Task Rejected_requests_never_reach_answer_selection()
    {
        await Sim.ScriptAsync(new { content = "scripted" });
        Assert.Equal(400, (await Sim.Chat("{}")).Status);
        Assert.Equal(404, (await Sim.Chat(Req("hi", "nope"))).Status);
        Assert.Equal(401, (await Sim.Chat(Req("hi"), key: "bad")).Status);
        Assert.Equal(1, (await Sim.Get("/_sim/script")).Json.GetProperty("remaining").GetInt32());
        Assert.Equal("scripted", (await Sim.Chat(Req("hi"))).Content);
    }
}
