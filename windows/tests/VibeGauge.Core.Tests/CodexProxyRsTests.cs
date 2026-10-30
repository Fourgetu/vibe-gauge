using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class CodexProxyRsTests
{
    [Theory]
    [InlineData("https://site.example", "https://site.example")]
    [InlineData("https://site.example/prefix/v1/", "https://site.example/prefix")]
    [InlineData("https://site.example/prefix/v1/usage", "https://site.example/prefix")]
    [InlineData("http://localhost:8080/v1", "http://localhost:8080")]
    public void NormalizesRootWithoutDiscardingPathPrefix(string input, string expected) => Assert.Equal(expected, CodexProxyRsEndpoint.Normalize(input));

    [Theory]
    [InlineData("http://remote.example")]
    [InlineData("https://user:password@site.example")]
    [InlineData("https://site.example?key=secret")]
    public void RejectsUnsafeEndpoint(string input) => Assert.Throws<ArgumentException>(() => CodexProxyRsEndpoint.Normalize(input));

    [Fact]
    public void DecimalStringBudgetsAndUnlimitedWindowsUseServerSemantics()
    {
        using var doc = JsonDocument.Parse("""{"unit":"USD","daily":{"total":"10","used":"2.25","remaining":"7.75","resetsAt":"2030-01-02T00:00:00Z"},"weekly":{"total":null,"used":"20.5","remaining":null,"resetsAt":null}}""");
        var value = CodexProxyRsParser.Budget("Site", doc.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal(22, value.Daily!.UsedPercent); Assert.Null(value.Weekly);
        Assert.Contains("不限额", value.Detail); Assert.Contains("7.75", value.Detail);
        Assert.Equal(CodexProxyRsParser.Kind, value.Tier);
    }
    [Fact]
    public void RejectsMalformedOrUnsuccessfulPayloadInsteadOfZeroUsage()
    {
        foreach (var json in new[] { "{}", """{"code":40101,"data":{}}""", """{"code":200,"data":{}}""" })
        {
            using var doc = JsonDocument.Parse(json);
            Assert.Throws<JsonException>(() => CodexProxyRsParser.Overview(doc.RootElement));
        }
    }
    [Fact]
    public void TokensDoNotDoubleCountCachedOrReasoningAndUnknownCostStaysUnknown()
    {
        using var doc = JsonDocument.Parse("""{"requests":2,"inputTokens":100,"outputTokens":20,"cachedInputTokens":80,"cacheWriteInputTokens":5,"reasoningOutputTokens":10,"totalTokens":120,"costUsd":null,"costIncomplete":true}""");
        var m = CodexProxyRsParser.Metrics(doc.RootElement);
        Assert.Equal(120, m.Total); Assert.Null(m.CostUsd); Assert.True(m.CostIncomplete);
        Assert.Equal(240, RemoteUsageMetrics.Sum([m, m]).Total);
    }
    [Fact]
    public void RepeatedSnapshotsAndCorrectionsReplaceBucketsAndPreserveOlderHistory()
    {
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var m = new RemoteUsageMetrics(1, 100, 20, 80, 0, 5, 120, 1m, false);
        var old = new RemoteUsageBucket(start.AddDays(-1), 86400, "09-30", m);
        var bucket = new RemoteUsageBucket(start, 86400, "10-01", m);
        var period = new RemoteUsagePeriod(start, start.AddHours(12), m, [bucket], "ready");
        var merged = RemoteUsageHistory.Merge([old], period);
        merged = RemoteUsageHistory.Merge(merged, period);
        Assert.Equal(2, merged.Count); Assert.Equal(240, RemoteUsageMetrics.Sum(merged.Select(x => x.Metrics)).Total);
        var corrected = bucket with { Metrics = m with { Total = 130 } };
        merged = RemoteUsageHistory.Merge(merged, period with { Buckets = [corrected] });
        Assert.Equal(250, RemoteUsageMetrics.Sum(merged.Select(x => x.Metrics)).Total);
    }
}
