using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class ProviderCardChecks
{
    internal static void Verify()
    {
        var palette = (ThemePalette)Application.Current.FindResource("ThemePalette");
        var quota = new QuotaWindow(42, DateTimeOffset.Now.AddDays(2), DateTimeOffset.Now, TimeSpan.FromDays(7));
        var detail = "今日 120 次 · 总 Token 120万\n今日上下文 100万 · 输出 20万\n本地累计 900 次 · 总 Token 900万\n到期 2030-01-01";
        var cards = new[]
        {
            new PlatformStatus("Claude", "Max", true, 2, ProviderDataState.Available, "本地额度数据", quota, quota),
            new PlatformStatus("Codex", "Pro", true, 1, ProviderDataState.Available, "本地额度数据", quota, quota),
            new PlatformStatus("Gemini", "已登录", true, 1, ProviderDataState.Available, "本地额度数据", quota, quota, "3P", quota, quota),
            new PlatformStatus("ZCode", "", true, 1, ProviderDataState.Available, detail, CompactDetail: "今日 120万 · 120 次\n累计 900万 Token"),
            new PlatformStatus("PI-Desktop", "", true, 1, ProviderDataState.Available, detail, CompactDetail: "今日 120万 · 120 次\n累计 900万 Token"),
            new PlatformStatus("Ollama", "本地", false, 0, ProviderDataState.NotRunning, "未运行", ModelCount: 0),
            new PlatformStatus("WorkBuddy", "", true, 1, ProviderDataState.Available, detail, CompactDetail: "今日 120万 · 120 次\n累计 900万 Token"),
            new PlatformStatus("DSH Desktop", "", true, 1, ProviderDataState.Available, detail, CompactDetail: "今日 120万 · 120 次\n累计 900万 Token"),
            new PlatformStatus("自定义站点 · 12345678", "sub2api", false, 0, ProviderDataState.Available,
                detail, Weekly: quota, AlwaysShowDetail: true, CompactDetail: "钱包余额 1234567.89 USD\n今日 120万 Token · 累计 900万 Token")
        }.Select(x => PlatformRow.From(x)).ToArray();
        foreach (var light in new[] { false, true })
        foreach (var width in new[] { 390, 490 })
        foreach (var codexTier in new[] { "Pro", "API Key" })
        {
            cards[1] = PlatformRow.From(codexTier == "API Key"
                ? new PlatformStatus("Codex", codexTier, true, 1, ProviderDataState.NoQuota, "API Key 模式无订阅额度")
                : new PlatformStatus("Codex", codexTier, true, 1, ProviderDataState.Available, "本地额度数据", Weekly: quota));
            palette.IsLight = light;
            var panel = new PlansPanel { DataContext = new { Platforms = cards } };
            var window = new Window { Content = panel, Width = width, Height = 800, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var borders = Descendants<Border>(panel).Where(x => x.DataContext is PlatformRow && x.ToolTip is string)
                    .ToDictionary(x => ((PlatformRow)x.DataContext).Name);
                Assert.Equal(cards.Length, borders.Count);
                var codexTitle = Descendants<TextBlock>(borders["Codex"]).Single(x => x.Text == "Codex");
                var codexBadge = Descendants<TextBlock>(borders["Codex"]).Single(x => x.Text == codexTier);
                AssertFitsText(codexTitle);
                AssertFitsText(codexBadge);
                var titleRight = codexTitle.TranslatePoint(new Point(codexTitle.ActualWidth, 0), panel).X;
                var badgeLeft = codexBadge.TranslatePoint(new Point(), panel).X;
                Assert.True(titleRight <= badgeLeft, "Codex name overlaps its authentication badge");
                var gemini = borders["Gemini"].TranslatePoint(new Point(), panel);
                var zcode = borders["ZCode"].TranslatePoint(new Point(), panel);
                Assert.Equal(gemini.Y, zcode.Y);
                Assert.True(zcode.X > gemini.X);
                Assert.InRange(borders["PI-Desktop"].ActualHeight, 40, 85);
                Assert.InRange(borders["ZCode"].ActualHeight, 40, 95);
                Assert.InRange(borders[cards[^1].Name].ActualHeight, 40, 115);
                Assert.Equal(borders["PI-Desktop"].ActualHeight, borders["Ollama"].ActualHeight);
                foreach (var border in borders.Values)
                {
                    var row = (PlatformRow)border.DataContext;
                    if (!row.IsWide)
                        AssertFitsText(Descendants<TextBlock>(border).Single(x => x.Text == row.Name));
                    Assert.Equal(row.FullTooltip, border.ToolTip);
                    Assert.Contains(row.Detail, (string)border.ToolTip);
                    Assert.Equal(60000, ToolTipService.GetShowDuration(border));
                }
                var directory = Path.Combine(AppContext.BaseDirectory, "provider-captures");
                Directory.CreateDirectory(directory);
                Capture(panel, Path.Combine(directory, $"cards-{(light ? "light" : "dark")}-{width}-{codexTier.Replace(" ", "-")}.png"), light);
                var tooltip = new ToolTip { Content = cards[^1].FullTooltip, Style = (Style)Application.Current.FindResource(typeof(ToolTip)) };
                tooltip.ApplyTemplate();
                tooltip.Measure(new Size(390, double.PositiveInfinity));
                tooltip.Arrange(new Rect(tooltip.DesiredSize));
                tooltip.UpdateLayout();
                TooltipDiagnostics.Verify(tooltip);
                Capture(tooltip, Path.Combine(directory, $"custom-tooltip-{(light ? "light" : "dark")}-{width}.png"), light);
            }
            finally { window.Close(); }
        }
    }

    private static void AssertFitsText(TextBlock text)
    {
        var untrimmed = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
            FontWeight = text.FontWeight, FontStyle = text.FontStyle, FontStretch = text.FontStretch
        };
        untrimmed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(text.ActualWidth + 0.5 >= untrimmed.DesiredSize.Width,
            $"'{text.Text}' is clipped: needs {untrimmed.DesiredSize.Width:F1}px, has {text.ActualWidth:F1}px");
    }

    private static void Capture(FrameworkElement element, string path, bool light)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen())
            drawing.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(245, 245, 249) : Color.FromRgb(32, 33, 36)),
                null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        image.Render(backdrop);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
