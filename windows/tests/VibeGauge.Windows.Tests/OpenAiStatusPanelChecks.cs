using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class OpenAiStatusPanelChecks
{
    internal static void Verify()
    {
        var at = DateTimeOffset.Now;
        var days = Enumerable.Range(0, 90).Select(i => new ServiceDay(DateOnly.FromDateTime(at.UtcDateTime).AddDays(i - 89),
            i is 12 or 34 or 62 ? ServiceHealth.Degraded : ServiceHealth.Operational, i == 12 ? ["Elevated errors"] : [])).ToArray();
        var groups = new[] { ("APIs", 13), ("ChatGPT", 16), ("Codex", 4), ("FedRAMP", 1), ("Ads Platform", 2) }
            .Select(g => new ServiceStatusRow(g.Item1, g.Item1, ServiceHealth.Operational, g.Item1 == "Ads Platform" ? null : 99.95m,
                g.Item1 == "Ads Platform" ? [] : days, Enumerable.Range(0, g.Item2).Select(i =>
                    new ServiceStatusRow(g.Item1 + i, i == 0 ? "App" : "Component " + i, ServiceHealth.Operational, 99.95m, days, [])).ToArray())).ToArray();
        var snapshot = new OpenAiStatusSnapshot(at, 90, groups);
        var calls = 0; var fail = false; var schemaFailed = false;
        var panel = new OpenAiStatusPanel(false, _ => { calls++; return fail
            ? Task.FromException<OpenAiStatusSnapshot>(schemaFailed ? new InvalidDataException("schema changed") : new HttpRequestException("test")) : Task.FromResult(snapshot); });
        var window = new Window { Content = panel, Width = 390, SizeToContent = SizeToContent.Height,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        try
        {
            window.Show(); Layout(window);
            Assert.Equal(0, calls);
            panel.RefreshAsync().GetAwaiter().GetResult(); Layout(window);
            Assert.Equal(1, calls);
            var groupsUi = Elements<Expander>(panel).ToArray();
            Assert.Equal(5, groupsUi.Length);
            Assert.All(groupsUi, x => Assert.False(x.IsExpanded));
            Assert.All(groupsUi, x => Assert.Empty(((StackPanel)x.Content).Children));
            foreach (var light in new[] { false, true })
            foreach (var english in new[] { false, true })
            {
                UiLocalization.SetLanguage(english ? "en" : "zh");
                ((ThemePalette)Application.Current.FindResource("ThemePalette")).IsLight = light;
                window.Width = english ? 490 : 390; Layout(window);
                Assert.InRange(panel.ActualWidth, 300, 500);
                foreach (var grid in Elements<System.Windows.Controls.Primitives.UniformGrid>(panel))
                {
                    Assert.Equal(90, grid.Children.Count);
                    Assert.True(grid.ActualWidth > 250, $"History width: {grid.ActualWidth}");
                    Assert.True(grid.TranslatePoint(new Point(grid.ActualWidth, 0), panel).X <= panel.ActualWidth);
                }
                if (english) Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
                Capture(panel, $"status-{(light ? "light" : "dark")}-{(english ? "en" : "zh")}.png");
            }
            UiLocalization.SetLanguage("zh");
            groupsUi[0].IsExpanded = true; Layout(window);
            Assert.Equal(13, ((StackPanel)groupsUi[0].Content).Children.Count);
            panel.ShowSnapshot(snapshot); Layout(window);
            Assert.True(Elements<Expander>(panel).First().IsExpanded);
            fail = true; panel.RefreshAsync().GetAwaiter().GetResult(); Layout(window);
            Assert.Contains("以下为旧数据", Text(panel));
            Assert.Equal(5, Elements<Expander>(panel).Count());
            fail = false; panel.RefreshAsync().GetAwaiter().GetResult(); Layout(window);
            Assert.DoesNotContain("以下为旧数据", Text(panel));
            schemaFailed = true; fail = true;
            panel.RefreshAsync().GetAwaiter().GetResult(); Layout(window);
            Assert.Contains("以下为旧数据", Text(panel));
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
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle((Brush)Application.Current.FindResource("WindowBrush"), null,
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        image.Render(backdrop);
        image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var folder = Path.Combine(AppContext.BaseDirectory, "status-captures"); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }
}
