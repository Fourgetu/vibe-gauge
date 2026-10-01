using System.ComponentModel;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace VibeGauge.Windows;

public sealed class ThemePalette : INotifyPropertyChanged
{
    public const int DefaultLightTransparency = 75;
    public const int DefaultDarkTransparency = 40;
    private int lightTransparency = DefaultLightTransparency, darkTransparency = DefaultDarkTransparency;
    public int GetTransparency(bool lightTheme) => lightTheme ? lightTransparency : darkTransparency;
    public void SetTransparency(bool lightTheme, int value)
    {
        value = Math.Clamp(value, 0, 100);
        if (GetTransparency(lightTheme) == value) return;
        if (lightTheme) lightTransparency = value; else darkTransparency = value;
        if (lightTheme == IsLight) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
    // Bound colors keep shared brushes live, including styles and programmatic panels.
    private static readonly IReadOnlyDictionary<string, (string Dark, string Light)> Colors =
        new Dictionary<string, (string, string)>
        {
            ["WindowBrush"] = ("#FF292A2F", "#FFF1F3F7"),
            // Acrylic supplies the material. Keep its tint visible and build
            // nested surfaces from subtle secondary overlays in either theme.
            ["WindowTintBrush"] = ("#98202126", "#40E1E4E7"),
            ["PanelBrush"] = ("#08FFFFFF", "#08000000"),
            ["CardBrush"] = ("#0EFFFFFF", "#10000000"),
            ["CardRaisedBrush"] = ("#1CFFFFFF", "#18000000"),
            ["HoverBrush"] = ("#24FFFFFF", "#18000000"),
            ["BorderBrush"] = ("#14FFFFFF", "#22000000"),
            ["BorderStrongBrush"] = ("#26FFFFFF", "#39000000"),
            ["TextPrimaryBrush"] = ("#FFE5E5E8", "#FF20242C"),
            ["TextSecondaryBrush"] = ("#FFD0D2D7", "#FF323842"),
            ["TextMutedBrush"] = ("#FFC6CAD0", "#FF3B424D"),
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
            ["StatsPinkBrush"] = ("#FFFF739F", "#FFB12B58"),
            ["StatsTealBrush"] = ("#FF3DCDD0", "#FF007F83"),
            ["LocalDimBrush"] = ("#28D946EF", "#20A840C4"),
            ["TooltipBackgroundBrush"] = ("#FF24262E", "#FFFAFBFE"),
            ["TooltipTextBrush"] = ("#FFF5F7FB", "#FF1D2633"),
            ["TooltipBorderBrush"] = ("#FF686E7C", "#FF929BAA"),
            ["SelectedSegmentBrush"] = ("#B0696C6A", "#B03A403C"),
            ["SelectedSegmentTextBrush"] = ("#FFFFFFFF", "#FFFFFFFF"),
            ["PopupBrush"] = ("#FF35363D", "#FFF7F8FB"),
            ["GripBrush"] = ("#36FFFFFF", "#55000000"),
            ["SubtleSurfaceBrush"] = ("#08FFFFFF", "#06000000"),
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
    public Color this[string role]
    {
        get
        {
            var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(IsLight ? Colors[role].Light : Colors[role].Dark);
            if (role == "WindowTintBrush") color.A = (byte)Math.Round(255 * (100 - GetTransparency(IsLight)) / 100d);
            return color;
        }
    }
}
