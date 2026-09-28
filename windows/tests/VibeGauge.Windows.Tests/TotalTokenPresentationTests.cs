using VibeGauge.Core;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class TotalTokenPresentationTests
{
    [Fact]
    public void SourceTotalIsSeparateAndUnavailableSourcesStayUnknown()
    {
        var source = new UsageSourceSummary("Codex", UsageDataState.Available, 3, 49_003_000, 42_000_000, 0, 295_000, 94_000, "");
        var row = UsageSourceRow.From(source);
        Assert.True(row.HasUsage);
        Assert.Equal($"总 Token {Formatting.Tokens(49_298_000)} · 3 次", row.TotalText);
        Assert.Contains("上下文", row.Metrics);
        Assert.Contains("思考", row.Metrics);
        var missing = UsageSourceRow.From(source with { State = UsageDataState.NotDetected, Note = "未检测到" });
        Assert.False(missing.HasUsage);
        Assert.Equal("", missing.TotalText);
        Assert.Equal("未检测到", missing.Metrics);
    }

    [Fact]
    public void RecentAndApiRowsLeadWithTotalWithoutDuplicatingThinking()
    {
        var record = new InteractionRecord("id", "Codex", "model", DateTimeOffset.Now, 1000, 800, 100, 200, 150);
        var expected = "总 Token " + Formatting.Tokens(1200) + "\n";
        Assert.StartsWith(expected, RecentRow.From(record).Metrics);
        Assert.StartsWith(expected, ApiProviderRow.From(new("site", 1, 1000, 800, 100, 200, 150)).Metrics);
        Assert.StartsWith(expected, ApiModelRow.From(new("site", "model", 1, 1000, 800, 100, 200, 150, 0, 100)).Metrics);
    }
}
