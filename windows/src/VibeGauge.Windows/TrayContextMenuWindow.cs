using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VibeGauge.Windows.Services;
using Wpf = System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace VibeGauge.Windows;

internal sealed class TrayContextMenuWindow : Window
{
    private readonly Wpf.Border surface;
    private bool appliedBackdrop;

    internal TrayContextMenuWindow(
        string summaryText,
        bool isLightTheme,
        string backdropMode,
        Action openAction,
        Action refreshAction,
        Action toggleThemeAction,
        Action exitAction)
    {
        Title = "VibeGauge Menu";
        Width = 216;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Topmost = true;
        Background = System.Windows.Media.Brushes.Transparent;
        AllowsTransparency = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var body = new Wpf.StackPanel { Margin = new Thickness(6) };

        // Compact status header
        var header = new Wpf.DockPanel { Margin = new Thickness(8, 5, 8, 6) };
        var title = new Wpf.TextBlock
        {
            Text = "VibeGauge",
            FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFontFamily"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var badge = new Wpf.TextBlock
        {
            Text = summaryText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(badge, FontNumeralAlignment.Tabular);
        Wpf.DockPanel.SetDock(badge, Wpf.Dock.Right);
        header.Children.Add(badge);
        header.Children.Add(title);
        body.Children.Add(header);
        body.Children.Add(Separator());

        body.Children.Add(MenuItem("\uE8A7", UiLocalization.Text("打开 VibeGauge"), openAction));
        body.Children.Add(MenuItem("\uE72C", UiLocalization.Text("立即刷新"), refreshAction));
        body.Children.Add(Separator());
        body.Children.Add(MenuItem(
            isLightTheme ? "\uE708" : "\uE706",
            UiLocalization.Text(isLightTheme ? "切换为深色毛玻璃" : "切换为浅色毛玻璃"),
            toggleThemeAction));
        body.Children.Add(Separator());
        body.Children.Add(MenuItem("\uE7E8", UiLocalization.Text("退出"), exitAction, danger: true));

        surface = new Wpf.Border
        {
            Child = body,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        surface.SetResourceReference(Wpf.Border.BackgroundProperty, "PopupBrush");
        surface.SetResourceReference(Wpf.Border.BorderBrushProperty, "BorderStrongBrush");
        Content = surface;

        SourceInitialized += (_, _) =>
        {
            appliedBackdrop = WindowAppearance.Apply(this, isLightTheme, backdropMode);
            surface.SetResourceReference(Wpf.Border.BackgroundProperty, appliedBackdrop ? "WindowTintBrush" : "PopupBrush");
            surface.SetResourceReference(Wpf.Border.BorderBrushProperty, appliedBackdrop ? "WindowRimBrush" : "BorderStrongBrush");
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (msg == 0x0086 && appliedBackdrop && !SystemParameters.HighContrast)
                {
                    handled = true;
                    return WindowAppearance.KeepBackdropActive(hwnd);
                }
                return IntPtr.Zero;
            });
        };

        Deactivated += (_, _) => CloseSafely();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseSafely(); };
    }

    internal void ShowAtCursor()
    {
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        Show();
        UpdateLayout();
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        var scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        var targetLeft = cursor.X * scaleX - ActualWidth * 0.5;
        var targetTop = cursor.Y * scaleY - ActualHeight - 10;
        if (targetTop < area.Top * scaleY + 8) targetTop = cursor.Y * scaleY + 10;
        Left = Math.Clamp(targetLeft, area.Left * scaleX + 8, Math.Max(area.Left * scaleX + 8, area.Right * scaleX - ActualWidth - 8));
        Top = Math.Clamp(targetTop, area.Top * scaleY + 8, Math.Max(area.Top * scaleY + 8, area.Bottom * scaleY - ActualHeight - 8));
        Activate();
    }

    private Wpf.Button MenuItem(string glyph, string label, Action action, bool danger = false)
    {
        var content = new Wpf.DockPanel { LastChildFill = true };
        var icon = new Wpf.TextBlock
        {
            Text = glyph,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFontFamily"),
            FontSize = 13,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        icon.SetResourceReference(Wpf.TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextSecondaryBrush");
        Wpf.DockPanel.SetDock(icon, Wpf.Dock.Left);
        var text = new Wpf.TextBlock
        {
            Text = label,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(Wpf.TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextPrimaryBrush");
        content.Children.Add(icon);
        content.Children.Add(text);

        var button = new Wpf.Button
        {
            Content = content,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(0, 1, 0, 1),
            Style = (Style)FindResource("SettingsNavigationStyle")
        };
        button.Click += (_, _) =>
        {
            CloseSafely();
            action();
        };
        return button;
    }

    private static Wpf.Border Separator()
    {
        var line = new Wpf.Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        line.SetResourceReference(Wpf.Border.BackgroundProperty, "BorderBrush");
        return line;
    }

    private void CloseSafely()
    {
        try { if (IsVisible) Close(); } catch { }
    }
}
