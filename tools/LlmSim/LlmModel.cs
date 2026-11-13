using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmSim;

public sealed record LlmSimOptions(
    string Urls = "http://0.0.0.0:8080", IReadOnlyList<string>? ApiKeys = null, bool RequireKey = true,
    IReadOnlyList<string>? Models = null, string? Recipes = null, TimeProvider? Clock = null)
{
    public const string DefaultApiKey = "llm-sim-key";
    public static readonly string[] DefaultModels = ["gpt-sim", "llama-sim"];

    public IReadOnlyList<string> EffectiveApiKeys => ApiKeys is { Count: > 0 } ? ApiKeys : [DefaultApiKey];
    public IReadOnlyList<string> EffectiveModels => Models is { Count: > 0 } ? Models : DefaultModels;
}

public static class LlmJson
{
    // OpenAI wire shapes: snake_case, nulls are written (OpenAI has explicit nulls), no HTML escaping.
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Control plane and request log: camelCase, nulls written.
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Control plane echo of replies and recipes: defaults and nulls omitted.
    public static readonly JsonSerializerOptions Echo = new(Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    // Input from tests and recipe files: a misspelled field is an error, not a silent no-op.
    public static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public sealed record ReplyUsage(int? Prompt = null, int? Completion = null);

public sealed record Reply
{
    public string? Content { get; init; }
    public string? ContentFrom { get; init; }
    public string? FinishReason { get; init; }
    public int? Status { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public bool ContextLengthExceeded { get; init; }
    public int? DelayMs { get; init; }
    public int? HangMs { get; init; }
    public bool MalformedJson { get; init; }
    public bool EmptyChoices { get; init; }
    public string? ContentType { get; init; }
    public bool ContentFilter { get; init; }
    public bool StreamAbort { get; init; }
    public ReplyUsage? Usage { get; init; }
    public int? Repeat { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
}

public sealed record RecipeWhen
{
    public List<string>? Contains { get; init; }
    public List<string>? ContainsAny { get; init; }
    public string? Regex { get; init; }
    public string? System { get; init; }
    public string? LastUser { get; init; }
    public string? Model { get; init; }
    public int? MinMessages { get; init; }
}

public sealed record Recipe
{
    public string Name { get; init; } = "";
    public RecipeWhen? When { get; init; }
    public Reply? Reply { get; init; }
}

public sealed record RecipeFile(List<Recipe>? Recipes = null);

public sealed record ScriptRequest(List<Reply>? Responses = null);

public sealed class Fault
{
    public string? PathRegex { get; set; }
    public int Status { get; set; } = 500;
    public string? Message { get; set; }
    public int? Remaining { get; set; }
    public int? RetryAfterSeconds { get; set; }
}

public sealed record RequestLogEntry(
    DateTimeOffset At, string Method, string Path, string Query, int Status, bool Authenticated, string? ApiKeyHint,
    IReadOnlyDictionary<string, string> Headers, string Body, bool BodyTruncated, JsonElement? Json, string? Model,
    int? MessageCount, string Matched, int PromptTokens, int CompletionTokens, bool Streamed, long LatencyMs);

// What the handlers learn about a request while it is in flight; the gate turns it into a log entry.
public sealed class RequestRecord
{
    public byte[] Bytes { get; set; } = [];
    public JsonDocument? Doc { get; set; }
    public bool Authenticated { get; set; }
    public string? ApiKeyHint { get; set; }
    public string? PresentedKey { get; set; }
    public string? Model { get; set; }
    public int? MessageCount { get; set; }
    public string Matched { get; set; } = "none";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public bool Streamed { get; set; }
}

public sealed record ApiError(
    int Status, string Message, string? Param = null, string? Code = null, string Type = "invalid_request_error", bool Azure = false);

public sealed record ErrorBody(string Message, string? Type, string? Param, string? Code);
public sealed record ErrorEnvelope(ErrorBody Error);
public sealed record AzureErrorBody(string Code, string Message);
public sealed record AzureErrorEnvelope(AzureErrorBody Error);
