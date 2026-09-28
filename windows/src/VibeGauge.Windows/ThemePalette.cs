using System.ComponentModel;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace VibeGauge.Windows;

public sealed class ThemePalette : INotifyPropertyChanged
{
    // Bound colors keep shared brushes live, including styles and programmatic panels.
    private static readonly IReadOnlyDictionary<string, (string Dark, string Light)> Colors =
        new Dictionary<string, (string, string)>
        {
            ["WindowBrush"] = ("#FF292A2F", "#FFF1F3F7"),
            ["WindowTintBrush"] = ("#D2202126", "#D9F4F5F8"),
            ["PanelBrush"] = ("#0AFFFFFF", "#60FFFFFF"),
            ["CardBrush"] = ("#12FFFFFF", "#A6FFFFFF"),
            ["CardRaisedBrush"] = ("#20FFFFFF", "#D6FFFFFF"),
            ["HoverBrush"] = ("#30FFFFFF", "#18000000"),
            ["BorderBrush"] = ("#14FFFFFF", "#22000000"),
            ["BorderStrongBrush"] = ("#26FFFFFF", "#39000000"),
            ["TextPrimaryBrush"] = ("#FFE5E5E8", "#FF20242C"),
            ["TextSecondaryBrush"] = ("#FFB1B1B9", "#FF4D535E"),
            ["TextMutedBrush"] = ("#FF9999A2", "#FF626976"),
            ["GoodBrush"] = ("#FF32D05D", "#FF137B3A"),
            ["GoodDimBrush"] = ("#2832D05D", "#201B8844"),
            ["WarningBrush"] = ("#FFFF9A36", "#FF9A4A00"),
            ["WarningDimBrush"] = ("#FF554722", "#20AE5700"),
            ["DangerBrush"] = ("#FFEF6A70", "#FFB4232B"),
            ["DangerDimBrush"] = ("#FF57292C", "#20C33030"),
            ["MutedBrush"] = ("#FF73737C", "#FF7E858F"),
            ["TrackBrush"] = ("#FF515158", "#FFCCD1D9"),
            ["AccentBrush"] = ("#FF22A8FF", "#FF0069B4"),
            ["LocalBrush"] = ("#FFE26BFF", "#FF9936BB"),
            ["LocalDimBrush"] = ("#28D946EF", "#20A840C4"),
            ["TooltipBackgroundBrush"] = ("#FF24262E", "#FFFAFBFE"),
            ["TooltipTextBrush"] = ("#FFF5F7FB", "#FF1D2633"),
            ["TooltipBorderBrush"] = ("#FF686E7C", "#FF929BAA"),
            ["SelectedSegmentBrush"] = ("#32FFFFFF", "#E8FFFFFF"),
            ["PopupBrush"] = ("#FF35363D", "#FFF7F8FB"),
            ["GripBrush"] = ("#36FFFFFF", "#55000000"),
            ["SubtleSurfaceBrush"] = ("#08FFFFFF", "#45FFFFFF"),
            ["HeatZeroBrush"] = ("#FF2D2F2E", "#FFE0E5EB")
        };

    private bool light;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsLight
    {
        get => light;
        set
        {
            if (light == value) return;
            light = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLight)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }
    public Color this[string role] => (Color)System.Windows.Media.ColorConverter.ConvertFromString(IsLight ? Colors[role].Light : Colors[role].Dark);
}
