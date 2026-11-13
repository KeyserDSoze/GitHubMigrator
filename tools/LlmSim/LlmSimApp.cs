using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LlmSim;

// An OpenAI-compatible LLM simulator: /v1 (OpenAI), /openai/v1 and /openai/deployments/{name} (Azure OpenAI).
// Answers are deterministic; scripted, recipe-driven and hostile answers and faults let tests exercise a client
// without a model, and the request log proves exactly what was sent to it.
public static partial class LlmSimApp
{
    private const string RecordKey = "llmsim.record";
    private const int MaxLoggedBodyBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> MaskedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "api-key", "x-api-key", "Ocp-Apim-Subscription-Key", "Proxy-Authorization", "Cookie"
    };

    public static async Task<WebApplication> StartAsync(LlmSimOptions options)
    {
        var state = new LlmState(options);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64L * 1024 * 1024);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.Use((ctx, next) => Gate(ctx, next, state));
        app.MapGet("/healthz", () => Results.Text("ok"));
        MapSimEndpoints(app, state);
        MapOpenAi(app, state);
        await app.StartAsync();
        return app;
    }

    public static RequestRecord Rec(HttpContext ctx) => (RequestRecord)ctx.Items[RecordKey]!;

    // ---- middleware ----------------------------------------------------------------------

    // Authentication, the full request log, global faults and last-resort error handling for everything but /healthz.
    private static async Task Gate(HttpContext ctx, RequestDelegate next, LlmState sim)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path == "/healthz") { await next(ctx); return; }
        var isControl = path == "/_sim" || path.StartsWith("/_sim/", StringComparison.Ordinal);
        var record = new RequestRecord();
        ctx.Items[RecordKey] = record;
        var watch = Stopwatch.StartNew();
        ctx.Response.Headers["x-request-id"] = "req_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var buffer = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
                record.Bytes = buffer.ToArray();
            }
            if (record.Bytes.Length > 0)
                try { record.Doc = JsonDocument.Parse(record.Bytes); }
                catch (JsonException) { }

            var (valid, presented) = sim.Authenticate(ctx);
            record.Authenticated = valid;
            record.ApiKeyHint = Hint(presented);
            record.PresentedKey = presented;
            if (!valid && sim.RequireKey)
            {
                await WriteErrorAsync(ctx, Unauthorized(ctx, presented));
                return;
            }
            if (!isControl && sim.TakeFault(path) is { } fault)
            {
                var error = ErrorFor(fault.Status, record.Model ?? "", 0, presented, false, fault.Message, IsAzure(ctx), fault.RetryAfterSeconds);
                if (fault.RetryAfterSeconds is { } seconds) ctx.Response.Headers["Retry-After"] = seconds.ToString();
                await WriteErrorAsync(ctx, error);
                return;
            }
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 499;
        }
        catch (BadHttpRequestException ex)
        {
            if (!ctx.Response.HasStarted) await WriteErrorAsync(ctx, new ApiError(ex.StatusCode, ex.Message, Type: "invalid_request_error"));
        }
        catch (Exception ex)
        {
            if (!ctx.Response.HasStarted)
                await WriteErrorAsync(ctx, new ApiError(500, "The server had an error while processing your request. Sorry about that! (" + ex.GetType().Name + ": " + ex.Message + ")", Type: "server_error"));
            else ctx.Abort();
        }
        finally
        {
            watch.Stop();
            if (!isControl) sim.Record(ToLogEntry(ctx, record, sim, watch.ElapsedMilliseconds));
            record.Doc?.Dispose();
        }
    }

    private static RequestLogEntry ToLogEntry(HttpContext ctx, RequestRecord record, LlmState sim, long latencyMs)
    {
        var headers = new Dictionary<string, string>();
        foreach (var (name, value) in ctx.Request.Headers) headers[name] = MaskedHeaders.Contains(name) ? "***" : value.ToString();
        var truncated = record.Bytes.Length > MaxLoggedBodyBytes;
        var body = Encoding.UTF8.GetString(record.Bytes, 0, Math.Min(record.Bytes.Length, MaxLoggedBodyBytes));
        return new RequestLogEntry(
            sim.Now, ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Request.QueryString.Value?.TrimStart('?') ?? "",
            ctx.Response.StatusCode, record.Authenticated, record.ApiKeyHint, headers, body, truncated,
            truncated ? null : record.Doc?.RootElement.Clone(), record.Model, record.MessageCount, record.Matched,
            record.PromptTokens, record.CompletionTokens, record.Streamed, latencyMs);
    }

    private static string? Hint(string? key) =>
        string.IsNullOrEmpty(key) ? null : key.Length <= 3 ? "***" : key[..3] + "***";

    private static string Mask(string key) =>
        key.Length <= 7 ? new string('*', key.Length) : key[..3] + new string('*', key.Length - 7) + key[^4..];

    private static bool IsAzure(HttpContext ctx) => ctx.Request.Path.StartsWithSegments("/openai");

    // ---- errors --------------------------------------------------------------------------

    private static ApiError Unauthorized(HttpContext ctx, string? presented)
    {
        if (IsAzure(ctx))
            return new ApiError(401, "Access denied due to invalid subscription key or wrong API endpoint. Make sure to provide a valid key for an active subscription and use a correct regional API endpoint for your resource.",
                Code: "401", Azure: true);
        return presented is null
            ? new ApiError(401, "You didn't provide an API key. You need to provide your API key in an Authorization header using Bearer auth (i.e. Authorization: Bearer YOUR_KEY), or as the password field (with blank username) if you're accessing the API from your browser and are prompted for a username and password. You can obtain an API key from https://platform.openai.com/account/api-keys.",
                Code: "invalid_api_key")
            : new ApiError(401, $"Incorrect API key provided: {Mask(presented)}. You can find your API key at https://platform.openai.com/account/api-keys.",
                Code: "invalid_api_key");
    }

    // The error a scripted status, a global fault or an HTTP-level problem produces.
    public static ApiError ErrorFor(int status, string model, int promptTokens, string? presentedKey, bool contextLength,
        string? message, bool azure, int? retryAfter = null)
    {
        switch (status)
        {
            case 400 when contextLength:
                return new ApiError(400, message ?? $"This model's maximum context length is 8192 tokens. However, your messages resulted in {Math.Max(promptTokens, 8193)} tokens. Please reduce the length of the messages.",
                    "messages", "context_length_exceeded");
            case 400: return new ApiError(400, message ?? "We could not process your request: the request body is invalid.");
            case 401:
                var unauthorized = azure
                    ? new ApiError(401, "Access denied due to invalid subscription key or wrong API endpoint. Make sure to provide a valid key for an active subscription and use a correct regional API endpoint for your resource.", Code: "401", Azure: true)
                    : new ApiError(401, $"Incorrect API key provided: {Mask(presentedKey ?? "sk-llm-sim-invalid")}. You can find your API key at https://platform.openai.com/account/api-keys.", Code: "invalid_api_key");
                return message is null ? unauthorized : unauthorized with { Message = message };
            case 403: return new ApiError(403, message ?? $"Project `proj_llm_sim` does not have access to model `{model}`", null, "model_not_found");
            case 404: return new ApiError(404, message ?? $"The model `{model}` does not exist or you do not have access to it.", null, "model_not_found");
            case 408: return new ApiError(408, message ?? "Request timed out. Please retry your request.", null, "request_timeout", "server_error");
            case 413: return new ApiError(413, message ?? "Request body too large. Reduce the size of the request and try again.", null, "request_too_large");
            case 429:
                return new ApiError(429, message ?? $"Rate limit reached for {(model.Length == 0 ? "requests" : model)} in organization org-llm-sim on requests per min (RPM): Limit 3, Used 3, Requested 1. Please try again in {retryAfter ?? 1}s. Visit https://platform.openai.com/account/rate-limits to learn more.",
                    null, "rate_limit_exceeded", "requests");
            case 502: return new ApiError(502, message ?? "Bad gateway.", null, null, "server_error");
            case 503: return new ApiError(503, message ?? "That model is currently overloaded with other requests. You can retry your request, or contact us through our help center at help.openai.com if the error persists.", null, null, "server_error");
            default: return new ApiError(status, message ?? "The server had an error while processing your request. Sorry about that!", null, null, "server_error");
        }
    }

    private static byte[] ErrorBytes(ApiError error) => error.Azure
        ? JsonSerializer.SerializeToUtf8Bytes(new AzureErrorEnvelope(new AzureErrorBody(error.Code ?? error.Status.ToString(), error.Message)), LlmJson.Wire)
        : JsonSerializer.SerializeToUtf8Bytes(new ErrorEnvelope(new ErrorBody(error.Message, error.Type, error.Param, error.Code)), LlmJson.Wire);

    public static Task WriteErrorAsync(HttpContext ctx, ApiError error, int hangMs = 0) =>
        WriteBytesAsync(ctx, error.Status, "application/json", ErrorBytes(error), hangMs, abort: false);

    // Headers go out first and the body is split in two, so a stall happens mid-body (and an abort truncates it).
    private static async Task WriteBytesAsync(HttpContext ctx, int status, string contentType, byte[] body, int hangMs, bool abort)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = body.Length;
        if (hangMs <= 0 && !abort)
        {
            await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
            return;
        }
        var half = body.Length / 2;
        await ctx.Response.Body.WriteAsync(body.AsMemory(0, half), ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        if (hangMs > 0) await Task.Delay(hangMs, ctx.RequestAborted);
        if (abort)
        {
            await Task.Delay(50, ctx.RequestAborted);
            ctx.Abort();
            return;
        }
        await ctx.Response.Body.WriteAsync(body.AsMemory(half), ctx.RequestAborted);
    }

    public static IResult SimError(int status, string message) =>
        Results.Json(new ErrorEnvelope(new ErrorBody(message, "invalid_request_error", null, null)), LlmJson.Wire, statusCode: status);
}
