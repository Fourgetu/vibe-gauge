using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VibeGauge.Windows.Services;

internal sealed class TopEdgeAutoHide : IDisposable
{
    private readonly Window window;
    private readonly DispatcherTimer timer;
    private readonly bool enabled;
    private readonly bool pollPointer;
    private System.Drawing.Rectangle screen;
    private double expandedTop;
    private double hiddenTop;
    private double targetTop;
    private double animationStartTop;
    private long animationStarted;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;
    private TimeSpan lastFrameElapsed;
    private double animationPixelScale;
    private bool renderingSubscribed;
    private bool clipDuringAnimation;
    private (int Top, int Width, int Height)? clipBounds;
    private long lastInside;
    private bool moving;
    private bool animating;
    private bool clipped;
    internal bool IsDocked { get; private set; }
    internal bool IsCollapsed { get; private set; }
    internal bool IsAnimating => animating;
    internal static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(240);
    internal double PlacementTop => IsDocked ? expandedTop : window.Top;
    private IntPtr Handle => new WindowInteropHelper(window).Handle;

    internal TopEdgeAutoHide(Window window, bool enabled = true, bool pollPointer = true)
    {
        this.window = window;
        this.enabled = enabled;
        this.pollPointer = pollPointer;
        // Pointer polling is independent of animation rendering and stays cheap while idle.
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += Tick;
        window.IsVisibleChanged += VisibilityChanged;
        window.Closed += Closed;
    }

    internal void BeginMove()
    {
        Reveal();
        moving = true;
        IsDocked = false;
        timer.Stop();
    }

    internal void CompleteMove()
    {
        StopAnimation();
        moving = false;
        if (!enabled || Handle == IntPtr.Zero || window.WindowState != WindowState.Normal) return;
        var monitor = Forms.Screen.FromHandle(Handle);
        if (!GetWindowRect(Handle, out var rect)) return;
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleY;
        IsDocked = Math.Abs(rect.Top - monitor.WorkingArea.Top) <= 12 * scale ||
            Math.Abs(rect.Top - monitor.Bounds.Top) <= 12 * scale;
        if (!IsDocked) { timer.Stop(); return; }
        screen = monitor.Bounds;
        clipDuringAnimation = NeedsAnimationClip(screen, rect.Left, rect.Right, rect.Bottom - rect.Top,
            Forms.Screen.AllScreens.Select(x => x.Bounds));
        expandedTop = window.Top + (monitor.WorkingArea.Top - rect.Top) / scale;
        window.Top = expandedTop;
        // Keep only a two-pixel activation strip, regardless of display scaling.
        hiddenTop = expandedTop + (screen.Top + 2 - (monitor.WorkingArea.Top + rect.Bottom - rect.Top)) / scale;
        targetTop = expandedTop;
        IsCollapsed = animating = false;
        lastInside = Environment.TickCount64;
        if (window.IsVisible && pollPointer) timer.Start();
    }

    internal void Reveal()
    {
        StopAnimation();
        IsCollapsed = false;
        if (IsDocked) window.Top = targetTop = expandedTop;
        ClearClip();
        lastInside = Environment.TickCount64;
    }

    internal void UpdatePointer(System.Drawing.Point cursor, long now, bool interactionInProgress = false)
    {
        if (!IsDocked || moving || !window.IsVisible || !GetWindowRect(Handle, out var bounds)) return;
        if (window.WindowState != WindowState.Normal) { Reveal(); IsDocked = false; timer.Stop(); return; }
        var inWindow = cursor.X >= bounds.Left && cursor.X < bounds.Right &&
            cursor.Y >= Math.Max(screen.Top, bounds.Top) && cursor.Y < bounds.Bottom;
        var atEdge = cursor.X >= bounds.Left && cursor.X < bounds.Right &&
            cursor.Y >= screen.Top && cursor.Y < screen.Top + 4;
        if (IsCollapsed)
        {
            if (atEdge || (!animating && inWindow)) SetCollapsed(false);
            return;
        }
        if (inWindow || interactionInProgress) lastInside = now;
        else if (now - lastInside >= 700) SetCollapsed(true);
    }

    private void SetCollapsed(bool value)
    {
        StopAnimation();
        IsCollapsed = value;
        animationStartTop = window.Top;
        targetTop = value ? hiddenTop : expandedTop;
        animationStarted = Stopwatch.GetTimestamp();
        lastRenderingTime = TimeSpan.MinValue;
        lastFrameElapsed = TimeSpan.FromMilliseconds(-8);
        animationPixelScale = VisualTreeHelper.GetDpi(window).DpiScaleY;
        animating = true;
        lastInside = Environment.TickCount64;
        if (!clipDuringAnimation) ClearClip();
        CompositionTarget.Rendering += RenderFrame;
        renderingSubscribed = true;
    }

    private void Tick(object? sender, EventArgs e)
    {
        var editing = window.IsActive && Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        UpdatePointer(Forms.Cursor.Position, Environment.TickCount64,
            !window.IsEnabled || !IsWindowEnabled(Handle) || Mouse.Captured is not null || Mouse.LeftButton == MouseButtonState.Pressed || editing);
        // Occluded windows can stop receiving render callbacks. Never leave a slide stuck.
        if (animating && Stopwatch.GetElapsedTime(animationStarted) > AnimationDuration + TimeSpan.FromMilliseconds(160))
            AdvanceAnimation(AnimationDuration);
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || frame.RenderingTime == lastRenderingTime) return;
        lastRenderingTime = frame.RenderingTime;
        var elapsed = Stopwatch.GetElapsedTime(animationStarted);
        // Moving an HWND can request extra WPF renders between display frames.
        if (elapsed - lastFrameElapsed < TimeSpan.FromMilliseconds(8) && elapsed < AnimationDuration) return;
        lastFrameElapsed = elapsed;
        AdvanceAnimation(elapsed);
    }

    internal static double Interpolate(double from, double to, TimeSpan elapsed)
    {
        var progress = Math.Clamp(elapsed.TotalMilliseconds / AnimationDuration.TotalMilliseconds, 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        return from + (to - from) * eased;
    }

    internal static bool NeedsAnimationClip(System.Drawing.Rectangle monitor, int left, int right, int height,
        IEnumerable<System.Drawing.Rectangle> monitors) =>
        monitors.Any(other => other != monitor && other.IntersectsWith(new System.Drawing.Rectangle(left, monitor.Top - height, right - left, height)));

    internal void AdvanceAnimation(TimeSpan elapsed)
    {
        if (!animating) return;
        var next = elapsed >= AnimationDuration ? targetTop : animationStartTop +
            Math.Round((Interpolate(animationStartTop, targetTop, elapsed) - animationStartTop) * animationPixelScale) / animationPixelScale;
        if (window.Top != next) window.Top = next;
        if (elapsed >= AnimationDuration) StopAnimation();
        // A normal top edge is already clipped by the desktop. Only change its native
        // region at the endpoint; per-frame clipping is needed for a monitor above us.
        if ((clipDuringAnimation || !animating) && GetWindowRect(Handle, out var rect) && rect.Top < screen.Top)
        {
            var bounds = (Top: screen.Top - rect.Top, Width: rect.Right - rect.Left, Height: rect.Bottom - rect.Top);
            if (clipBounds == bounds) return;
            var region = CreateRectRgn(0, bounds.Top, bounds.Width, bounds.Height);
            if (region != IntPtr.Zero)
            {
                if (SetWindowRgn(Handle, region, true) == 0) DeleteObject(region);
                else { clipped = true; clipBounds = bounds; }
            }
        }
        else ClearClip();
    }

    private void StopAnimation()
    {
        animating = false;
        if (!renderingSubscribed) return;
        CompositionTarget.Rendering -= RenderFrame;
        renderingSubscribed = false;
    }

    private void ClearClip()
    {
        if (!clipped || Handle == IntPtr.Zero) return;
        SetWindowRgn(Handle, IntPtr.Zero, true);
        clipped = false;
        clipBounds = null;
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!window.IsVisible) { timer.Stop(); Reveal(); }
        else if (IsDocked) { Reveal(); if (pollPointer) timer.Start(); }
    }

    private void Closed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        timer.Stop();
        StopAnimation();
        timer.Tick -= Tick;
        window.IsVisibleChanged -= VisibilityChanged;
        window.Closed -= Closed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
}
