using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class AntigravityQuotaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-23T02:00:00Z");
    private static string Report(string rows) =>
        """{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"buckets":[""" + rows + "]}]}}}";

    [Theory]
    [InlineData("Google AI Ultra", "Ultra")]
    [InlineData("Google AI Pro", "Pro")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    public void TierAndQuotaComeFromTheSameStatusReport(string reported, string? expected)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { plan_tier = reported,
            quota = new Dictionary<string, object> { ["gemini-5h"] = new { remaining_fraction = 0.75 } } });
        var snapshot = AntigravityQuota.FromStatusLine(payload, Now);
        Assert.Equal(expected, snapshot?.Tier);
        if (expected != null) Assert.Equal(25, snapshot!.GeminiFive!.UsedPercent);
        Assert.Empty(AntigravityQuota.Parse(Report("""{"id":"3p-weekly","remaining_fraction":1}"""), Now).Tier);
    }

    [Fact]
    public void OfficialBucketIdsSeparatePoolsAndConvertRemainingOnlyOnce()
    {
        var q = AntigravityQuota.Parse(Report("""
            {"id":"3p-weekly","remaining_fraction":0.1},
            {"id":"gemini-weekly","remaining_fraction":0.99996,"reset_time":"2026-11-30T01:38:25Z"},
            {"id":"3p-5h","remaining_fraction":0},
            {"id":"gemini-5h","remaining_fraction":0.735}
            """), Now);
        Assert.Equal("", q.Error);
        Assert.Equal(0, q.GeminiWeek!.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), q.GeminiWeek.Window);
        Assert.Equal(DateTimeOffset.Parse("2026-11-30T01:38:25Z"), q.GeminiWeek.ResetsAt);
        Assert.Equal(26, q.GeminiFive!.UsedPercent);
        Assert.Equal(90, q.OtherWeek!.UsedPercent);
        Assert.Equal(100, q.OtherFive!.UsedPercent);
        Assert.Null(q.OtherFive.ResetsAt);
    }

    [Fact]
    public void InvalidAndMissingWindowsAreNotShownAsFull()
    {
        var q = AntigravityQuota.Parse(Report("""
            {"id":"gemini-weekly","remaining_fraction":1.1},
            {"id":"gemini-5h","remaining_fraction":1,"reset_time":"2026-11-22T01:00:00Z"},
            {"id":"3p-weekly","remaining_fraction":1,"disabled":true},
            {"id":"unknown","remaining_fraction":0.9},
            {"id":"3p-5h","remaining_fraction":0.25}
            """), Now);
        Assert.Null(q.GeminiWeek);
        Assert.Null(q.GeminiFive);
        Assert.Null(q.OtherWeek);
        Assert.Equal(75, q.OtherFive!.UsedPercent);
        Assert.NotEmpty(AntigravityQuota.Parse(Report(""), Now).Error);
        Assert.NotEmpty(AntigravityQuota.Parse("""{"status":"ERROR"}""", Now).Error);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Current = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    [Fact]
    public async Task BackgroundRefreshThrottlesAndManualRefreshDropsPreviousAccount()
    {
        int calls = 0;
        var clock = new Clock();
        var first = new TaskCompletionSource<AntigravityQuota>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new AntigravityQuotaRefresh(_ => Interlocked.Increment(ref calls) == 1 ?
            first.Task : Task.FromResult(new AntigravityQuota(Error: "offline")), clock);
        var local = new PlatformStatus("Antigravity", "", false, 0, ProviderDataState.Available, "local tokens");
        for (int i = 0; i < 12; i++) Assert.Null(monitor.Apply(local).FiveHour);
        first.SetResult(new(GeminiFive: new(17, Now.AddHours(1), Now, TimeSpan.FromHours(5))));
        async Task<PlatformStatus> WaitFor(Func<PlatformStatus, bool> predicate)
        {
            for (int i = 0; i < 200; i++)
            {
                var result = monitor.Apply(local);
                if (predicate(result)) return result;
                await Task.Delay(5);
            }
            throw new TimeoutException();
        }
        Assert.Equal("local tokens", (await WaitFor(p => p.FiveHour?.UsedPercent == 17)).Detail);
        clock.Current = Now.AddMinutes(2);
        Assert.NotNull(monitor.Apply(local).FiveHour);
        Assert.Equal(1, calls);
        monitor.Invalidate();
        Assert.Null(monitor.Apply(local).FiveHour);
        var unavailable = await WaitFor(p => p.Metadata!.Rows.Any(r => r.Value == "offline"));
        Assert.Null(unavailable.FiveHour);
        Assert.Equal(2, calls);
        clock.Current = Now.AddMinutes(5);
        await WaitFor(_ => Volatile.Read(ref calls) == 3);
    }
}
