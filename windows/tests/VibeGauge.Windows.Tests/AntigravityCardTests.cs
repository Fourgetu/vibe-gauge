using VibeGauge.Core;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class AntigravityCardTests
{
    [Fact]
    public void CardShowsRemainingForBothPoolsAlongsideDesktopTokens()
    {
        var now = DateTimeOffset.Now;
        var local = new UsageSourceSummary("Antigravity", UsageDataState.Available, 1, 50, 0, 0, 20, 0, "");
        QuotaWindow Window(int used, int hours) => new(used, now.AddHours(hours), now, TimeSpan.FromHours(hours));
        var status = new PlatformStatus("Antigravity", "Ultra", false, 0, ProviderDataState.Available, "",
            Window(27, 5), Window(5, 168), "Claude/GPT", Window(100, 5), Window(0, 168),
            DesktopTokens: new(local, local, true), Metadata: new([new("额度来源", "agy 当前登录账号")], []));
        var row = PlatformRow.From(status);
        Assert.True(row.ShowTier);
        Assert.Equal("Ultra", row.Tier);
        Assert.True(row.ShowQuotaMeters && row.ShowDetail && row.IsWide);
        Assert.Contains("累计", row.DisplayDetail);
        Assert.Contains("agy 当前登录账号", row.FullTooltip);
        Assert.Equal(new[] { "Gemini 5H 余 73%", "Gemini 周 余 95%", "Claude/GPT 5H 余 0%", "Claude/GPT 周 余 100%" },
            row.Quotas.Select(q => q.Label + " " + q.PercentText));
        Assert.Equal("Danger", row.Quotas[2].Tone);
        Assert.Equal(4, ProviderDetails.Windows(status).Count());
        Assert.NotNull(status.DesktopTokens);
    }
}
