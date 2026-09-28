using System.Text;
using System.Text.Json;
using VibeGauge.Proxy.Streaming;
using VibeGauge.Proxy.Usage;
using Xunit;

namespace VibeGauge.Proxy.Tests;

public sealed class UpstreamProtocolTests
{
    [Fact]
    public void ResponsesCompletedParsesNestedUsage()
    {
        var usage = new UsageAccumulator(null);
        UsageParser.Apply("""{"type":"response.completed","response":{"model":"fixture","usage":{"input_tokens":120,"output_tokens":30,"input_tokens_details":{"cached_tokens":80}}}}"""u8, usage);
        Assert.Equal(120, usage.ContextTokens); Assert.Equal(30, usage.OutputTokens); Assert.Equal(80, usage.CacheReadTokens);
        Assert.Equal("fixture", usage.Model);
    }
    [Fact]
    public void GeminiThinkingIsIncludedInOutputExactlyOnce()
    {
        var usage = new UsageAccumulator(null);
        for (var i = 0; i < 2; i++) UsageParser.Apply("""{"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":20,"thoughtsTokenCount":7,"cachedContentTokenCount":50}}"""u8, usage);
        Assert.Equal(27, usage.OutputTokens); Assert.Equal(7, usage.ThinkingTokens);
    }
    [Fact]
    public void NdjsonCountsOllamaFinalSnapshot()
    {
        var usage = new UsageAccumulator(null);
        var parser = new SseParser(usage, ndjson: true);
        parser.Push("{\"response\":\"piece\"}\n{\"done\":true,\"prompt_eval_count\":80,\"eval_count\":9}\n"u8);
        parser.Complete();
        Assert.Equal(80, usage.ContextTokens); Assert.Equal(9, usage.OutputTokens);
    }
    [Fact]
    public void SseSupportsMultilineEventsAndPartialUtf8()
    {
        var usage = new UsageAccumulator(null);
        var parser = new SseParser(usage);
        var bytes = Encoding.UTF8.GetBytes("data: {\"model\":\"测试\",\ndata: \"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}\n\n");
        foreach (var value in bytes) parser.Push(new[] { value });
        parser.Complete();
        Assert.Equal(10, usage.ContextTokens); Assert.Equal("测试", usage.Model); Assert.Null(usage.Error);
    }
    [Fact]
    public void MalformedNumericUsageNeverCrashesForwarding()
    {
        var usage = new UsageAccumulator(null);
        UsageParser.Apply("""{"usage":{"input_tokens":"invalid","output_tokens":-2}}"""u8, usage);
        Assert.False(usage.Parsed);
    }
    [Fact]
    public void TimeoutSupportsLongerThanOneMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ProxyOptions.Parse(["--timeout-ms=600000"]).UpstreamTimeout);
    }
    [Fact]
    public async Task AccountsProduceDifferentFingerprintsAndConfirmUpstreamReached()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        foreach (var key in new[] { "fixture-key-a", "fixture-key-b" })
        {
            using var request = fixture.Post("/openai-json", """{"model":"fixture"}""");
            request.Headers.Authorization = new("Bearer", key);
            using var response = await fixture.Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        _ = await fixture.WaitForRecordAsync(2);
        var records = File.ReadAllLines(fixture.LogPath).Select(x => JsonDocument.Parse(x)).ToArray();
        try
        {
            Assert.NotEqual(records[0].RootElement.GetProperty("key").GetString(), records[1].RootElement.GetProperty("key").GetString());
            Assert.All(records, r => Assert.True(r.RootElement.GetProperty("reached_upstream").GetBoolean()));
            Assert.DoesNotContain("fixture-key", File.ReadAllText(fixture.LogPath));
        }
        finally { foreach (var r in records) r.Dispose(); }
    }
}
