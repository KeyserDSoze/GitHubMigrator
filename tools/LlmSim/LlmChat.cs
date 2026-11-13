using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LlmSim;

public sealed record ChatMessage(string Role, string Text);

public sealed record PromptView(string Model, IReadOnlyList<ChatMessage> Messages)
{
    public string AllText { get; } = string.Join("\n", Messages.Select(m => m.Text));
    public string SystemText { get; } = string.Join("\n", Messages.Where(m => m.Role is "system" or "developer").Select(m => m.Text));
    public string LastUserText { get; } = Messages.LastOrDefault(m => m.Role == "user")?.Text ?? "";
    public long Chars { get; } = Messages.Sum(m => (long)m.Text.Length);
}

public sealed record ChatRequest(
    string Model, IReadOnlyList<ChatMessage> Messages, int? MaxTokens, bool Stream, bool IncludeUsage, string ResponseFormat);

public sealed record AssistantMessage(string Role, string? Content, string? Refusal);
public sealed record Choice(int Index, AssistantMessage Message, string? Logprobs, string FinishReason);
public sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record ChatCompletion(
    string Id, [property: JsonPropertyName("object")] string ObjectType, long Created, string Model,
    string SystemFingerprint, IReadOnlyList<Choice> Choices, Usage Usage);

public sealed record Delta(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null);
public sealed record ChunkChoice(int Index, Delta Delta, string? Logprobs, string? FinishReason);

public sealed record ChatChunk(
    string Id, [property: JsonPropertyName("object")] string ObjectType, long Created, string Model,
    string SystemFingerprint, IReadOnlyList<ChunkChoice> Choices);

// With stream_options.include_usage every chunk carries "usage": null except the last one.
public sealed record ChatChunkWithUsage(
    string Id, [property: JsonPropertyName("object")] string ObjectType, long Created, string Model,
    string SystemFingerprint, IReadOnlyList<ChunkChoice> Choices, Usage? Usage);

public sealed record ModelInfo(string Id, [property: JsonPropertyName("object")] string ObjectType, long Created, string OwnedBy);
public sealed record ModelList([property: JsonPropertyName("object")] string ObjectType, IReadOnlyList<ModelInfo> Data);

public sealed record ContentFilterCategory(bool Filtered, string Severity);
public sealed record ContentFilterResult(
    ContentFilterCategory Hate, ContentFilterCategory SelfHarm, ContentFilterCategory Sexual, ContentFilterCategory Violence);
public sealed record InnerError(string Code, ContentFilterResult ContentFilterResult);
public sealed record ContentFilterBody(string Message, string? Type, string? Param, string Code, int Status, InnerError Innererror);
public sealed record ContentFilterEnvelope(ContentFilterBody Error);

internal static class ChatParser
{
    private static readonly string[] Roles = ["developer", "system", "user", "assistant", "tool"];

    public const string NotJson =
        "We could not parse the JSON body of your request. (HINT: This likely means you aren't using your HTTP library correctly. " +
        "The OpenAI API expects a JSON payload, but what was sent was not valid JSON. If you have trouble figuring out how to fix this, " +
        "please contact us through our help center at help.openai.com.)";

    private static ApiError Missing(string param) =>
        new(400, $"Missing required parameter: '{param}'.", param, "missing_required_parameter");

    private static ApiError InvalidType(string param, string expected, JsonElement actual) =>
        new(400, $"Invalid type for '{param}': expected {expected}, but got {Describe(actual)} instead.", param, "invalid_type");

    private static ApiError InvalidValue(string param, string message) => new(400, message, param, "invalid_value");

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => value.TryGetInt64(out _) ? "an integer" : "a decimal number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "null"
    };

    public static (ChatRequest? Request, ApiError? Error) Parse(
        JsonElement root, string? deployment, IReadOnlyList<string> models, RequestRecord record)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, new ApiError(400, NotJson));

        string model;
        if (deployment is not null) model = deployment;
        else if (!root.TryGetProperty("model", out var modelValue) || modelValue.ValueKind == JsonValueKind.Null)
            return (null, new ApiError(400, "you must provide a model parameter"));
        else if (modelValue.ValueKind != JsonValueKind.String) return (null, InvalidType("model", "a string", modelValue));
        else model = modelValue.GetString()!;
        record.Model = model;

        if (!root.TryGetProperty("messages", out var messagesValue) || messagesValue.ValueKind == JsonValueKind.Null)
            return (null, Missing("messages"));
        if (messagesValue.ValueKind != JsonValueKind.Array) return (null, InvalidType("messages", "an array of objects", messagesValue));
        record.MessageCount = messagesValue.GetArrayLength();
        if (record.MessageCount == 0)
            return (null, new ApiError(400, "Invalid 'messages': empty array. Expected an array with minimum length 1, but got an empty array instead.", "messages", "empty_array"));

        var messages = new List<ChatMessage>();
        var index = 0;
        foreach (var message in messagesValue.EnumerateArray())
        {
            var where = $"messages[{index++}]";
            if (message.ValueKind != JsonValueKind.Object) return (null, InvalidType(where, "an object", message));
            if (!message.TryGetProperty("role", out var role) || role.ValueKind == JsonValueKind.Null) return (null, Missing(where + ".role"));
            if (role.ValueKind != JsonValueKind.String) return (null, InvalidType(where + ".role", "a string", role));
            var roleName = role.GetString()!;
            if (!Roles.Contains(roleName))
                return (null, InvalidValue(where + ".role", $"Invalid value: '{roleName}'. Supported values are: 'developer', 'system', 'user', 'assistant', and 'tool'."));

            string text;
            if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
            {
                if (roleName != "assistant") return (null, Missing(where + ".content"));
                text = "";
            }
            else if (content.ValueKind == JsonValueKind.String) text = content.GetString()!;
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                var partIndex = 0;
                foreach (var part in content.EnumerateArray())
                {
                    var partWhere = $"{where}.content[{partIndex++}]";
                    if (part.ValueKind != JsonValueKind.Object) return (null, InvalidType(partWhere, "an object", part));
                    if (!part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return (null, Missing(partWhere + ".type"));
                    if (type.GetString() != "text")
                        return (null, InvalidValue(partWhere + ".type", $"Invalid value: '{type.GetString()}'. Supported values are: 'text'."));
                    if (!part.TryGetProperty("text", out var partText) || partText.ValueKind != JsonValueKind.String) return (null, Missing(partWhere + ".text"));
                    parts.Add(partText.GetString()!);
                }
                text = string.Join("\n", parts);
            }
            else return (null, InvalidType(where + ".content", "one of a string or array of objects", content));
            messages.Add(new ChatMessage(roleName, text));
        }

        int? maxTokens = null;
        foreach (var name in new[] { "max_tokens", "max_completion_tokens" })
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var tokens)) return (null, InvalidType(name, "an integer", value));
            if (tokens < 1)
                return (null, new ApiError(400, $"Invalid '{name}': integer below minimum value. Expected a value >= 1, but got {tokens} instead.", name, "integer_below_min_value"));
            if (name == "max_completion_tokens" || maxTokens is null) maxTokens = (int)Math.Min(tokens, int.MaxValue);
        }

        if (root.TryGetProperty("temperature", out var temperature) && temperature.ValueKind != JsonValueKind.Null)
        {
            if (temperature.ValueKind != JsonValueKind.Number) return (null, InvalidType("temperature", "a decimal number", temperature));
            var t = temperature.GetDouble();
            if (t < 0) return (null, new ApiError(400, $"Invalid 'temperature': decimal below minimum value. Expected a value >= 0, but got {t} instead.", "temperature", "decimal_below_min_value"));
            if (t > 2) return (null, new ApiError(400, $"Invalid 'temperature': decimal above maximum value. Expected a value <= 2, but got {t} instead.", "temperature", "decimal_above_max_value"));
        }

        if (root.TryGetProperty("seed", out var seed) && seed.ValueKind != JsonValueKind.Null &&
            (seed.ValueKind != JsonValueKind.Number || !seed.TryGetInt64(out _)))
            return (null, InvalidType("seed", "an integer", seed));

        if (root.TryGetProperty("n", out var n) && n.ValueKind != JsonValueKind.Null)
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt64(out var count)) return (null, InvalidType("n", "an integer", n));
            if (count < 1) return (null, new ApiError(400, $"Invalid 'n': integer below minimum value. Expected a value >= 1, but got {count} instead.", "n", "integer_below_min_value"));
            if (count > 1) return (null, new ApiError(400, "Unsupported value: 'n' must be 1 with llm-sim (a single choice is returned).", "n", "unsupported_value"));
        }

        var stream = false;
        if (root.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind != JsonValueKind.Null)
        {
            if (streamValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, InvalidType("stream", "a boolean", streamValue));
            stream = streamValue.GetBoolean();
        }

        var includeUsage = false;
        if (root.TryGetProperty("stream_options", out var options) && options.ValueKind != JsonValueKind.Null)
        {
            if (options.ValueKind != JsonValueKind.Object) return (null, InvalidType("stream_options", "an object", options));
            if (!stream) return (null, new ApiError(400, "The 'stream_options' parameter is only allowed when 'stream' is enabled.", "stream_options"));
            if (options.TryGetProperty("include_usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
            {
                if (usage.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, InvalidType("stream_options.include_usage", "a boolean", usage));
                includeUsage = usage.GetBoolean();
            }
        }

        var format = "text";
        if (root.TryGetProperty("response_format", out var responseFormat) && responseFormat.ValueKind != JsonValueKind.Null)
        {
            if (responseFormat.ValueKind != JsonValueKind.Object) return (null, InvalidType("response_format", "an object", responseFormat));
            if (!responseFormat.TryGetProperty("type", out var type) || type.ValueKind == JsonValueKind.Null) return (null, Missing("response_format.type"));
            if (type.ValueKind != JsonValueKind.String) return (null, InvalidType("response_format.type", "a string", type));
            format = type.GetString()!;
            if (format is not ("text" or "json_object" or "json_schema"))
                return (null, InvalidValue("response_format.type", $"Invalid value: '{format}'. Supported values are: 'text', 'json_object', and 'json_schema'."));
            if (format == "json_schema" && (!responseFormat.TryGetProperty("json_schema", out var schema) || schema.ValueKind != JsonValueKind.Object))
                return (null, Missing("response_format.json_schema"));
        }

        if (!models.Contains(model))
            return (null, deployment is null
                ? new ApiError(404, $"The model `{model}` does not exist or you do not have access to it.", null, "model_not_found")
                : new ApiError(404, "The API deployment for this resource does not exist. If you created the deployment within the last 5 minutes, please wait a moment and try again.",
                    Code: "DeploymentNotFound", Azure: true));

        if (format == "json_object" && !messages.Any(m => m.Text.Contains("json", StringComparison.OrdinalIgnoreCase)))
            return (null, new ApiError(400, "'messages' must contain the word 'json' in some form, to use 'response_format' of type 'json_object'.", "messages"));

        return (new ChatRequest(model, messages, maxTokens, stream, includeUsage, format), null);
    }
}

internal static class Answers
{
    public const string AuditMarker = "Audit a disabled Azure Pipelines to GitHub Actions migration scaffold";

    // Exactly seven lines and no preface: the product's reviewer keeps every non-empty line as one checklist item.
    public static readonly string[] AuditChecklist =
    [
        "1. Task translation: map each Azure Pipelines task to a GitHub Actions step and keep unsupported tasks disabled until reviewed.",
        "2. Triggers: replace CI, PR and schedule triggers with explicit on: events and branch filters; keep the workflow disabled.",
        "3. Runners: map agent pools to GitHub-hosted or self-hosted runner labels and confirm they exist.",
        "4. Permissions: set least-privilege permissions at workflow and job level, defaulting to contents: read.",
        "5. OIDC: prefer short-lived OIDC federation over stored cloud credentials.",
        "6. Secrets: map secret names only; secret values are unknown and must be added by an owner in GitHub.",
        "7. Tests: add test jobs and a dry run, and enable the workflow only after human review; nothing is deployed by this scaffold."
    ];

    private static readonly Regex Token = new(@"\{\{(model|messageCount|lastUserMessage)\}\}", RegexOptions.CultureInvariant);

    // One pass: substituted values are never re-scanned for tokens.
    public static string Substitute(string template, PromptView prompt) => Token.Replace(template, m => m.Groups[1].Value switch
    {
        "model" => prompt.Model,
        "messageCount" => prompt.Messages.Count.ToString(),
        _ => prompt.LastUserText
    });

    public static string Default(PromptView prompt, ChatRequest request)
    {
        if (prompt.SystemText.Contains(AuditMarker, StringComparison.Ordinal)) return string.Join("\n", AuditChecklist);
        var count = prompt.Messages.Count;
        var sentence = $"This is a simulated answer from llm-sim for model {prompt.Model}. I received {count} message{(count == 1 ? "" : "s")}.";
        return request.ResponseFormat is "json_object" or "json_schema"
            ? JsonSerializer.Serialize(new { answer = sentence, message_count = count }, LlmJson.Wire)
            : sentence;
    }

    public static int Tokens(long chars) => (int)((chars + 3) / 4);

    public static string Cut(string text, long maxChars)
    {
        if (text.Length <= maxChars) return text;
        var length = (int)maxChars;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    public static List<string> Split(string text, int size)
    {
        var parts = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var length = Math.Min(size, text.Length - i);
            if (i + length < text.Length && char.IsHighSurrogate(text[i + length - 1])) length++;
            parts.Add(text.Substring(i, length));
            i += length;
        }
        return parts;
    }

    // The id depends only on the model and the request with insignificant whitespace removed.
    public static string RequestId(JsonElement root, string model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) root.WriteTo(writer);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(model + "\n" + Encoding.UTF8.GetString(stream.ToArray())));
        return "chatcmpl-" + Convert.ToHexStringLower(hash)[..29];
    }
}
