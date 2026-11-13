namespace GitHubMigrator.Business;

public enum AiProvider { OpenAICompatible, AzureOpenAI }

// None is for local servers (Ollama, LM Studio) that take no key. ManagedIdentity is Azure OpenAI only.
public enum AiAuthMode { None, ApiKey, ManagedIdentity }

// Newer OpenAI/Azure models reject max_tokens, many compatible servers only know it. Operator-visible, to confirm online.
public enum TokenLimitParameter { MaxTokens, MaxCompletionTokens }

public sealed record ChatEndpoint(
    AiProvider Provider,
    string BaseUrl,
    string Model,
    AiAuthMode Auth,
    string? ApiVersion = null,
    TimeSpan? Timeout = null,
    int MaxOutputTokens = 4096,
    double? Temperature = 0,
    int MaxInputBytes = 200_000,
    int MaxRetries = 2,
    TokenLimitParameter TokenParameter = TokenLimitParameter.MaxTokens)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
}

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
}

public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, int? MaxOutputTokens = null, int? Seed = null, bool JsonObject = false);

public sealed record ChatUsage(int PromptTokens, int CompletionTokens);

// FinishReason "length" means the answer was cut off: the caller decides what that means (a truncated workflow is
// unusable, a connection test deliberately asks for a few tokens).
public sealed record ChatResult(string Content, string FinishReason, ChatUsage? Usage, string? Model, int Attempts)
{
    public bool Truncated => FinishReason == "length";
}

// Stable codes: tests, the UI and the audit trail name them. Messages are fixed sentences and never echo the
// provider's body, which may quote the prompt.
public enum ChatFailure
{
    Misconfigured,
    InputTooLarge,
    Unauthorized,
    ModelNotFound,
    RateLimited,
    ContextLengthExceeded,
    ContentFiltered,
    Refused,
    BadRequest,
    Timeout,
    Unavailable,
    MalformedResponse
}

public sealed class ChatModelException(ChatFailure failure, string message, bool retryable = false,
    TimeSpan? retryAfter = null, int? httpStatus = null, string? providerCode = null) : Exception(message)
{
    public ChatFailure Failure { get; } = failure;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public int? HttpStatus { get; } = httpStatus;
    public string? ProviderCode { get; } = providerCode;
    public int Attempts { get; set; } = 1;
}

public interface IChatModelClient
{
    // The secret (API key) is passed per call and never stored by the client. Throws ChatModelException; caller
    // cancellation surfaces as OperationCanceledException.
    Task<ChatResult> CompleteAsync(ChatEndpoint endpoint, string? secret, ChatRequest request, CancellationToken ct);
}

public interface IManagedIdentityTokenSource
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

public static class AiHostDefaults
{
    // Same set the advisory reviewer has always allowed. Other hosts (api.openai.com, a self-hosted vLLM) are an
    // explicit operator decision in Ai:AllowedHosts.
    public static readonly string[] AzureHosts = ["*.openai.azure.com", "*.cognitiveservices.azure.com", "*.services.ai.azure.com"];
}
