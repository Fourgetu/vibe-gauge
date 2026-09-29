using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace VibeGauge.Windows;

internal static class QuotaVisuals
{
    internal static Color ColorForPercent(int percent)
    {
        // Matches DashboardView.swift quotaColor: HSB(0.33 * (1 - p^2), 0.82, 0.82).
        var p = Math.Clamp(percent, 0, 100) / 100d;
        var hue = .33 * (1 - p * p) * 6;
        const double chroma = .82 * .82;
        const double minimum = .82 - chroma;
        var x = chroma * (1 - Math.Abs(hue % 2 - 1));
        var (r, g, b) = hue switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + minimum) * 255),
            (byte)Math.Round((g + minimum) * 255), (byte)Math.Round((b + minimum) * 255));
    }

    internal static SolidColorBrush BrushForPercent(int percent)
    {
        var brush = new SolidColorBrush(ColorForPercent(percent));
        brush.Freeze();
        return brush;
    }
}
