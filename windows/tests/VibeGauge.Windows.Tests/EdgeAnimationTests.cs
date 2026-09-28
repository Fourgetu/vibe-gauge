using VibeGauge.Windows.Services;
using Xunit;
using Rect = System.Drawing.Rectangle;

namespace VibeGauge.Windows.Tests;

public sealed class EdgeAnimationTests
{
    [Fact]
    public void DurationDependsOnElapsedTimeNotFrameCount()
    {
        Assert.Equal(0, TopEdgeAutoHide.Interpolate(0, -800, TimeSpan.Zero));
        Assert.Equal(-400, TopEdgeAutoHide.Interpolate(0, -800, TimeSpan.FromMilliseconds(120)));
        Assert.Equal(-800, TopEdgeAutoHide.Interpolate(0, -800, TimeSpan.FromMilliseconds(240)));
        Assert.Equal(-800, TopEdgeAutoHide.Interpolate(0, -800, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, TopEdgeAutoHide.Interpolate(0, -800, TimeSpan.FromMilliseconds(-1)));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void SmoothMotionIsMonotonicAtDifferentFrameRates(int frameRate)
    {
        double previous = 0;
        for (var ms = 0d; ms <= 240; ms += 1000d / frameRate)
        {
            var top = TopEdgeAutoHide.Interpolate(0, -800, TimeSpan.FromMilliseconds(ms));
            Assert.InRange(top, -800, previous);
            Assert.InRange(previous - top, 0, 90);
            previous = top;
        }
        Assert.Equal(-800, TopEdgeAutoHide.Interpolate(0, -800, TopEdgeAutoHide.AnimationDuration));
    }

    [Fact]
    public void ReversalStartsAtTheCurrentPositionWithoutJumping()
    {
        var current = TopEdgeAutoHide.Interpolate(0, -800, TimeSpan.FromMilliseconds(80));
        Assert.Equal(current, TopEdgeAutoHide.Interpolate(current, 0, TimeSpan.Zero));
        Assert.Equal(0, TopEdgeAutoHide.Interpolate(current, 0, TopEdgeAutoHide.AnimationDuration));
    }

    [Fact]
    public void PerFrameClippingIsOnlyRequiredForOverlappingUpperDisplays()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var above = new Rect(0, -1080, 1920, 1080);
        var beside = new Rect(-1920, 0, 1920, 1080);
        Assert.False(TopEdgeAutoHide.NeedsAnimationClip(monitor, 100, 520, 800, [monitor, beside]));
        Assert.True(TopEdgeAutoHide.NeedsAnimationClip(monitor, 100, 520, 800, [monitor, above]));
        Assert.False(TopEdgeAutoHide.NeedsAnimationClip(monitor, 100, 520, 800, [monitor, new Rect(1920, -1080, 1920, 1080)]));
        Assert.True(TopEdgeAutoHide.NeedsAnimationClip(above, 100, 520, 800, [above, new Rect(0, -2160, 1920, 1080)]));
    }
}
