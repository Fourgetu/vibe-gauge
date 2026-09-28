using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VibeGauge.Windows.Services;
using Point = System.Windows.Point;

namespace VibeGauge.Windows;

internal static class WindowDiagnostics
{
    internal static object Verify(MainWindow window, TrayIconManager tray, string directory)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int Hit(FrameworkElement element, double x, double y)
        {
            var pt = element.PointToScreen(new Point(x, y));
            var packed = ((int)pt.Y << 16) | ((int)pt.X & 0xffff);
            return (int)SendMessage(handle, 0x0084, IntPtr.Zero, new IntPtr(packed));
        }
        var caption = Hit(window, window.ActualWidth / 2, 10);
        var tab = Hit(window.PlansTab, window.PlansTab.ActualWidth / 2, window.PlansTab.ActualHeight / 2);
        if (caption != 2) throw new InvalidOperationException($"Drag strip must be HTCAPTION, was {caption}");
        if (tab != 1) throw new InvalidOperationException($"Navigation must remain HTCLIENT, was {tab}");
        var resize = Hit(window, window.ActualWidth - 2, window.ActualHeight - 2);
        if (resize != 17) throw new InvalidOperationException($"Resize corner must be HTBOTTOMRIGHT, was {resize}");
        var layered = (GetWindowLongPtr(handle, -20).ToInt64() & 0x80000) != 0;
        if (layered) throw new InvalidOperationException("A layered HWND disables the native backdrop");
        var backdrop = 0;
        DwmGetWindowAttribute(handle, 38, out backdrop, sizeof(int));
        if (window.UsesSystemBackdrop && backdrop != 3) throw new InvalidOperationException("Acrylic backdrop was lost");

        var initialTheme = window.IsLightTheme;
        window.ThemeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (window.IsLightTheme == initialTheme || new ThemeSettings(directory).IsLight != window.IsLightTheme)
            throw new InvalidOperationException("Theme button did not switch and persist the preference");
        var darkFrame = 0;
        if (window.UsesSystemBackdrop && (DwmGetWindowAttribute(handle, 20, out darkFrame, sizeof(int)) != 0 || darkFrame != (window.IsLightTheme ? 0 : 1)))
            throw new InvalidOperationException("Native window material did not follow the theme switch");
        window.ThemeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (window.IsLightTheme != initialTheme || new ThemeSettings(directory).IsLight != initialTheme)
            throw new InvalidOperationException("Theme button did not restore the original preference");

        // Exercise the same enter/exit move messages used by the native caption loop.
        // Do not inject input into the user's desktop or touch another application's HWND.
        SendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
        window.Left -= 24;
        SendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
        var left = window.Left;
        var top = window.Top;
        window.Hide(); tray.ShowWindow();
        if (Math.Abs(window.Left - left) > 1 || Math.Abs(window.Top - top) > 1)
            throw new InvalidOperationException("Tray reopen changed the dragged position");
        var saved = new WindowPlacementStore(directory).Load();
        if (saved is null || Math.Abs(saved.Left - left) > 1)
            throw new InvalidOperationException("Window placement was not persisted");
        var meters = Descendants<System.Windows.Controls.ProgressBar>(window).Where(x => x.IsVisible).ToArray();
        if (meters.Length == 0 || meters.Any(x => !double.IsFinite(x.ActualWidth) || x.ActualWidth <= 0))
            throw new InvalidOperationException("Visible quota meters did not receive a valid layout");
        return new
        {
            CaptionHit = caption,
            NavigationHit = tab,
            ResizeHit = resize,
            LayeredWindow = layered,
            BackdropType = backdrop,
            NativeAcrylicRequested = window.UsesSystemBackdrop,
            ThemeToggleRoundTrip = true,
            ThemePreferencePersisted = true,
            Theme = initialTheme ? "light" : "dark",
            PositionPreserved = true,
            PlacementPersisted = true,
            VisibleMeters = meters.Length,
            Width = window.ActualWidth,
            Height = window.ActualHeight
        };
    }

    internal static void CaptureCompositedWindow(MainWindow window, string path)
    {
        // RenderTargetBitmap omits DWM material. This explicit diagnostic mode captures
        // only this application's visible bounds, including the compositor backdrop.
        var origin = window.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
        using var image = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(image);
        graphics.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(width, height));
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
