using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class StatisticsSummaryChecks
{
    internal static void Verify()
    {
        var at = new DateTimeOffset(DateTime.Today).AddDays(-1).AddHours(12);
        var records = new[] {
            new InteractionRecord("a", "Claude", "Opus 5", at, 88_000_000, 84_500_000, 1_000_000, 518_000, 50_000),
            new InteractionRecord("b", "Codex", "gpt-6", at, 17_000_000, 16_400_000, 0, 101_000, 10_000),
            new InteractionRecord("c", "Codex", "Opus 5", at, 1_000_000, 500_000, 0, 1000, 100),
            new InteractionRecord("d", "Claude", "Opus 5", at.AddDays(-10), 3_490_000_000, 3_409_000_000, 0, 20_000_000, 6_000_000)
        };
        var prices = PriceTable.Parse("""{"_currency":"USD","Opus 5":{"in":5,"out":25,"cache_read":0.5,"cache_write":6.25},"gpt-6":{"in":2.5,"out":10,"cache_read":0.25}}""");
        var stats = UsageStatistics.Build(records, DateTimeOffset.Now) with { Prices = prices };
        var panel = new StatisticsPanel();
        var window = new Window { Content = panel, Width = 420, Height = 860, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Padding = new Thickness(12) };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        try
        {
            UiLocalization.SetLanguage("zh"); panel.Update(stats, TokenUnit.International); window.Show(); Layout(window);
            Assert.Equal(4, Named<UniformGrid>(panel, "StatsMetrics").Children.Count);
            Assert.Equal("2", Named<TextBlock>(panel, "StatsActiveDays").Text);
            Assert.Equal($"{prices.Estimate(panel.CurrentPeriod.Models).Amount:N2} USD", Named<TextBlock>(panel, "StatsCost").Text);
            Assert.Equal(2, Models(panel).Length);
            Assert.Equal(2, Agents(panel).Length);
            Assert.Equal(panel.CurrentPeriod.TotalTokens, Agents(panel).Sum(x => x.TotalTokens));
            Assert.Equal(panel.CurrentPeriod.Calls, Agents(panel).Sum(x => x.Turns));
            Assert.Equal(100, Elements<TextBlock>(panel).Where(x => x.Name == "StatsAgentShare").Sum(x => (double)x.Tag), 8);
            Assert.Equal(panel.CurrentPeriod.TotalTokens, Named<Grid>(panel, "StatsTokenBar").Children.OfType<Border>().Sum(x => (long)x.Tag));
            Assert.Equal(panel.CurrentPeriod.TotalTokens, Named<Grid>(panel, "StatsModelBar").Children.OfType<Border>().Sum(x => (long)x.Tag));
            Assert.Equal(100, Elements<TextBlock>(panel).Where(x => x.Name == "StatsModelShare").Sum(x => (double)x.Tag), 8);
            var scroll = (ScrollViewer)panel.Content;
            foreach (var width in new[] { 385, 520 })
            foreach (var light in new[] { false, true })
            foreach (var language in new[] { "zh", "en" })
            {
                window.Width = width;
                ((ThemePalette)Application.Current.FindResource("ThemePalette")).IsLight = light;
                panel.RefreshTheme(); UiLocalization.SetLanguage(language); Layout(window);
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                foreach (var card in Named<UniformGrid>(panel, "StatsMetrics").Children.OfType<Border>())
                    Assert.InRange(card.ActualWidth, 100, width / 2);
                if (language == "en") Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
                scroll.ScrollToTop(); Layout(window);
                Capture(window, $"stats-{language}-{(light ? "light" : "dark")}-{width}-top.png");
                scroll.ScrollToEnd(); Layout(window);
                Capture(window, $"stats-{language}-{(light ? "light" : "dark")}-{width}-distribution.png");
            }
            UiLocalization.SetLanguage("zh");
            window.Width = 420;
            ((ThemePalette)Application.Current.FindResource("ThemePalette")).IsLight = false;
            var many = UsageStatistics.Build(records.Concat(Enumerable.Range(0, 6).Select(i =>
                new InteractionRecord("extra-" + i, "Codex", "model-" + i, at, 100_000 * (i + 1), 50_000, 0, 1_000, 100))), DateTimeOffset.Now) with { Prices = prices };
            panel.Update(many, TokenUnit.International); panel.RefreshTheme(); Layout(window);
            Assert.Equal(2, Models(panel).Length);
            Assert.Equal(2, Agents(panel).Length);
            Named<TextBlock>(panel, "StatsModelTitle").BringIntoView(); Layout(window);
            Capture(window, "stats-models-collapsed.png");
            Named<Button>(panel, "StatsModelsToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
            Assert.Equal(8, Models(panel).Length);
            Assert.Equal(many.ForPeriod(panel.CurrentPeriod.Start, panel.CurrentPeriod.End).TotalTokens, Agents(panel).Sum(x => x.TotalTokens));
            Capture(window, "stats-models-expanded.png");
            UiLocalization.SetLanguage("en"); Layout(window);
            Assert.Equal("Show top 2 models", Named<Button>(panel, "StatsModelsToggle").Content);
            Named<Button>(panel, "StatsModelsToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
            Assert.Equal("Show all 8 models", Named<Button>(panel, "StatsModelsToggle").Content);
            Assert.Equal(2, Models(panel).Length);
            UiLocalization.SetLanguage("zh"); panel.Update(stats, TokenUnit.International); Layout(window);
            Named<StackPanel>(panel, "StatsAgents").BringIntoView(); Layout(window);
            Capture(window, "stats-agents.png");
            panel.SelectDate(DateOnly.FromDateTime(at.LocalDateTime)); Layout(window);
            Assert.Equal(1, panel.CurrentPeriod.ActiveDays);
            Assert.Equal(2, Models(panel).Length);
            Assert.Contains("Claude / Codex", Models(panel).Single(x => x.Model == "Opus 5").Source);
            Assert.Equal(88_518_000, Agents(panel).Single(x => x.Name == "Claude").TotalTokens);
            Assert.Equal(18_102_000, Agents(panel).Single(x => x.Name == "Codex").TotalTokens);
            Assert.DoesNotContain(Elements<Button>(panel), x => x.Name == "StatsModelsToggle");
            panel.Update(stats with { Prices = null }); Layout(window);
            Assert.Equal("未配置价目表", Named<TextBlock>(panel, "StatsCost").Text);
            panel.Update(stats with { Prices = PriceTable.Parse("""{"unknown":{"in":1}}""") }); Layout(window);
            Assert.Equal("未定价", Named<TextBlock>(panel, "StatsCost").Text);
            panel.Update(stats with { Prices = PriceTable.Parse("""{"gpt-6":{"in":1,"out":1}}""") }); Layout(window);
            Assert.Contains("1 个模型未定价", Text(panel));
            Assert.NotEqual("0.00 USD", Named<TextBlock>(panel, "StatsCost").Text);
            var imported = OpenRouterPrices.Parse("""{"data":[{"id":"test/Opus 5","pricing":{"prompt":"0.000005","completion":"0.000025"}},{"id":"test/gpt-6","pricing":{"prompt":"0.0000025","completion":"0.00001"}}]}""", DateTimeOffset.Now);
            panel.Update(stats with { Prices = PriceTable.Parse(imported.Json) }); Layout(window);
            Assert.Contains("OpenRouter 基础 Token 单价", Text(panel));
            UiLocalization.SetLanguage("en"); Layout(window);
            Assert.Contains("OpenRouter base token rates", Text(panel));
            UiLocalization.SetLanguage("zh");
            panel.Update(UsageStatistics.Empty); Layout(window);
            Assert.Empty(Named<Grid>(panel, "StatsTokenBar").Children);
            Assert.Empty(Named<Grid>(panel, "StatsModelBar").Children);
            Assert.Equal("—", Named<TextBlock>(panel, "StatsCacheHit").Text);
            Assert.Equal("0", Named<TextBlock>(panel, "StatsActiveDays").Text);
            Assert.Empty(Agents(panel));
            scroll.ScrollToTop(); Layout(window); Capture(window, "stats-empty.png");
        }
        finally { UiLocalization.SetLanguage("zh"); window.Close(); }
    }
    private static ModelMix[] Models(DependencyObject panel) => Elements<TextBlock>(panel).Where(x => x.Tag is ModelMix).Select(x => (ModelMix)x.Tag).ToArray();
    private static UsageSourceSummary[] Agents(DependencyObject panel) => Elements<TextBlock>(panel).Where(x => x.Tag is UsageSourceSummary).Select(x => (UsageSourceSummary)x.Tag).ToArray();
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Assert.Single(Elements<T>(root), x => x.Name == name);
    private static string Text(DependencyObject root) => string.Join("\n", Elements<TextBlock>(root).Select(x => x.Text));
    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T value) yield return value;
            foreach (var nested in Elements<T>(child)) yield return nested;
        }
    }
    private static void Layout(Window window)
    {
        window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static void Capture(Window window, string name)
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/stats-agents-20260930/screenshots"));
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
}
