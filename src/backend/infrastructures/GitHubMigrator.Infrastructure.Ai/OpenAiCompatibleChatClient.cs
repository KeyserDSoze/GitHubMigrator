using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHubMigrator.Business;

namespace GitHubMigrator.Infrastructure.Ai;

// One adapter for every provider that speaks the OpenAI chat-completions dialect: OpenAI, Azure OpenAI (v1 and
// deployment routes), vLLM, Ollama, LiteLLM, LM Studio. The HttpClient must not follow redirects (a redirect would
// carry the credential to another host) and must not impose its own timeout (each attempt has its own).
public sealed partial class OpenAiCompatibleChatClient(HttpClient http, IManagedIdentityTokenSource? tokens = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? clock = null) : IChatModelClient
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "system", "developer", "user", "assistant" };
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex SafeCode();

    public async Task<ChatResult> CompleteAsync(ChatEndpoint endpoint, string? secret, ChatRequest request, CancellationToken ct)
    {
        Validate(endpoint, secret, request);
        var url = BuildUrl(endpoint);
        var body = BuildBody(endpoint, request);
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                var result = await SendOnceAsync(endpoint, secret, url, body, ct);
                return result with { Attempts = attempts };
            }
            catch (ChatModelException ex)
            {
                ex.Attempts = attempts;
                var wait = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempts - 1));
                // A provider that asks us to wait longer than we are willing to is reported, not hammered.
                if (!ex.Retryable || attempts > endpoint.MaxRetries || wait > MaxRetryDelay) throw;
                await _delay(wait, ct);
            }
        }
    }

    private static void Validate(ChatEndpoint endpoint, string? secret, ChatRequest request)
    {
        if (endpoint.Auth == AiAuthMode.ApiKey && string.IsNullOrEmpty(secret))
            throw new ChatModelException(ChatFailure.Misconfigured, "The connection needs an API key.");
        if (endpoint.Auth == AiAuthMode.ManagedIdentity && endpoint.Provider != AiProvider.AzureOpenAI)
            throw new ChatModelException(ChatFailure.Misconfigured, "Managed identity is only available for Azure OpenAI.");
        if (string.IsNullOrWhiteSpace(endpoint.Model))
            throw new ChatModelException(ChatFailure.Misconfigured, "The connection has no model.");
        if (request.Messages.Count == 0 || request.Messages.Any(m => !Roles.Contains(m.Role) || m.Content is null))
            throw new ChatModelException(ChatFailure.BadRequest, "The request has no valid messages.");
        var bytes = request.Messages.Sum(m => (long)Encoding.UTF8.GetByteCount(m.Content));
        if (bytes > endpoint.MaxInputBytes)
            throw new ChatModelException(ChatFailure.InputTooLarge, $"The input is larger than the {endpoint.MaxInputBytes} bytes allowed for this connection.");
    }

    internal static Uri BuildUrl(ChatEndpoint endpoint)
    {
        if (!Uri.TryCreate(endpoint.BaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new ChatModelException(ChatFailure.Misconfigured, "The connection endpoint is not a valid URL.");
        var root = baseUri.GetLeftPart(UriPartial.Authority);
        if (endpoint.Provider == AiProvider.AzureOpenAI)
            return string.IsNullOrWhiteSpace(endpoint.ApiVersion)
                ? new($"{root}/openai/v1/chat/completions")
                : new($"{root}/openai/deployments/{Uri.EscapeDataString(endpoint.Model)}/chat/completions?api-version={Uri.EscapeDataString(endpoint.ApiVersion)}");
        var path = baseUri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/chat/completions", StringComparison.Ordinal) ? new(root + path) : new(root + path + "/chat/completions");
    }

    internal static string BuildBody(ChatEndpoint endpoint, ChatRequest request)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("model", endpoint.Model);
            json.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                json.WriteStartObject();
                json.WriteString("role", message.Role);
                json.WriteString("content", message.Content);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            var limit = Math.Min(request.MaxOutputTokens ?? endpoint.MaxOutputTokens, endpoint.MaxOutputTokens);
            json.WriteNumber(endpoint.TokenParameter == TokenLimitParameter.MaxCompletionTokens ? "max_completion_tokens" : "max_tokens", limit);
            if (endpoint.Temperature is { } temperature) json.WriteNumber("temperature", temperature);
            if (request.Seed is { } seed) json.WriteNumber("seed", seed);
            if (request.JsonObject)
            {
                json.WriteStartObject("response_format");
                json.WriteString("type", "json_object");
                json.WriteEndObject();
            }
            json.WriteBoolean("stream", false);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<ChatResult> SendOnceAsync(ChatEndpoint endpoint, string? secret, Uri url, string body, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(endpoint.Timeout ?? ChatEndpoint.DefaultTimeout);
        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await ApplyAuthAsync(message, endpoint, secret, attempt.Token);

        HttpResponseMessage? response = null;
        try
        {
            byte[]? payload;
            try
            {
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                payload = await ReadBoundedAsync(response.Content, attempt.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ChatModelException(ChatFailure.Timeout, "The model did not answer in time.");
            }
            catch (HttpRequestException)
            {
                throw new ChatModelException(ChatFailure.Unavailable, "The model endpoint could not be reached.", retryable: true);
            }

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                throw new ChatModelException(ChatFailure.Unavailable, "The model endpoint redirected the request; redirects are not followed.", httpStatus: status);
            if (!response.IsSuccessStatusCode) throw MapError(response, payload, status);
            if (payload is null)
                throw new ChatModelException(ChatFailure.MalformedResponse, "The model answer is larger than allowed.", httpStatus: status);
            return ParseSuccess(response, payload);
        }
        finally { response?.Dispose(); }
    }

    private async Task ApplyAuthAsync(HttpRequestMessage message, ChatEndpoint endpoint, string? secret, CancellationToken ct)
    {
        switch (endpoint.Auth)
        {
            case AiAuthMode.ApiKey when endpoint.Provider == AiProvider.AzureOpenAI:
                message.Headers.TryAddWithoutValidation("api-key", secret);
                break;
            case AiAuthMode.ApiKey:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;
            case AiAuthMode.ManagedIdentity:
                if (tokens is null)
                    throw new ChatModelException(ChatFailure.Misconfigured, "Managed identity is not available in this deployment.");
                try { message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ChatModelException(ChatFailure.Unauthorized, "A managed identity token could not be obtained.");
                }
                break;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes) return null;
        }
        return buffer.ToArray();
    }

    private ChatModelException MapError(HttpResponseMessage response, byte[]? payload, int status)
    {
        var (code, type, message, innerCode) = ReadError(payload);
        var retryAfter = ReadRetryAfter(response);
        var providerCode = code ?? type;
        bool Mentions(params string[] needles) => message is not null && needles.Any(n => message.Contains(n, StringComparison.OrdinalIgnoreCase));

        if (code is "content_filter" || string.Equals(innerCode, "ResponsibleAIPolicyViolation", StringComparison.OrdinalIgnoreCase))
            return new(ChatFailure.ContentFiltered, "The provider's content filter rejected the request.", httpStatus: status, providerCode: providerCode);
        if (code is "context_length_exceeded" || status == 413 || (status == 400 && Mentions("maximum context length", "context length", "too many tokens", "reduce the length")))
            return new(ChatFailure.ContextLengthExceeded, "The input is longer than the model's context window.", httpStatus: status, providerCode: providerCode);
        return status switch
        {
            401 or 403 => new(ChatFailure.Unauthorized, "The provider rejected the credential.", httpStatus: status, providerCode: providerCode),
            404 => new(ChatFailure.ModelNotFound, "The model or deployment was not found at this endpoint.", httpStatus: status, providerCode: providerCode),
            408 => new(ChatFailure.Timeout, "The provider timed out.", httpStatus: status, providerCode: providerCode),
            429 when code is "insufficient_quota" => new(ChatFailure.RateLimited, "The provider account has no quota left.", httpStatus: status, providerCode: providerCode),
            429 => new(ChatFailure.RateLimited, "The provider is rate limiting requests.", retryable: true, retryAfter: retryAfter, httpStatus: status, providerCode: providerCode),
            502 or 503 or 504 => new(ChatFailure.Unavailable, "The provider is temporarily unavailable.", retryable: true, retryAfter: retryAfter, httpStatus: status, providerCode: providerCode),
            >= 500 => new(ChatFailure.Unavailable, "The provider failed to process the request.", httpStatus: status, providerCode: providerCode),
            _ => new(ChatFailure.BadRequest, "The provider rejected the request.", httpStatus: status, providerCode: providerCode)
        };
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var wait = date - _clock.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    private static (string? Code, string? Type, string? Message, string? InnerCode) ReadError(byte[]? payload)
    {
        if (payload is null || payload.Length == 0) return default;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return default;
            string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value)
                ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null } : null;
            string? Safe(string? value) => value is not null && SafeCode().IsMatch(value) ? value : null;
            var inner = error.TryGetProperty("innererror", out var innerElement) && innerElement.ValueKind == JsonValueKind.Object ? Text(innerElement, "code") : null;
            // the message is only inspected for known phrases, never copied into an exception
            return (Safe(Text(error, "code")), Safe(Text(error, "type")), Text(error, "message"), Safe(inner));
        }
        catch (JsonException) { return default; }
    }

    private static ChatResult ParseSuccess(HttpResponseMessage response, byte[] payload)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new ChatModelException(ChatFailure.MalformedResponse, "The endpoint answered with something that is not JSON (a proxy or login page?).", httpStatus: (int)response.StatusCode);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw Malformed("The answer has no choices.");
            var choice = choices[0];
            var finish = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() ?? "" : "";
            if (finish == "content_filter")
                throw new ChatModelException(ChatFailure.ContentFiltered, "The provider's content filter stopped the answer.");
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                throw Malformed("The answer has no message.");
            if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refusal.GetString()))
                throw new ChatModelException(ChatFailure.Refused, "The model refused to answer.");
            var content = message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            if (string.IsNullOrEmpty(content)) throw Malformed("The answer is empty.");

            ChatUsage? usage = null;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object &&
                u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var prompt) &&
                u.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var completion))
                usage = new(prompt, completion);
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return new(content, finish, usage, model, 1);
        }
        catch (JsonException) { throw Malformed("The answer is not valid JSON."); }
    }

    private static ChatModelException Malformed(string message) => new(ChatFailure.MalformedResponse, message);
}
