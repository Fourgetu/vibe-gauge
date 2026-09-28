using System.Text.Json;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class TotalTokenTests
{
    [Theory]
    [InlineData(1000L, 200L, 800L, 100L, 150L, 1200L)]
    [InlineData(0L, 200L, 0L, 0L, 150L, 200L)]
    [InlineData(0L, 0L, 0L, 0L, 0L, 0L)]
    [InlineData(4_000_000_000L, 1_000_000_000L, 2_000_000_000L, 1000L, 10000L, 5_000_000_000L)]
    public void EveryUsageTypeCountsContextAndOutputExactlyOnce(long context, long output, long read, long write, long thinking, long expected)
    {
        var day = new DateOnly(2026, 9, 28);
        var record = new InteractionRecord("test", "Codex", "model", DateTimeOffset.Now, context, read, write, output, thinking);
        var source = new UsageSourceSummary("Codex", UsageDataState.Available, 1, context, read, write, output, thinking, "");
        var summary = new UsageSummary(1, context, read, write, output, thinking, [source], [record]);
        var provider = new ApiProviderSummary("site", 1, context, read, write, output, thinking);
        var apiModel = new ApiModelSummary("site", "model", 1, context, read, write, output, thinking, 0, 100);
        var apiRange = new ApiRangeSummary("today", "今日", 1, context, read, write, output, thinking, 0, 100, [provider], [apiModel]);
        var model = new ModelMix("Codex", "model", 1, context, output, read, write, thinking);
        var usageDay = new UsageDay(day, 1, context, output, read, write, thinking);
        var period = new UsagePeriod(day, day, 1, 1, context, output, read, write, thinking, [model]);
        Assert.All(new[] { record.TotalTokens, source.TotalTokens, summary.TotalTokens, provider.TotalTokens,
            apiModel.TotalTokens, apiRange.TotalTokens, model.TotalTokens, usageDay.TotalTokens, period.TotalTokens }, total => Assert.Equal(expected, total));
    }

    [Fact]
    public void SelectedDayAndRangeTotalsTieToTheirModelDistribution()
    {
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var records = new[] {
            new InteractionRecord("today", "Codex", "a", now, 1000, 800, 100, 200, 150),
            new InteractionRecord("old", "Codex", "b", now.AddDays(-8), 5000, 4500, 50, 400, 300),
            new InteractionRecord("old-pi", "PI-Desktop", "c", now.AddDays(-8), 2000, 1000, 100, 600, 400)
        };
        var stats = UsageStatistics.Build(records, now, TimeZoneInfo.Utc);
        var historical = stats.ForPeriod(today.AddDays(-8), today.AddDays(-8));
        Assert.Equal(8000, historical.TotalTokens);
        Assert.Equal(historical.TotalTokens, historical.Models.Sum(x => x.TotalTokens));
        Assert.Equal(1200, stats.ForPeriod(today.AddDays(-6), today).TotalTokens);
        Assert.Equal(9200, stats.ForPeriod(today.AddDays(-29), today).TotalTokens);
        Assert.Equal(0, stats.ForPeriod(today.AddDays(-1), today.AddDays(-1)).TotalTokens);
    }

    [Fact]
    public void DerivedTotalsDoNotChangePersistentRecordSchema()
    {
        var value = new InteractionRecord("test", "Codex", "model", DateTimeOffset.Now, 1000, 800, 100, 200, 150);
        var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("TotalTokens", json);
        Assert.Equal(1200, JsonSerializer.Deserialize<InteractionRecord>(json)!.TotalTokens);
        Assert.Equal(1200, (value with { CacheReadTokens = 999, CacheWriteTokens = 500, ThinkingTokens = 199 }).TotalTokens);
    }
}
