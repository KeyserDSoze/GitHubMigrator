using System.Text;
using System.Text.Json;

namespace LlmSim;

public static partial class LlmSimApp
{
    private const long ModelCreated = 1_700_000_000;
    private const string Fingerprint = "fp_llm_sim";
    private const int StreamChunkChars = 24;

    private static void MapOpenAi(WebApplication app, LlmState sim)
    {
        foreach (var prefix in new[] { "/v1", "/openai/v1" })
        {
            app.MapPost(prefix + "/chat/completions", (HttpContext ctx) => Chat(ctx, sim, null));
            app.MapGet(prefix + "/models", (HttpContext ctx) =>
                WriteJsonAsync(ctx, 200, new ModelList("list", sim.Models.Select(m => new ModelInfo(m, "model", ModelCreated, "llm-sim")).ToList())));
            app.MapGet(prefix + "/models/{**id}", async (HttpContext ctx, string id) =>
            {
                if (sim.Models.Contains(id)) await WriteJsonAsync(ctx, 200, new ModelInfo(id, "model", ModelCreated, "llm-sim"));
                else await WriteErrorAsync(ctx, new ApiError(404, $"The model '{id}' does not exist", "model", "model_not_found"));
            });
        }
        app.MapPost("/openai/deployments/{deployment}/chat/completions",
            (HttpContext ctx, string deployment) => Chat(ctx, sim, deployment));

        app.MapFallback(ctx =>
        {
            var path = ctx.Request.Path.Value ?? "";
            var chatPath = path is "/v1/chat/completions" or "/openai/v1/chat/completions" ||
                (path.StartsWith("/openai/deployments/", StringComparison.Ordinal) && path.EndsWith("/chat/completions", StringComparison.Ordinal));
            return WriteErrorAsync(ctx, chatPath
                ? new ApiError(405, "Method Not Allowed", Code: "method_not_allowed")
                : new ApiError(404, $"Invalid URL ({ctx.Request.Method} {path})"));
        });
    }

    private static Task WriteJsonAsync(HttpContext ctx, int status, object body) =>
        WriteBytesAsync(ctx, status, "application/json", JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), LlmJson.Wire), 0, false);

    // validation -> answer selection (queue, recipes, default) -> optional delay -> fault or completion
    private static async Task Chat(HttpContext ctx, LlmState sim, string? deployment)
    {
        var record = Rec(ctx);
        if (deployment is not null && string.IsNullOrWhiteSpace(ctx.Request.Query["api-version"]))
        {
            await WriteErrorAsync(ctx, new ApiError(400, "Missing required query parameter: api-version", Code: "BadRequest", Azure: true));
            return;
        }
        if (record.Doc is null)
        {
            await WriteErrorAsync(ctx, new ApiError(400, ChatParser.NotJson));
            return;
        }
        var root = record.Doc.RootElement;
        var (request, error) = ChatParser.Parse(root, deployment, sim.Models, record);
        if (request is null)
        {
            await WriteErrorAsync(ctx, error!);
            return;
        }

        var prompt = new PromptView(request.Model, request.Messages);
        var (reply, matched) = sim.Select(prompt);
        record.Matched = matched;
        var promptTokens = reply?.Usage?.Prompt ?? Answers.Tokens(prompt.Chars);
        record.PromptTokens = promptTokens;
        var hangMs = reply?.HangMs ?? 0;

        if (reply?.DelayMs is > 0 and var delay) await Task.Delay(delay, ctx.RequestAborted);
        if (reply?.Headers is { } headers)
            foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;

        if (reply?.Status is { } status && reply.ContentType is null)
        {
            var retryAfter = reply.RetryAfterSeconds ?? (status == 429 ? 1 : null);
            if (retryAfter is { } seconds)
            {
                ctx.Response.Headers["Retry-After"] = seconds.ToString();
                ctx.Response.Headers["retry-after-ms"] = (seconds * 1000L).ToString();
            }
            await WriteErrorAsync(ctx, ErrorFor(status, request.Model, promptTokens, record.PresentedKey, reply.ContextLengthExceeded, null, IsAzure(ctx), retryAfter), hangMs);
            return;
        }

        if (reply?.ContentType is { } contentType)
        {
            var raw = reply.Content ?? (reply.ContentFrom is { } rawSource ? Answers.Substitute(sim.Content.Read(rawSource), prompt) : "");
            await WriteBytesAsync(ctx, reply.Status ?? 200, contentType, Encoding.UTF8.GetBytes(raw), hangMs, reply.StreamAbort);
            return;
        }

        if (reply is { ContentFilter: true } && !request.Stream)
        {
            var filtered = new ContentFilterEnvelope(new ContentFilterBody(
                "The response was filtered due to the prompt triggering Azure OpenAI's content management policy. Please modify your prompt and retry. To learn more about our content filtering policies please read our documentation: https://go.microsoft.com/fwlink/?linkid=2198766",
                null, "prompt", "content_filter", 400,
                new InnerError("ResponsibleAIPolicyViolation", new ContentFilterResult(
                    new ContentFilterCategory(true, "high"), new ContentFilterCategory(false, "safe"),
                    new ContentFilterCategory(false, "safe"), new ContentFilterCategory(false, "safe")))));
            await WriteBytesAsync(ctx, 400, "application/json", JsonSerializer.SerializeToUtf8Bytes(filtered, LlmJson.Wire), hangMs, false);
            return;
        }

        var content = reply?.Content ??
            (reply?.ContentFrom is { } source ? Answers.Substitute(sim.Content.Read(source), prompt) : Answers.Default(prompt, request));
        var finish = reply?.FinishReason ?? "stop";
        if (reply is { ContentFilter: true })
        {
            finish = "content_filter";
            content = Answers.Cut(content, content.Length / 2);
        }
        if (request.MaxTokens is { } max && finish != "content_filter" && content.Length > max * 4L)
        {
            content = Answers.Cut(content, max * 4L);
            finish = "length";
        }
        var completionTokens = reply?.Usage?.Completion ?? (reply?.EmptyChoices == true ? 0 : Answers.Tokens(content.Length));
        record.CompletionTokens = completionTokens;
        var usage = new Usage(promptTokens, completionTokens, promptTokens + completionTokens);
        var id = Answers.RequestId(root, request.Model);
        var created = sim.Now.ToUnixTimeSeconds();

        if (request.Stream)
        {
            record.Streamed = true;
            await StreamAsync(ctx, request, reply, id, created, content, finish, usage);
            return;
        }

        var choices = reply?.EmptyChoices == true
            ? []
            : new List<Choice> { new(0, new AssistantMessage("assistant", content, null), null, finish) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ChatCompletion(id, "chat.completion", created, request.Model, Fingerprint, choices, usage), LlmJson.Wire);
        if (reply?.MalformedJson == true) bytes = bytes[..(bytes.Length * 6 / 10)];
        await WriteBytesAsync(ctx, 200, "application/json", bytes, hangMs, reply?.StreamAbort == true);
    }

    private static async Task StreamAsync(HttpContext ctx, ChatRequest request, Reply? reply, string id, long created,
        string content, string finish, Usage usage)
    {
        object Chunk(List<ChunkChoice> choices, Usage? usageValue = null) => request.IncludeUsage
            ? new ChatChunkWithUsage(id, "chat.completion.chunk", created, request.Model, Fingerprint, choices, usageValue)
            : new ChatChunk(id, "chat.completion.chunk", created, request.Model, Fingerprint, choices);
        string Json(object chunk) => JsonSerializer.Serialize(chunk, chunk.GetType(), LlmJson.Wire);
        byte[] Frame(string json) => Encoding.UTF8.GetBytes("data: " + json + "\n\n");
        ChunkChoice Choice(Delta delta, string? finishReason = null) => new(0, delta, null, finishReason);

        var frames = new List<byte[]>();
        var contentFrames = 0;
        if (reply?.EmptyChoices == true)
        {
            frames.Add(Frame(Json(Chunk([]))));
            if (request.IncludeUsage) frames.Add(Frame(Json(Chunk([], usage))));
        }
        else if (reply?.MalformedJson == true)
        {
            var json = Json(Chunk([Choice(new Delta("assistant", ""))]));
            frames.Add(Frame(json[..(json.Length * 6 / 10)]));
        }
        else
        {
            frames.Add(Frame(Json(Chunk([Choice(new Delta("assistant", ""))]))));
            foreach (var part in Answers.Split(content, StreamChunkChars))
            {
                frames.Add(Frame(Json(Chunk([Choice(new Delta(null, part))]))));
                contentFrames++;
            }
            frames.Add(Frame(Json(Chunk([Choice(new Delta(), finish)]))));
            if (request.IncludeUsage) frames.Add(Frame(Json(Chunk([], usage))));
        }
        frames.Add(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));

        var abortAfter = reply?.StreamAbort == true ? Math.Min(frames.Count - 1, 1 + Math.Min(2, contentFrames)) : -1;
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        for (var i = 0; i < frames.Count; i++)
        {
            await ctx.Response.Body.WriteAsync(frames[i], ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            if (i == 0 && reply?.HangMs is > 0 and var hang) await Task.Delay(hang, ctx.RequestAborted);
            if (abortAfter == i + 1)
            {
                await Task.Delay(50, ctx.RequestAborted);
                ctx.Abort();
                return;
            }
        }
    }
}
