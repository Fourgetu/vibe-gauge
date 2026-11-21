using VibeGauge.Core;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class DevinWeeklyCardTests
{
    [Fact]
    public void CompactCardShowsQuotaAlongsideLocalTokenTotals()
    {
        var usage = new UsageSourceSummary("Devin", UsageDataState.Available, 2, 800, 0, 0, 200, 0, "");
        var status = new PlatformStatus("Devin", "", true, 1, ProviderDataState.Available, "",
            Weekly: new(5, DateTimeOffset.UtcNow.AddHours(19), null, TimeSpan.FromDays(7)), DesktopTokens: new(usage, usage, true));
        var row = PlatformRow.From(status);
        Assert.True(row.ShowQuotaMeters && row.ShowDetail && row.IsCompact);
        Assert.False(row.IsWide);
        Assert.Contains("周额度 5%", row.FullTooltip);
        Assert.Contains("重置", row.FullTooltip);
        Assert.Contains("累计", row.DisplayDetail);
        Assert.Single(ProviderDetails.Windows(status));
    }
}
