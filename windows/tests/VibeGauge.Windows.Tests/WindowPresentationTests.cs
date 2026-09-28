using System.IO;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class WindowPresentationTests
{
    [Fact]
    public void PiUsageCardKeepsItsDetailAndDoesNotLabelProcessesAsSessions()
    {
        var status = new PlatformStatus("PI-Desktop", "", true, 8, ProviderDataState.NoQuota, "本地无今日 token 统计");
        var row = PlatformRow.From(status);
        Assert.Equal(status.Detail, row.Detail);
        Assert.Equal("运行中", row.SessionText);
        Assert.False(row.ShowTier);
        Assert.Empty(row.Quotas);
        var stopped = PlatformRow.From(status with { IsRunning = false });
        Assert.Equal("未运行", stopped.SessionText);
        Assert.Equal("Muted", stopped.Tone);
    }

    [Fact]
    public void PlacementRoundTripsWithoutChangingOtherSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeGauge-placement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WindowPlacementStore(root);
            Assert.Null(store.Load());
            var value = new WindowPlacement(-1200, 80, 540, 860, true);
            store.Save(value);
            Assert.Equal(value, store.Load());
            File.WriteAllText(Path.Combine(root, "window-placement.json"), "not json");
            Assert.Null(store.Load());
            store.Save(value with { Width = 12 });
            Assert.Null(store.Load());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DualPoolAndMonthlyProvidersSpanBothColumns()
    {
        var quota = new QuotaWindow(22, DateTimeOffset.Now.AddHours(3), DateTimeOffset.Now, TimeSpan.FromHours(5));
        var status = new PlatformStatus("Codex", "Pro", true, 1, ProviderDataState.Available, "", quota);
        Assert.False(PlatformRow.From(status).IsWide);
        Assert.True(PlatformRow.From(status with { Name = "Gemini", SecondaryFiveHour = quota, SecondaryPoolName = "3P" }).IsWide);
        Assert.True(PlatformRow.From(status with { Monthly = quota }).IsWide);
        var pools = PlatformRow.From(status with { SecondaryFiveHour = quota, SecondaryPoolName = "3P" });
        Assert.Single(pools.PrimaryQuotas);
        Assert.Single(pools.SecondaryQuotas);
    }

    [Fact]
    public void UnknownAccountsDoNotDisplayAnActivePlanBadge()
    {
        var status = new PlatformStatus("Codex", "未登录", false, 0, ProviderDataState.NotSignedIn, "未登录");
        Assert.False(PlatformRow.From(status).ShowTier);
        Assert.True(PlatformRow.From(status with { Tier = "Pro" }).ShowTier);
        Assert.True(PlatformRow.From(status with { Name = "Ollama", Tier = "本地" }).IsLocal);
    }

    [Fact]
    public void StaleQuotaKeepsTrustMessageWithoutForecast()
    {
        var quota = new QuotaWindow(55, DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(-2), TimeSpan.FromHours(5));
        var row = PlatformRow.From(new PlatformStatus("Claude", "Max", true, 1, ProviderDataState.Stale, "", quota));
        Assert.True(row.HasTrustNote);
        Assert.False(row.HasForecast);
        Assert.Equal("—", row.PrimaryQuotas.Single().PercentText);
    }
}
