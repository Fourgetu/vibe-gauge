using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class StatisticsSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 28);

    [Fact]
    public void HistoricalDateReturnsItsModelsAndTotalsInsteadOfToday()
    {
        var stats = UsageStatistics.Build([
            Record(Now, "today-model", 100, 20),
            Record(Now.AddDays(-40), "historical-model", 250, 30)
        ], Now, TimeZoneInfo.Utc);
        var old = Today.AddDays(-40);
        var period = stats.ForPeriod(old, old);
        Assert.Equal("historical-model", Assert.Single(period.Models).Model);
        Assert.Equal(1, period.Calls);
        Assert.Equal(250, period.Context);
        Assert.Equal(30, period.Output);
        Assert.Contains(stats.Hours, x => x.Date == old && x.Hour == 12 && x.Calls == 1);
        Assert.Equal("today-model", Assert.Single(stats.Models).Model);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 2)]
    [InlineData(29, 4)]
    [InlineData(100, 5)]
    public void RangesUseInclusiveDatesAndTheSameModelsAsTheirTotals(int daysBack, int count)
    {
        var stats = UsageStatistics.Build(new[] { 0, 6, 7, 29, 30 }
            .Select(d => Record(Now.AddDays(-d), "model-" + d, 100, 10)), Now, TimeZoneInfo.Utc);
        var period = stats.ForPeriod(Today.AddDays(-daysBack), Today);
        Assert.Equal(count, period.Calls);
        Assert.Equal(count, period.ActiveDays);
        Assert.Equal(count, period.Models.Count);
        Assert.Equal(period.Calls, period.Models.Sum(x => x.Calls));
        Assert.Equal(period.Context, period.Models.Sum(x => x.Context));
        Assert.Equal(period.Output, period.Models.Sum(x => x.Output));
        Assert.Equal(period.CacheRead, period.Models.Sum(x => x.CacheRead));
        Assert.Equal(period.CacheWrite, period.Models.Sum(x => x.CacheWrite));
        Assert.Equal(period.Thinking, period.Models.Sum(x => x.Thinking));
    }

    [Fact]
    public void ModelAggregationKeepsToolsDistinctAndOrdersByContextShare()
    {
        var stats = UsageStatistics.Build([
            Record(Now, "same", 100, 10),
            Record(Now.AddDays(-1), "same", 200, 20),
            Record(Now, "same", 500, 30) with { Source = "PI-Desktop" }
        ], Now, TimeZoneInfo.Utc);
        var period = stats.ForPeriod(Today.AddDays(-1), Today);
        Assert.Equal(2, period.Models.Count);
        Assert.Equal("PI-Desktop", period.Models[0].Source);
        Assert.Equal(300, period.Models[1].Context);
        Assert.Equal(2, period.Models[1].Calls);
        Assert.Equal(100, period.Models.Sum(x => x.Context * 100d / period.Context), 8);
    }

    [Fact]
    public void EmptyDateHasZeroTotalsAndNoFallbackModels()
    {
        var stats = UsageStatistics.Build([Record(Now, "today", 100, 20)], Now, TimeZoneInfo.Utc);
        var result = stats.ForPeriod(Today.AddDays(-1), Today.AddDays(-1));
        Assert.Equal(0, result.Calls);
        Assert.Equal(0, result.Context);
        Assert.Equal(0, result.ActiveDays);
        Assert.Null(result.CacheHitRate);
        Assert.Empty(result.Models);
    }

    [Fact]
    public void AllTimeStillExcludesFutureRecordsAndUsesLocalMidnightBoundaries()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("stats+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var stats = UsageStatistics.Build([
            Record(new(2026, 9, 27, 15, 59, 0, TimeSpan.Zero), "before-midnight", 100, 10),
            Record(new(2026, 9, 27, 16, 0, 0, TimeSpan.Zero), "after-midnight", 200, 20),
            Record(Now.AddSeconds(1), "future", 300, 30)
        ], Now, zone);
        Assert.Equal("after-midnight", Assert.Single(stats.ForPeriod(Today, Today).Models).Model);
        Assert.Equal("before-midnight", Assert.Single(stats.ForPeriod(Today.AddDays(-1), Today.AddDays(-1)).Models).Model);
        Assert.Equal(2, stats.ForPeriod(DateOnly.MinValue, DateOnly.MaxValue).Calls);
        Assert.Contains(stats.Hours, x => x.Date == Today && x.Hour == 0);
    }

    [Fact]
    public void OutputOnlyUsageDoesNotInventCacheRatioAndInvalidIntervalsAreRejected()
    {
        var stats = UsageStatistics.Build([Record(Now, "output-only", 0, 20) with { CacheReadTokens = 0, CacheWriteTokens = 0 }], Now, TimeZoneInfo.Utc);
        var period = stats.ForPeriod(Today, Today);
        Assert.Equal(20, period.Output);
        Assert.Null(period.CacheHitRate);
        Assert.Throws<ArgumentException>(() => stats.ForPeriod(Today, Today.AddDays(-1)));
    }

    private static InteractionRecord Record(DateTimeOffset at, string model, long context, long output) =>
        new(Guid.NewGuid().ToString(), "Codex", model, at, context, 30, 5, output, 4);
}
