using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class RemoteApiUsagePanelChecks
{
    internal static void Verify()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var metrics = new RemoteUsageMetrics(80, 100000, 20000, 60000, 0, 5000, 120000, 0.25m, false);
        var buckets = Enumerable.Range(0, 30).Select(i => new RemoteUsageBucket(start.AddDays(i), 86400,
            start.AddDays(i).ToString("MM-dd"), metrics with { Total = (i + 1) * 4000 })).ToArray();
        var recent = new RemoteUsagePeriod(start, start.AddDays(30), RemoteUsageMetrics.Sum(buckets.Select(x => x.Metrics)), buckets, "ready");
        var today = new RemoteUsagePeriod(start.AddDays(29), start.AddDays(30), metrics, [buckets[^1]], "ready");
        var report = new RemoteUsageReport(start.AddDays(30), today, recent, buckets,
            [new("1", "2026-10-30 12:30:00", "gpt-test", "success", 12000, 350), new("2", "2026-10-30 12:31:00", "gpt-test", "running", null, null)]);
        var provider = new PlatformStatus("Test site · abc12345", CodexProxyRsParser.Kind, false, 0,
            ProviderDataState.Available, "", RemoteUsage: report);
        var panel = new RemoteApiUsagePanel();
        panel.Update([], TokenUnit.International); Assert.Equal(Visibility.Collapsed, panel.Visibility);
        panel.Update([provider], TokenUnit.International);
        var window = new Window { Content = panel, Width = 390, SizeToContent = SizeToContent.Height,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        try
        {
            window.Show(); Layout(window);
            Assert.Contains("120 K", Text(panel));
            var expander = Assert.Single(Elements<Expander>(panel)); expander.IsExpanded = true; Layout(window);
            Assert.Contains("用量未知", Text(panel));
            panel.Update([provider], TokenUnit.Chinese); Layout(window);
            Assert.True(Assert.Single(Elements<Expander>(panel)).IsExpanded);
            Assert.Contains("12.0 万", Text(panel));
            foreach (var light in new[] { false, true })
            foreach (var english in new[] { false, true })
            foreach (var width in new[] { 380, 480 })
            {
                UiLocalization.SetLanguage(english ? "en" : "zh");
                ((ThemePalette)Application.Current.FindResource("ThemePalette")).IsLight = light;
                window.Width = width;
                panel.Update([provider], TokenUnit.International); Layout(window);
                foreach (var period in new[] { "today", "30d", "all" })
                {
                    var index = period == "today" ? 0 : period == "30d" ? 1 : 2;
                    Elements<Button>(panel).ElementAt(index).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
                    if (english) Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
                    Assert.All(Elements<TextBlock>(panel).Where(x => x.IsVisible), block =>
                        Assert.True(block.TranslatePoint(new Point(block.ActualWidth, 0), panel).X <= panel.ActualWidth + 1));
                }
                Capture(panel, $"remote-{(light ? "light" : "dark")}-{(english ? "en" : "zh")}-{width}.png");
            }
            UiLocalization.SetLanguage("zh");
            panel.Update([provider with { DataState = ProviderDataState.ReadFailed, Detail = "站点响应超时，请稍后重试",
                RemoteUsage = report with { Note = "查询失败，以下为旧数据：站点响应超时，请稍后重试" } }], TokenUnit.International);
            Layout(window); Assert.Contains("以下为旧数据", Text(panel)); Assert.Contains("已同步历史范围", Text(panel));
            panel.Update([], TokenUnit.International); Assert.Equal(Visibility.Collapsed, panel.Visibility);
        }
        finally { window.Close(); UiLocalization.SetLanguage("zh"); }
    }
    private static void Layout(Window window)
    { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static string Text(DependencyObject root) => string.Join("\n", Elements<TextBlock>(root).Where(x => x.IsVisible).Select(x => x.Text));
    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Elements<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle((Brush)Application.Current.FindResource("WindowBrush"), null,
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(backdrop); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(AppContext.BaseDirectory, "remote-captures"); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }
}
