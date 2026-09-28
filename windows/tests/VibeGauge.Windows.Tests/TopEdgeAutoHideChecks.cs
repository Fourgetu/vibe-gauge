using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VibeGauge.Windows.Services;
using Xunit;
using Forms = System.Windows.Forms;

namespace VibeGauge.Windows.Tests;

internal static class TopEdgeAutoHideChecks
{
    // Use the shared WPF application, synthetic pointer positions, and our own HWND only.
    internal static void Verify()
    {
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var window = new Window
            {
                Width = 420, Height = 500, WindowStyle = WindowStyle.None,
                ShowActivated = false, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize,
                Left = screen.WorkingArea.Left + 60, Top = screen.WorkingArea.Top + 100
            };
            using var edge = new TopEdgeAutoHide(window, pollPointer: false);
            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                Assert.True(GetWindowRect(handle, out var initial));
                var dpi = VisualTreeHelper.GetDpi(window);
                window.Left += (screen.WorkingArea.Left + 60 - initial.Left) / dpi.DpiScaleX;
                window.Top += (screen.WorkingArea.Top + 6 - initial.Top) / dpi.DpiScaleY;
                window.UpdateLayout();
                edge.CompleteMove();
                Assert.True(edge.IsDocked);
                Assert.False(edge.IsCollapsed);
                var expanded = edge.PlacementTop;
                Assert.True(GetWindowRect(handle, out var visible));
                Assert.InRange(visible.Top, screen.WorkingArea.Top - 1, screen.WorkingArea.Top + 1);
                var away = new System.Drawing.Point(visible.Right + 100, visible.Bottom + 100);
                var inside = new System.Drawing.Point(visible.Left + 50, visible.Top + 50);
                var now = Environment.TickCount64;
                edge.UpdatePointer(inside, now);
                edge.UpdatePointer(away, now + 699);
                Assert.False(edge.IsCollapsed);
                edge.UpdatePointer(away, now + 700);
                Assert.True(edge.IsCollapsed);
                edge.AdvanceAnimation(TimeSpan.FromMilliseconds(16));
                Assert.True(edge.IsAnimating);
                Assert.InRange(expanded - window.Top, 0.1, window.Height * 0.03);
                Finish(edge);
                Assert.False(edge.IsAnimating);
                Assert.True(GetWindowRect(handle, out var hidden));
                Assert.InRange(hidden.Bottom, screen.Bounds.Top + 1, screen.Bounds.Top + 3);
                Assert.Equal(expanded, edge.PlacementTop);
                var region = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    Assert.NotEqual(0, GetWindowRgn(handle, region));
                    Assert.NotEqual(0, GetRgnBox(region, out var clip));
                    Assert.InRange(clip.Bottom - clip.Top, 1, 3);
                }
                finally { DeleteObject(region); }

                // Unrelated top-edge locations must not open this window.
                edge.UpdatePointer(new System.Drawing.Point(hidden.Right + 50, screen.Bounds.Top), now + 1000);
                Assert.True(edge.IsCollapsed);
                edge.UpdatePointer(new System.Drawing.Point(hidden.Left + 50, screen.Bounds.Top), now + 1100);
                Assert.False(edge.IsCollapsed);
                var start = window.Top;
                edge.AdvanceAnimation(TimeSpan.FromMilliseconds(120));
                Assert.InRange(window.Top, start + 1, expanded - 1);
                Finish(edge);
                Assert.Equal(expanded, window.Top, 2);
                edge.UpdatePointer(away, now + 3000, interactionInProgress: true);
                Assert.False(edge.IsCollapsed);

                // Reverse a running slide from its current position, without teleporting.
                edge.UpdatePointer(away, now + 4000);
                edge.AdvanceAnimation(TimeSpan.FromMilliseconds(80));
                var interruptedTop = window.Top;
                edge.UpdatePointer(new System.Drawing.Point(visible.Left + 50, screen.Bounds.Top), now + 4100);
                Assert.False(edge.IsCollapsed);
                Assert.Equal(interruptedTop, window.Top);
                Finish(edge);
                Assert.Equal(expanded, window.Top, 2);

                // Explicit tray hiding must not reopen on pointer hover.
                edge.UpdatePointer(away, now + 6000);
                edge.AdvanceAnimation(TimeSpan.FromMilliseconds(80));
                Assert.True(edge.IsAnimating);
                window.Hide();
                Assert.False(edge.IsAnimating);
                edge.AdvanceAnimation(TopEdgeAutoHide.AnimationDuration);
                Assert.Equal(expanded, window.Top, 2);
                edge.UpdatePointer(inside, now + 5000);
                Assert.False(window.IsVisible);
                window.Show();
                Assert.Equal(expanded, window.Top, 2);

                edge.BeginMove();
                window.Top = expanded + 80;
                edge.CompleteMove();
                Assert.False(edge.IsDocked);
                edge.UpdatePointer(away, now + 10000);
                Assert.False(edge.IsCollapsed);
                Assert.Equal(window.Top, edge.PlacementTop);
            }
            finally { window.Close(); }
        }
    }

    private static void Finish(TopEdgeAutoHide edge)
    {
        edge.AdvanceAnimation(TopEdgeAutoHide.AnimationDuration);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int GetRgnBox(IntPtr region, out NativeRect rect);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
}
