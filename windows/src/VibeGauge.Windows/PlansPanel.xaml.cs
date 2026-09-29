using System.Windows;
using VibeGauge.Core;
using VibeGauge.Windows.ViewModels;
using Wpf = System.Windows.Controls;
using Size = System.Windows.Size;

namespace VibeGauge.Windows;

public partial class PlansPanel : Wpf.UserControl
{
    public PlansPanel() => InitializeComponent();
    public event Action<string>? ProviderSelected;
    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlatformRow { CanOpenDetail: true } row }) ProviderSelected?.Invoke(row.Name);
    }
    public void Update(SessionSummary? sessions, TokenUnit unit = TokenUnit.Chinese) => SessionsView.Update(sessions, unit);
    internal void ScrollToUsageForCapture()
    {
        UpdateLayout();
        var content = (System.Windows.Media.Visual)PlansScroll.Content;
        var point = UsageSummaryHeading.TransformToAncestor(content).Transform(new System.Windows.Point(0, 0));
        PlansScroll.ScrollToVerticalOffset(point.Y);
        UpdateLayout();
    }
}

// A row shares its own height, not the height of every card in the dashboard.
// Gemini stays beside ZCode; other dual-pool and long-name providers can span both columns.
public sealed class ProviderCardPanel : Wpf.Panel
{
    private const double Gap = 7;
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 460;
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(Wide(child) ? width : (width - Gap) / 2, double.PositiveInfinity));
        return new Size(width, Layout(width, false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, true);
        return finalSize;
    }

    private double Layout(double width, bool arrange)
    {
        var half = Math.Max(0, (width - Gap) / 2);
        double y = 0;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var left = InternalChildren[index];
            var wide = Wide(left);
            var right = !wide && index + 1 < InternalChildren.Count && !Wide(InternalChildren[index + 1])
                ? InternalChildren[index + 1] : null;
            var height = Math.Max(left.DesiredSize.Height, right?.DesiredSize.Height ?? 0);
            if (arrange)
            {
                left.Arrange(new Rect(0, y, wide ? width : half, height));
                right?.Arrange(new Rect(half + Gap, y, half, height));
            }
            if (right is not null) index++;
            y += height + Gap;
        }
        return Math.Max(0, y - Gap);
    }

    private static bool Wide(UIElement child) => child is FrameworkElement { DataContext: PlatformRow { IsWide: true } };
}
