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
    // Frosted white-mist layered glass paired with comfortable, glare-free high-clarity
    // typography and translucent glass switch controls.
    private static readonly IReadOnlyDictionary<string, (string Dark, string Light)> Colors =
        new Dictionary<string, (string, string)>
        {
            ["WindowBrush"] = ("#FF22242A", "#FFF2F4F8"),
            ["WindowTintBrush"] = ("#98181A20", "#40EAEEF4"),
            ["WindowRimBrush"] = ("#28FFFFFF", "#20000000"),
            ["PanelBrush"] = ("#0DFFFFFF", "#1CFFFFFF"),
            ["CardBrush"] = ("#14FFFFFF", "#58FFFFFF"),
            ["CardHoverBrush"] = ("#22FFFFFF", "#7EFFFFFF"),
            ["CardRaisedBrush"] = ("#24FFFFFF", "#84FFFFFF"),
            ["HoverBrush"] = ("#28FFFFFF", "#16000000"),
            ["BorderBrush"] = ("#20FFFFFF", "#1E000000"),
            ["BorderStrongBrush"] = ("#34FFFFFF", "#32000000"),
            // Sweet-spot 3-tier hierarchy: glare-free off-white yet crisp over bright frosted white mist.
            ["TextPrimaryBrush"] = ("#FFF6F8FA", "#FF141820"),
            ["TextSecondaryBrush"] = ("#FFEAEEF5", "#FF2A313D"),
            ["TextMutedBrush"] = ("#FFDCE2EC", "#FF3E4756"),
            ["GoodBrush"] = ("#FF64F596", "#FF137A39"),
            ["GoodDimBrush"] = ("#6E0C3D22", "#2215803D"),
            ["WarningBrush"] = ("#FFFFDF6E", "#FFA34800"),
            ["WarningDimBrush"] = ("#684E2E04", "#22D97706"),
            ["StatusOrangeBrush"] = ("#FFFF9A5C", "#FFC94815"),
            ["DangerBrush"] = ("#FFFF8A92", "#FFC01A26"),
            ["DangerDimBrush"] = ("#665E141C", "#22DC2626"),
            ["MutedBrush"] = ("#FFE4EAF2", "#FF5E6778"),
            ["TrackBrush"] = ("#42000000", "#22000000"),
            ["SwitchTrackOffBrush"] = ("#20FFFFFF", "#15000000"),
            ["SwitchThumbOffBrush"] = ("#B4CFD6E2", "#9A4A5464"),
            ["GoodSwitchTrackBrush"] = ("#4028C860", "#2A15803D"),
            ["GoodSwitchBorderBrush"] = ("#743CE072", "#6415803D"),
            ["AccentSwitchTrackBrush"] = ("#4432A8F0", "#280067C0"),
            ["AccentSwitchBorderBrush"] = ("#7652C7FF", "#640067C0"),
            ["AccentBrush"] = ("#FF4AC0FF", "#FF0067C0"),
            ["AccentHoverBrush"] = ("#FF6BCEFF", "#FF1876C8"),
            ["AccentDimBrush"] = ("#3438B6FF", "#200067C0"),
            ["AccentTextBrush"] = ("#FFFFFFFF", "#FFFFFFFF"),
            ["LocalBrush"] = ("#FFE894FF", "#FF8626D9"),
            ["StatsPinkBrush"] = ("#FFFF7FA8", "#FFB12B58"),
            ["StatsTealBrush"] = ("#FF45D6DA", "#FF00767A"),
            ["LocalDimBrush"] = ("#5C4B1668", "#208626D9"),
            ["TransparentBrush"] = ("#00FFFFFF", "#00FFFFFF"),
            ["TooltipBackgroundBrush"] = ("#FF1A1C23", "#FFFFFFFF"),
            ["TooltipTextBrush"] = ("#FFF5F7FB", "#FF141820"),
            ["TooltipBorderBrush"] = ("#FF464C5C", "#FFBCC4D2"),
            ["SelectedSegmentBrush"] = ("#32FFFFFF", "#EAFFFFFF"),
            ["SelectedSegmentBorderBrush"] = ("#3EFFFFFF", "#24000000"),
            ["SelectedSegmentTextBrush"] = ("#FFF4F6FA", "#FF141820"),
            ["PopupBrush"] = ("#FF24262E", "#FFFDFEFF"),
            ["ToastBrush"] = ("#EE1B1E26", "#F5FFFFFF"),
            ["GripBrush"] = ("#54FFFFFF", "#4E000000"),
            ["SubtleSurfaceBrush"] = ("#10FFFFFF", "#3CFFFFFF"),
            // 5-level emerald heatmap ramp tuned for frosted white-mist glass.
            ["HeatZeroBrush"] = ("#1CFFFFFF", "#16000000"),
            ["HeatLevel1Brush"] = ("#FF1B5736", "#FFC0ECD0"),
            ["HeatLevel2Brush"] = ("#FF238A4F", "#FF74D695"),
            ["HeatLevel3Brush"] = ("#FF2DB866", "#FF31B062"),
            ["HeatLevel4Brush"] = ("#FF42E882", "#FF127536")
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
