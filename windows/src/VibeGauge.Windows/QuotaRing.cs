using System.Windows;
using System.Windows.Media;
using VibeGauge.Windows.ViewModels;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace VibeGauge.Windows;

public sealed class QuotaRing : FrameworkElement
{
    private static readonly Typeface RingTypeface = new(
        new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    public QuotaRing()
    {
        Width = 48; Height = 48;
        DataContextChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (DataContext is not QuotaRow row) return;
        var track = (Brush)FindResource("TrackBrush");
        var center = new Point(24, 24);
        dc.DrawEllipse(null, new Pen(track, 4.2), center, 20, 20);
        if (row.Tone != "Muted" && row.Percent > 0)
        {
            var pen = new Pen(QuotaVisuals.BrushForPercent((int)Math.Round(row.Percent)), 4.2)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (row.Percent >= 100) dc.DrawEllipse(null, pen, center, 20, 20);
            else
            {
                var angle = row.Percent / 100 * Math.PI * 2;
                var figure = new PathFigure { StartPoint = new Point(24, 4) };
                figure.Segments.Add(new ArcSegment(new Point(24 + 20 * Math.Sin(angle), 24 - 20 * Math.Cos(angle)),
                    new Size(20, 20), 0, row.Percent > 50, SweepDirection.Clockwise, true));
                dc.DrawGeometry(null, pen, new PathGeometry([figure]));
            }
        }
        var text = new FormattedText(row.PercentText, System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight, RingTypeface, 11,
            (Brush)FindResource(row.Tone == "Muted" ? "TextMutedBrush" : "TextPrimaryBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(24 - text.Width / 2, 24 - text.Height / 2));
        ToolTip = row.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(this, $"{row.Label} {row.PercentText} {row.TrustText}");
    }
}
