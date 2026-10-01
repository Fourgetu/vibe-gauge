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
    public void TransparencySurvivesThemeChangesAndLoadsLegacySettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeGauge-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "appearance.json"), "{\"Theme\":\"light\"}");
            var settings = new ThemeSettings(root);
            Assert.True(settings.IsLight);
            Assert.Equal(75, settings.GetTransparency(true));
            Assert.Equal(40, settings.GetTransparency(false));
            settings.SaveTransparency(true, 85);
            settings.SaveTransparency(false, 25);
            settings.Save(false);
            var reloaded = new ThemeSettings(root);
            Assert.False(reloaded.IsLight);
            Assert.Equal(85, reloaded.GetTransparency(true));
            Assert.Equal(25, reloaded.GetTransparency(false));
            settings.SaveTransparency(true, 200);
            settings.SaveTransparency(false, -5);
            Assert.Equal(100, settings.GetTransparency(true));
            Assert.Equal(0, settings.GetTransparency(false));
            File.WriteAllText(Path.Combine(root, "appearance.json"), "broken json");
            Assert.Equal(75, settings.GetTransparency(true));
            Assert.Equal(40, settings.GetTransparency(false));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TransparencyChangesOnlyBackdropAndIsIndependentForEachTheme()
    {
        var palette = new ThemePalette { IsLight = true };
        var text = palette["TextPrimaryBrush"];
        var card = palette["CardBrush"];
        palette.SetTransparency(true, 100);
        Assert.Equal(0, palette["WindowTintBrush"].A);
        Assert.Equal(text, palette["TextPrimaryBrush"]);
        Assert.Equal(card, palette["CardBrush"]);
        palette.IsLight = false;
        Assert.NotEqual(0, palette["WindowTintBrush"].A);
        palette.SetTransparency(false, 0);
        Assert.Equal(255, palette["WindowTintBrush"].A);
        palette.IsLight = true;
        Assert.Equal(0, palette["WindowTintBrush"].A);
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
