using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VibeGauge.Windows.Services;

public static class WindowAppearance
{
    public static bool Apply(Window window, bool light = false) => Apply(window, light, "acrylic");

    public static bool Apply(Window window, bool light, string backdropMode)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        var dark = light ? 0 : 1;
        var rounded = 2; // DWMWCP_ROUND
        // Native Win11 1px rim follows hardware rounded corners cleanly at any DPI.
        var borderColor = SystemParameters.HighContrast ? -2
            : light ? 0x00D8D0C8 // RGB(200, 208, 216) in 0x00BBGGRR
            : 0x00524842;        // RGB(66, 72, 82) in 0x00BBGGRR
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));

        var wantsBackdrop = backdropMode is "acrylic" or "mica";
        // Layered WPF windows cannot participate in the system backdrop.
        // Keep the HWND non-layered and let DWM compose acrylic or mica behind WPF.
        if (!wantsBackdrop || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                var none = 1; // DWMSBT_NONE
                DwmSetWindowAttribute(handle, 38, ref none, sizeof(int));
                var resetMargins = new Margins { Left = 0, Right = 0, Top = 0, Bottom = 0 };
                DwmExtendFrameIntoClientArea(handle, ref resetMargins);
            }
            return false;
        }

        var backdrop = backdropMode == "mica" ? 4 : 3; // 4 = DWMSBT_TABBEDWINDOW (Mica Alt), 3 = DWMSBT_TRANSIENTWINDOW (Desktop Acrylic)
        if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) != 0) return false;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0) return false;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        KeepBackdropActive(handle);
        return true;
    }

    // Keep the non-client material active without changing input activation.
    // WM_ACTIVATE still reaches WPF, so focus, pinning and auto-hide work normally.
    // lParam=-1 suppresses the standard frame paint on our custom WPF chrome.
    internal static IntPtr KeepBackdropActive(IntPtr handle)
    {
        DefWindowProc(handle, 0x0086, new IntPtr(1), new IntPtr(-1));
        // TRUE must be returned for the original deactivation notification;
        // returning DefWindowProc's active-state result can veto focus transfer.
        return new IntPtr(1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
