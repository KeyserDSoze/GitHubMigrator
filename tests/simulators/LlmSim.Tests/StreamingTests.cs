using System.Text.Json;
using Xunit;
using static LlmSim.Tests.SimHost;

namespace LlmSim.Tests;

public sealed class StreamingTests(LlmSimFixture fixture) : SimTests(fixture), IClassFixture<LlmSimFixture>
{
    private static Dictionary<string, object?> Streamed(string user = "hi", params (string Key, object? Value)[] extra) =>
        Req(user, "gpt-sim", [("stream", true), .. extra]);

    [Fact]
    public async Task A_stream_is_role_chunk_content_chunks_finish_chunk_and_done()
    {
        var text = "The quick brown fox jumps over the lazy dog. " + new string('z', 30);
        await Sim.ScriptAsync(new { content = text });
        var stream = await Sim.StreamAsync(Streamed());

        Assert.Equal(200, stream.Status);
        Assert.StartsWith("text/event-stream", stream.ContentType);
        Assert.Null(stream.Failure);
        Assert.Empty(stream.OtherLines);
        Assert.Equal("[DONE]", stream.Data[^1]);
        Assert.Single(stream.Data, d => d == "[DONE]");

        var chunks = stream.Chunks;
        var first = chunks[0];
        Assert.Equal(["id", "object", "created", "model", "system_fingerprint", "choices"], first.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("chat.completion.chunk", first.GetProperty("object").GetString());
        var firstChoice = first.GetProperty("choices")[0];
        Assert.Equal(["index", "delta", "logprobs", "finish_reason"], firstChoice.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("assistant", firstChoice.GetProperty("delta").GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, firstChoice.GetProperty("finish_reason").ValueKind);

        var contentChunks = chunks.Skip(1).SkipLast(1).ToList();
        Assert.Equal((int)Math.Ceiling(text.Length / 24.0), contentChunks.Count);
        Assert.All(contentChunks, chunk =>
        {
            var delta = chunk.GetProperty("choices")[0].GetProperty("delta");
            Assert.False(delta.TryGetProperty("role", out _));
            Assert.InRange(delta.GetProperty("content").GetString()!.Length, 1, 24);
            Assert.Equal(JsonValueKind.Null, chunk.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind);
        });
        Assert.Equal(text, stream.Text);

        var last = chunks[^1].GetProperty("choices")[0];
        Assert.Equal("stop", last.GetProperty("finish_reason").GetString());
        Assert.Empty(last.GetProperty("delta").EnumerateObject());

        Assert.Single(chunks.Select(c => c.GetProperty("id").GetString()).Distinct());
        Assert.Single(chunks.Select(c => c.GetProperty("created").GetInt64()).Distinct());
        Assert.All(chunks, c => Assert.Equal("gpt-sim", c.GetProperty("model").GetString()));
        Assert.All(chunks, c => Assert.False(c.TryGetProperty("usage", out _)));
    }

    [Fact]
    public async Task Usage_is_streamed_as_a_last_choiceless_chunk_when_requested()
    {
        await Sim.ScriptAsync(new { content = "0123456789" });
        var stream = await Sim.StreamAsync(Streamed("abcdefgh", ("stream_options", new { include_usage = true })));
        var chunks = stream.Chunks;
        Assert.Equal("[DONE]", stream.Data[^1]);

        var usage = chunks[^1];
        Assert.Equal(0, usage.GetProperty("choices").GetArrayLength());
        Assert.Equal(2, usage.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(3, usage.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal("stop", chunks[^2].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.All(chunks.SkipLast(1), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("usage").ValueKind));
        Assert.Equal("0123456789", stream.Text);

        var without = await Sim.StreamAsync(Streamed("abcdefgh", ("stream_options", new { include_usage = false })));
        Assert.All(without.Chunks, c => Assert.False(c.TryGetProperty("usage", out _)));
    }

    [Fact]
    public async Task A_streamed_answer_is_byte_identical_every_time()
    {
        var first = await Sim.StreamAsync(Streamed("same question"));
        var second = await Sim.StreamAsync(Streamed("same question"));
        Assert.Equal(first.Data, second.Data);
        Assert.NotEmpty(first.Text);
    }

    [Fact]
    public async Task Streaming_honours_max_tokens_and_reports_length()
    {
        await Sim.ScriptAsync(new { content = new string('q', 100) });
        var stream = await Sim.StreamAsync(Streamed("go", ("max_tokens", 3)));
        Assert.Equal(new string('q', 12), stream.Text);
        Assert.Equal("length", stream.Chunks[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task An_empty_answer_streams_only_the_role_and_finish_chunks()
    {
        await Sim.ScriptAsync(new { content = "" });
        var stream = await Sim.StreamAsync(Streamed());
        Assert.Equal(2, stream.Chunks.Count);
        Assert.Equal("", stream.Text);
        Assert.Equal("[DONE]", stream.Data[^1]);
    }

    [Fact]
    public async Task Unicode_survives_chunking_without_splitting_surrogate_pairs()
    {
        var text = string.Concat(Enumerable.Repeat("é😀", 40));
        await Sim.ScriptAsync(new { content = text });
        var stream = await Sim.StreamAsync(Streamed());
        Assert.Equal(text, stream.Text);
    }

    [Fact]
    public async Task A_streamed_request_that_is_rejected_gets_a_plain_json_error_not_an_event_stream()
    {
        var bad = await Sim.Chat(Req("hi", "nope", ("stream", true)));
        Assert.Equal(404, bad.Status);
        Assert.StartsWith("application/json", bad.ContentType);
        await Sim.ScriptAsync(new { status = 503 });
        var overloaded = await Sim.StreamAsync(Streamed());
        Assert.Equal(503, overloaded.Status);
        Assert.StartsWith("application/json", overloaded.ContentType);
        Assert.Empty(overloaded.Data);
        Assert.Contains("overloaded", string.Concat(overloaded.OtherLines));
    }

    [Theory]
    [InlineData("/openai/v1/chat/completions")]
    [InlineData("/openai/deployments/gpt-sim/chat/completions?api-version=2024-10-21")]
    public async Task Azure_routes_stream_too(string path)
    {
        var stream = await Sim.StreamAsync(Streamed(), path);
        Assert.Equal(200, stream.Status);
        Assert.Equal("[DONE]", stream.Data[^1]);
        Assert.Contains("I received 1 message", stream.Text);
    }

    [Fact]
    public async Task A_stream_can_be_cut_without_done_to_test_client_error_handling()
    {
        await Sim.ScriptAsync(new { content = new string('s', 300), streamAbort = true });
        var stream = await Sim.StreamAsync(Streamed());
        Assert.Equal(200, stream.Status);
        Assert.False(stream.Done);
        Assert.NotNull(stream.Failure);
        Assert.InRange(stream.Data.Count, 1, 3);
        Assert.Equal("assistant", stream.Chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        Assert.True(stream.Text.Length < 300);

        // the next request is unaffected
        var next = await Sim.StreamAsync(Streamed());
        Assert.True(next.Done);
        Assert.Null(next.Failure);
    }
}
