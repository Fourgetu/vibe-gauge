using System.Windows.Media;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class QuotaVisualsTests
{
    [Theory]
    [InlineData(0, 41, 209, 38)]
    [InlineData(50, 126, 209, 38)]
    [InlineData(70, 207, 209, 38)]
    [InlineData(85, 209, 132, 38)]
    [InlineData(95, 209, 71, 38)]
    [InlineData(100, 209, 38, 38)]
    [InlineData(-20, 41, 209, 38)]
    [InlineData(120, 209, 38, 38)]
    public void UsesUpstreamHueCurve(int percent, byte red, byte green, byte blue)
    {
        Assert.Equal(Color.FromRgb(red, green, blue), QuotaVisuals.ColorForPercent(percent));
        Assert.True(QuotaVisuals.BrushForPercent(percent).IsFrozen);
    }
}
