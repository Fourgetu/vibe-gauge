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

    [Fact]
    public void ModelCompositionMergesToolsAndRanksByInputPlusOutput()
    {
        var stats = UsageStatistics.Build([
            Record(Now, "same", 100, 10),
            Record(Now, "same", 200, 20) with { Source = "Claude" },
            Record(Now, "output-heavy", 50, 450),
            Record(Now.AddDays(-1), "old", 900, 1)
        ], Now, TimeZoneInfo.Utc);
        var period = stats.ForPeriod(Today, Today);
        var mix = period.ModelComposition;
        Assert.Equal(3, period.Models.Count); // Pricing retains individual source records.
        Assert.Equal(2, mix.Count);
        Assert.Equal("output-heavy", mix[0].Model);
        Assert.Equal(500, mix[0].TotalTokens);
        Assert.Equal("same", mix[1].Model);
        Assert.Equal("Claude / Codex", mix[1].Source);
        Assert.Equal(330, mix[1].TotalTokens);
        Assert.Equal(60, mix[1].CacheRead);
        Assert.Equal(10, mix[1].CacheWrite);
        Assert.Equal(8, mix[1].Thinking);
        Assert.Equal(period.TotalTokens, mix.Sum(x => x.TotalTokens));
        Assert.Equal(100, mix.Sum(x => x.TotalTokens * 100d / period.TotalTokens), 8);
    }

    [Fact]
    public void DistributionPartitionsTokensWithoutCountingReasoningTwice()
    {
        var stats = UsageStatistics.Build([
            Record(Now, "m", 1000, 200) with { CacheReadTokens = 600, CacheWriteTokens = 100, ThinkingTokens = 150 },
            Record(Now.AddDays(-1), "zero", 0, 0) with { CacheReadTokens = 0, CacheWriteTokens = 0, ThinkingTokens = 0 }
        ], Now, TimeZoneInfo.Utc);
        var period = stats.ForPeriod(Today.AddDays(-1), Today);
        Assert.Equal(300, period.NewInput);
        Assert.Equal(700, period.CacheTotal);
        Assert.Equal(200, period.Output);
        Assert.Equal(1200, period.NewInput + period.CacheTotal + period.Output);
        Assert.Equal(60, period.CacheHitRate);
        Assert.Equal(1, period.ActiveDays); // A zero-token call is not an active token day.
        var empty = stats.ForPeriod(Today.AddDays(-2), Today.AddDays(-2));
        Assert.Equal(0, empty.NewInput + empty.CacheTotal + empty.Output);
        Assert.Empty(empty.ModelComposition);
        Assert.Null(empty.CacheHitRate);
    }

    [Fact]
    public void AgentUsageKeepsSameModelToolsSeparateAndUsesSelectedDates()
    {
        var stats = UsageStatistics.Build([
            Record(Now, "same", 100, 10),
            Record(Now, "same", 200, 20) with { Source = "Claude" },
            Record(Now, "another", 300, 30) with { Source = "Claude" },
            Record(Now.AddDays(-1), "same", 900, 90)
        ], Now, TimeZoneInfo.Utc);
        var period = stats.ForPeriod(Today, Today);
        var agents = period.Sources;
        Assert.Equal(2, agents.Count);
        Assert.Equal("Claude", agents[0].Name);
        Assert.Equal(550, agents[0].TotalTokens);
        Assert.Equal(2, agents[0].Turns);
        Assert.Equal(60, agents[0].CacheReadTokens);
        Assert.Equal(110, agents[1].TotalTokens);
        Assert.Equal(period.TotalTokens, agents.Sum(x => x.TotalTokens));
        Assert.Equal(period.Calls, agents.Sum(x => x.Turns));
        Assert.Equal(period.CacheWrite, agents.Sum(x => x.CacheWriteTokens));
        Assert.Equal(period.Thinking, agents.Sum(x => x.ThinkingTokens));
        Assert.Equal(990, Assert.Single(stats.ForPeriod(Today.AddDays(-1), Today.AddDays(-1)).Sources).TotalTokens);
        Assert.Empty(stats.ForPeriod(Today.AddDays(-2), Today.AddDays(-2)).Sources);
    }
}
