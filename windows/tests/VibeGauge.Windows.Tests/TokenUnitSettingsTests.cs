using System.IO;
using System.Text.Json;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class TokenUnitSettingsTests
{
    [Fact]
    public void UnitPersistsIndependentlyOfAppearanceAndCorruptSettingsFallBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-token-unit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new TokenUnitSettings(root);
            Assert.Equal(TokenUnit.Chinese, settings.Load());
            new ThemeSettings(root).Save(true);
            Assert.True(settings.Save(TokenUnit.International));
            Assert.Equal(TokenUnit.International, new TokenUnitSettings(root).Load());
            Assert.True(new ThemeSettings(root).IsLight);
            new ThemeSettings(root).Save(false);
            Assert.Equal(TokenUnit.International, settings.Load());
            foreach (var content in new[] { "broken", "null", "{}", "{\"Unit\":\"unknown\"}" })
            {
                File.WriteAllText(Path.Combine(root, "token-display.json"), content);
                Assert.Equal(TokenUnit.Chinese, settings.Load());
            }
            Assert.True(settings.Save(TokenUnit.Chinese));
            Assert.Equal(TokenUnit.Chinese, new TokenUnitSettings(root).Load());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ProviderCardsReformatNumericSnapshotsWithoutLosingExactTooltips()
    {
        var summary = new UsageSourceSummary("PI-Desktop", UsageDataState.Available, 2, 1200000, 1000000, 0, 34000, 2000, "");
        var data = new DesktopTokenDisplay(summary, summary, true);
        var display = data.Format();
        var status = new PlatformStatus("PI-Desktop", "", true, 1, ProviderDataState.Available, display.Detail,
            CompactDetail: display.Compact, DesktopTokens: data);
        var row = PlatformRow.From(status, unit: TokenUnit.International);
        Assert.Contains("1.23 M", row.DisplayDetail);
        Assert.Contains("34 K", row.FullTooltip);
        Assert.Contains(summary.TotalTokens.ToString("N0"), row.FullTooltip);
        Assert.Contains("万", PlatformRow.From(status).Detail);
        using var json = JsonDocument.Parse("""{"isValid":true,"balance":10,"usage":{"today":{"total_tokens":268800},"total":{"total_tokens":1202949847}}}""");
        var custom = Sub2ApiParser.Parse("Site", json.RootElement, DateTimeOffset.Now);
        var compact = PlatformRow.From(custom, unit: TokenUnit.International);
        Assert.Contains("268.8 K", compact.DisplayDetail);
        Assert.Contains("1.2 B", compact.DisplayDetail);
        Assert.Contains(custom.Detail, compact.FullTooltip);
    }

    [Fact]
    public void AllUsageAndApiRowsRespectTheRequestedUnit()
    {
        var record = new InteractionRecord("id", "Codex", "model", DateTimeOffset.Now, 1000000, 800000, 100000, 200000, 150000);
        var source = new UsageSourceSummary("Codex", UsageDataState.Available, 1, 1000000, 800000, 100000, 200000, 150000, "");
        Assert.StartsWith("总 Token 1.2 M", UsageSourceRow.From(source, TokenUnit.International).TotalText);
        Assert.Contains("150 K", RecentRow.From(record, TokenUnit.International).Metrics);
        Assert.Contains("800 K", ApiProviderRow.From(new("Site", 1, 1000000, 800000, 100000, 200000, 150000), TokenUnit.International).CacheText);
        Assert.StartsWith("总 Token 1.2 M", ApiModelRow.From(new("Site", "model", 1, 1000000, 800000, 100000, 200000, 150000, 0, 100), TokenUnit.International).Metrics);
        Assert.Equal("1.2 M", ApiRecentRow.From(new("Site", "model", DateTimeOffset.Now, 1200000, 200, 100), TokenUnit.International).Tokens);
    }
}
