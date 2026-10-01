using System.IO;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class ThemeSettingsTests
{
    [Fact]
    public void ThemeChoicePersistsAndCorruptPreferencesFallBackToDark()
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeGauge-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new ThemeSettings(root);
            Assert.False(settings.IsLight);
            settings.Save(true);
            Assert.True(new ThemeSettings(root).IsLight);
            settings.Save(false);
            Assert.False(new ThemeSettings(root).IsLight);
            File.WriteAllText(Path.Combine(root, "appearance.json"), "broken json");
            Assert.False(settings.IsLight);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PaletteSwitchNotifiesBoundBrushesAndUsesOppositeTextPolarity()
    {
        var palette = new ThemePalette();
        var notifications = new List<string?>();
        palette.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        Assert.True(palette["TextPrimaryBrush"].R > palette["WindowBrush"].R);
        palette.IsLight = true;
        Assert.Contains("Item[]", notifications);
        Assert.True(palette["TextPrimaryBrush"].R < palette["WindowBrush"].R);
        Assert.Equal(255, palette["TooltipBackgroundBrush"].A);
        Assert.True(palette["TooltipTextBrush"].R < palette["TooltipBackgroundBrush"].R);
    }

    [Fact]
    public void LightPaletteKeepsAcrylicBackdropVisibleThroughNestedSurfaces()
    {
        var palette = new ThemePalette { IsLight = true };
        Assert.InRange(palette["WindowTintBrush"].A, 1, 0x70);
        Assert.InRange(palette["PanelBrush"].A, 1, 0x30);
        Assert.InRange(palette["CardBrush"].A, 1, 0x60);
        Assert.InRange(palette["CardRaisedBrush"].A, 1, 0x90);
        Assert.True(palette["TextSecondaryBrush"].R < palette["WindowBrush"].R);
        Assert.True(palette["TextMutedBrush"].R < palette["WindowBrush"].R);
    }
}
