using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ApiAccountingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
    [Fact]
    public void PricesUseLongestPrefixSeparateCachesAndMarkUnknownModels()
    {
        var prices = PriceTable.Parse("""{"_currency":"TEST","_asof":"2026-09-29","model":{"in":4,"out":10,"cache_read":1,"cache_write":5},"model-pro":{"in":6,"out":20},"placeholder":{"in":0,"out":0}}""");
        Assert.Equal(4.3m, prices.Cost("model-v2", 1_000_000, 100_000, 300_000, 200_000));
        Assert.Equal(8m, prices.Cost("model-pro-v2", 1_000_000, 100_000, 300_000, 200_000));
        Assert.Null(prices.Cost("placeholder", 10, 10, 0, 0));
        var summary = prices.Estimate([new("test", "unknown", 1, 100, 20)], 2);
        Assert.Equal(1, summary.UnpricedModels);
        Assert.Equal(2, summary.UnknownCalls);
        Assert.Contains("非实际账单", summary.Description);
    }
    [Fact]
    public void QualityUsesNearestRankAndDropsSensitiveHeaders()
    {
        var safe = ApiQuality.SafeHeaders(new Dictionary<string, string> { ["authorization"] = "secret", ["set-cookie"] = "secret", ["x-ratelimit-remaining-tokens"] = "12", ["retry-after"] = "30" });
        Assert.Equal(2, safe.Count);
        var q = ApiQuality.Build(Enumerable.Range(1, 20).Select(i => new InteractionRecord(i.ToString(), "API", "m", Now, 1, 0, 0, 1, 0,
            i == 1 ? 429 : 200, i * 10, true, true, safe)));
        Assert.Equal(100, q.P50); Assert.Equal(190, q.P95); Assert.Equal(200, q.Maximum); Assert.Equal(1, q.RateLimited);
        Assert.Equal(2, q.Limits.Count);
    }
    [Fact]
    public void PlansUseWeightsAndDoNotDoubleCountIndependentPoolsOrLocalFailures()
    {
        using var doc = JsonDocument.Parse("""{"requests":{"5h":10},"weights":{"*":2},"models":{"special":{"requests":{"5h":5}}}}""");
        var plan = CodingPlan.Parse(doc.RootElement, "test");
        var rows = new[] { new InteractionRecord("1", "API", "normal", Now.AddHours(-1), 1, 0, 0, 1, 0, 200, ReachedUpstream: true),
            new InteractionRecord("2", "API", "special-pro", Now.AddHours(-1), 1, 0, 0, 1, 0, 429, ReachedUpstream: true),
            new InteractionRecord("3", "API", "normal", Now.AddHours(-1), 0, 0, 0, 0, 0, 502, ReachedUpstream: false) };
        var windows = plan.Estimate(rows, Now);
        Assert.Equal(2, windows.Count); Assert.All(windows, x => Assert.Equal(20, x.Window.UsedPercent));
        Assert.All(windows, x => Assert.True(x.Window.IsEstimate));
    }
    [Fact]
    public void FixedWindowsHandleIdleFirstUseMondayAndShortSubscriptionMonths()
    {
        var span = TimeSpan.FromHours(5);
        var calls = new[] { (Now.AddHours(-7), 1d), (Now.AddHours(-1), 1d) };
        var first = CodingPlan.Window(calls, "first_use", span, 10, Now, TimeZoneInfo.Utc);
        Assert.Equal(10, first.UsedPercent); Assert.Equal(Now.AddHours(4), first.ResetsAt);
        Assert.Null(CodingPlan.Window([(Now.AddHours(-7), 1d)], "first_use", span, 10, Now, TimeZoneInfo.Utc).ResetsAt);
        var monday = CodingPlan.Window(calls, "monday", TimeSpan.FromDays(7), 10, Now, TimeZoneInfo.Utc);
        Assert.Equal(DayOfWeek.Monday, monday.ResetsAt!.Value.DayOfWeek);
        var feb = new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero);
        var month = CodingPlan.Window([], "subscription_day", TimeSpan.FromDays(30), 10, feb, TimeZoneInfo.Utc, 31);
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero), month.ResetsAt);
    }
    [Fact]
    public void WeeklyResetUsesCalendarAcrossDaylightSaving()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        var w = CodingPlan.Window([], "monday", TimeSpan.FromDays(7), 10, now, zone);
        Assert.Equal(167, w.Window.TotalHours);
    }
}
