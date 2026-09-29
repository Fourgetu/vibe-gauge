using System.Globalization;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class TokenFormattingTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1 K")]
    [InlineData(1234, "1.234 K")]
    [InlineData(268800, "268.8 K")]
    [InlineData(999999, "999.999 K")]
    [InlineData(1000000, "1 M")]
    [InlineData(1234567, "1.23 M")]
    [InlineData(26880000, "26.88 M")]
    [InlineData(1000000000, "1 B")]
    [InlineData(1202949847, "1.2 B")]
    public void InternationalUnitsUseDecimalTokenCounts(long value, string expected)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, Formatting.Tokens(value, TokenUnit.International));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void ChineseModeRemainsTheDefaultWithoutSharedMutableState()
    {
        Assert.EndsWith(" 万", Formatting.Tokens(268800));
        Assert.EndsWith(" K", Formatting.Tokens(268800, TokenUnit.International));
        Assert.Equal(Formatting.Tokens(268800), Formatting.Tokens(268800, TokenUnit.Chinese));
        Assert.EndsWith(" 亿", Formatting.Tokens(1202949847));
        Assert.EndsWith(" B", Formatting.Tokens(long.MaxValue, TokenUnit.International));
    }

    [Fact]
    public void DesktopDisplayRetainsExactOriginalCountsAndErrorNotesAcrossUnits()
    {
        var today = new UsageSourceSummary("ZCode", UsageDataState.Available, 3, 264000, 200000, 1000, 4800, 1200, "");
        var total = today with { Turns = 9, ContextTokens = 1200000000, OutputTokens = 3000000 };
        var data = new DesktopTokenDisplay(today, total, true);
        var cn = data.Format();
        var english = data.Format(TokenUnit.International);
        Assert.Contains("268.8 K", english.Detail);
        Assert.Contains("1.2 B", english.Compact);
        Assert.EndsWith(cn.Detail.Split('\n')[^1], english.Detail);
        Assert.Equal(268800, today.TotalTokens);
        Assert.Equal(1203000000, total.TotalTokens);
        Assert.Equal(cn, data.Format());
        var failed = (data with { Total = total with { State = UsageDataState.ReadFailed, Note = "读取失败" } }).Format(TokenUnit.International);
        Assert.StartsWith("读取失败", failed.Detail);
        Assert.Contains("1.2 B", failed.Compact);
    }
}
