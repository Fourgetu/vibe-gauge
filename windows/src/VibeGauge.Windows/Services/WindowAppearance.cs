using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VibeGauge.Windows.Services;

public static class WindowAppearance
{
    public static bool Apply(Window window, bool light = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        var dark = light ? 0 : 1;
        var rounded = 2;
        var noSystemBorder = -2;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref noSystemBorder, sizeof(int));
        // Layered WPF windows cannot participate in the system backdrop.
        // Keep the HWND non-layered and let DWM compose acrylic behind WPF.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
            return false;
        var acrylic = 3; // DWMSBT_TRANSIENTWINDOW (Desktop Acrylic)
        if (DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int)) != 0) return false;
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
