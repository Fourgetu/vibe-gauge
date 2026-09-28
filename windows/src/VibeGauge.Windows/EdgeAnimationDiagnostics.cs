using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VibeGauge.Windows.Services;
using Forms = System.Windows.Forms;

namespace VibeGauge.Windows;

internal static class EdgeAnimationDiagnostics
{
    // Capture fixtures only. Observe our own HWND without injecting global mouse input.
    internal static async Task<object> VerifyAsync(MainWindow window)
    {
        var oldTop = window.Top;
        using var edge = new TopEdgeAutoHide(window, pollPointer: false);
        var steps = new List<double>();
        var clock = new Stopwatch();
        var reports = new List<object>();
        void Moved(object? sender, EventArgs e) { if (clock.IsRunning) steps.Add(clock.Elapsed.TotalMilliseconds); }
        window.LocationChanged += Moved;
        try
        {
            var screen = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);
            var dpi = VisualTreeHelper.GetDpi(window);
            var origin = window.PointToScreen(new System.Windows.Point(0, 0));
            window.Top += (screen.WorkingArea.Top - origin.Y) / dpi.DpiScaleY;
            edge.CompleteMove();
            if (!edge.IsDocked) throw new InvalidOperationException("Animation fixture did not dock");
            await Task.Delay(150);
            var expandedTop = window.Top;
            var x = (int)window.PointToScreen(new System.Windows.Point(30, 0)).X;

            async Task Measure(string direction, Action trigger)
            {
                steps.Clear();
                clock.Restart();
                trigger();
                while (edge.IsAnimating && clock.ElapsedMilliseconds < 2000) await Task.Delay(10);
                clock.Stop();
                if (edge.IsAnimating || steps.Count < 2)
                    throw new InvalidOperationException("Render-driven edge animation did not complete");
                var gaps = steps.Zip(steps.Skip(1), (a, b) => b - a).Order().ToArray();
                reports.Add(new
                {
                    Direction = direction,
                    PositionUpdates = steps.Count,
                    DurationMs = Math.Round(steps.Last(), 2),
                    MedianStepMs = Math.Round(gaps[gaps.Length / 2], 2),
                    MaximumStepMs = Math.Round(gaps.Last(), 2)
                });
            }

            for (var run = 0; run < 3; run++)
            {
                await Measure("hide", () =>
                {
                    var now = Environment.TickCount64;
                    edge.UpdatePointer(new System.Drawing.Point(x, screen.WorkingArea.Top + 30), now);
                    edge.UpdatePointer(new System.Drawing.Point(screen.Bounds.Right + 100, screen.Bounds.Bottom + 100), now + 701);
                });
                var bottom = window.PointToScreen(new System.Windows.Point(0, window.ActualHeight)).Y;
                if (Math.Abs(bottom - screen.Bounds.Top - 2) > 2)
                    throw new InvalidOperationException("Collapsed activation strip changed size");
                await Measure("reveal", () => edge.UpdatePointer(new System.Drawing.Point(x, screen.Bounds.Top), Environment.TickCount64));
                if (Math.Abs(window.Top - expandedTop) > 1)
                    throw new InvalidOperationException("Reveal did not restore the expanded position");
                await Task.Delay(80);
            }
            return new { TargetDurationMs = TopEdgeAutoHide.AnimationDuration.TotalMilliseconds, CompletedCycles = 3, Runs = reports };
        }
        finally
        {
            clock.Stop();
            window.LocationChanged -= Moved;
            edge.Reveal();
            window.Top = oldTop;
        }
    }
}
