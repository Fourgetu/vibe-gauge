using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using ToolTip = System.Windows.Controls.ToolTip;

namespace VibeGauge.Windows;

public static class TooltipDiagnostics
{
    internal static async Task<object> CaptureAsync(MainWindow window, string kind, string path)
    {
        var target = Descendants<FrameworkElement>(window).FirstOrDefault(element => element.IsVisible &&
            element.ToolTip is string text && (kind.StartsWith("provider:", StringComparison.Ordinal) ?
                element.DataContext is ViewModels.PlatformRow row && row.Name == kind["provider:".Length..] :
                kind == "refresh" ? text == "立即刷新" :
                text.Length > 10 && text[4] == '-' && text.Contains("\n调用 ") && !text.Contains("\n调用 0 次")))
            ?? throw new InvalidOperationException("Tooltip target was not visible: " + kind);
        var original = target.ToolTip;
        var tooltip = new ToolTip { Content = original, PlacementTarget = target, StaysOpen = true };
        target.ToolTip = tooltip;
        try
        {
            tooltip.IsOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(350);
            tooltip.UpdateLayout();
            var checks = Verify(tooltip);
            WindowDiagnostics.CaptureCompositedWindow(window, path);
            var origin = tooltip.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(tooltip);
            var width = (int)Math.Ceiling(tooltip.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(tooltip.ActualHeight * dpi.DpiScaleY);
            using var image = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(image);
            graphics.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(width, height));
            image.Save(path + ".tooltip.png", System.Drawing.Imaging.ImageFormat.Png);
            return checks;
        }
        finally { tooltip.IsOpen = false; target.ToolTip = original; }
    }

    public static object Verify(ToolTip tooltip)
    {
        var background = (tooltip.Background as SolidColorBrush)?.Color
            ?? throw new InvalidOperationException("Tooltip background is not a solid brush");
        if (background.A != 255) throw new InvalidOperationException("Tooltip must have an opaque background");
        var text = Descendants<TextBlock>(tooltip).Where(x => x.Text.Length > 0).ToArray();
        if (text.Length == 0) throw new InvalidOperationException("Tooltip text did not render");
        var contrasts = text.Select(block =>
        {
            var foreground = (block.Foreground as SolidColorBrush)?.Color
                ?? throw new InvalidOperationException("Tooltip text has no solid foreground");
            if (foreground.A != 255 || block.FontSize < 13 || block.TextWrapping != TextWrapping.Wrap)
                throw new InvalidOperationException($"Tooltip text must be opaque, at least 13pt, and wrap: color={foreground}, size={block.FontSize}, wrapping={block.TextWrapping}");
            return Contrast(foreground, background);
        }).ToArray();
        if (contrasts.Min() < 7) throw new InvalidOperationException("Tooltip contrast fell below 7:1");
        return new
        {
            Text = tooltip.Content?.ToString(),
            Background = background.ToString(),
            MinimumContrastRatio = Math.Round(contrasts.Min(), 2),
            FontSize = tooltip.FontSize,
            Width = tooltip.ActualWidth,
            Height = tooltip.ActualHeight,
            TextWrappingVerified = true
        };
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var first = Luminance(foreground); var second = Luminance(background);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var value in Descendants<T>(child)) yield return value;
        }
    }
}
