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

    [Fact]
    public void ThemeModeAndBackdropModePersistAndFallBackCleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeGauge-backdrop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new ThemeSettings(root);
            Assert.Equal("dark", settings.ThemeMode);
            Assert.Equal("acrylic", settings.BackdropMode);
            settings.SaveThemeMode("system");
            settings.SaveBackdropMode("mica");
            var reloaded = new ThemeSettings(root);
            Assert.Equal("system", reloaded.ThemeMode);
            Assert.Equal("mica", reloaded.BackdropMode);
            settings.SaveBackdropMode("solid");
            Assert.Equal("solid", new ThemeSettings(root).BackdropMode);
            settings.SaveThemeMode("invalid");
            settings.SaveBackdropMode("invalid");
            Assert.Equal("dark", new ThemeSettings(root).ThemeMode);
            Assert.Equal("acrylic", new ThemeSettings(root).BackdropMode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void HeatmapUsesFiveDistinctLevels()
    {
        Assert.Equal(0, StatisticsPanel.HeatLevel(0, 100));
        Assert.Equal(1, StatisticsPanel.HeatLevel(5, 100));
        Assert.Equal(2, StatisticsPanel.HeatLevel(25, 100));
        Assert.Equal(3, StatisticsPanel.HeatLevel(55, 100));
        Assert.Equal(4, StatisticsPanel.HeatLevel(90, 100));
    }

    [Fact]
    public void DarkAndLightTextRemainLegibleOverWorstCaseAcrylicBackdrops()
    {
        static System.Windows.Media.Color Over(System.Windows.Media.Color fg, System.Windows.Media.Color bg)
        {
            var a = fg.A / 255d;
            return System.Windows.Media.Color.FromRgb(
                (byte)Math.Round(fg.R * a + bg.R * (1 - a)),
                (byte)Math.Round(fg.G * a + bg.G * (1 - a)),
                (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
        }
        static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            static double L(System.Windows.Media.Color c)
            {
                static double Lin(byte ch) { var v = ch / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
                return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
            }
            var l1 = L(a); var l2 = L(b);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }

        // Frosted white-mist dark cards stay airy while text remains bright & legible
        // even when dark Acrylic sits over a bright white window (DWM dark acrylic ~#888C92).
        var dark = new ThemePalette { IsLight = false };
        var darkAcrylicOverWhite = System.Windows.Media.Color.FromRgb(136, 140, 146);
        var darkWindowOverWhite = Over(dark["WindowTintBrush"], darkAcrylicOverWhite);
        var darkCardOverWhite = Over(dark["CardBrush"], darkWindowOverWhite);
        Assert.Equal(255, dark["CardBrush"].R); // White-mist frosted card overlay
        Assert.True(dark["TextMutedBrush"].R >= 190); // Comfortable high-clarity silver, never muddy gray
        Assert.True(Contrast(dark["TextPrimaryBrush"], darkCardOverWhite) >= 6.0);
        Assert.True(Contrast(dark["TextSecondaryBrush"], darkCardOverWhite) >= 5.0);
        Assert.True(Contrast(dark["TextMutedBrush"], darkCardOverWhite) >= 4.2);

        // Light frosted glass over dark wallpaper behind Win11 light acrylic (~#D2D6DE).
        var light = new ThemePalette { IsLight = true };
        var lightAcrylicBase = System.Windows.Media.Color.FromRgb(210, 214, 222);
        var lightWindow = Over(light["WindowTintBrush"], lightAcrylicBase);
        var lightCard = Over(light["CardBrush"], lightWindow);
        Assert.True(Contrast(light["TextPrimaryBrush"], lightCard) >= 11.0);
        Assert.True(Contrast(light["TextSecondaryBrush"], lightCard) >= 8.0);
        Assert.True(Contrast(light["TextMutedBrush"], lightCard) >= 5.8);
    }

    [Fact]
    public void TrayIconRendersCrisplyAcrossAllTaskbarDpiSizes()
    {
        foreach (var size in new[] { 16, 20, 24, 32 })
        foreach (var pct in new[] { 8, 51, 100 })
        {
            using var bmp = TrayIconManager.IconFactory.RenderBitmap(pct, size);
            Assert.Equal(size, bmp.Width);
            Assert.Equal(size, bmp.Height);
            // Verify high-contrast white digit pixels exist in the center band.
            var whitePixels = 0;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.A > 200 && p.R > 220 && p.G > 220 && p.B > 220) whitePixels++;
                }
            Assert.True(whitePixels >= size, $"Expected crisp white digit pixels at {size}px for {pct}%");
        }
        using var icon = TrayIconManager.IconFactory.Create(51);
        Assert.NotNull(icon);
    }
}
