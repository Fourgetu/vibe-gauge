using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VibeGauge.Windows.Services;
using Wpf = System.Windows.Controls;

namespace VibeGauge.Windows;

internal sealed class GlassDialog : Window
{
    private MessageBoxResult result = MessageBoxResult.None;
    private readonly Wpf.Border surface;

    private GlassDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, bool light, string backdropMode)
    {
        Title = caption;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Topmost = true;
        Background = System.Windows.Media.Brushes.Transparent;
        AllowsTransparency = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var owner = System.Windows.Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var root = new Wpf.StackPanel { Margin = new Thickness(18, 16, 18, 16) };

        var header = new Wpf.DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (image != MessageBoxImage.None)
        {
            var (glyph, brushKey) = image switch
            {
                MessageBoxImage.Warning => ("\uE7BA", "WarningBrush"),
                MessageBoxImage.Error => ("\uEA39", "DangerBrush"),
                MessageBoxImage.Question => ("\uE897", "AccentBrush"),
                _ => ("\uE946", "AccentBrush")
            };
            var icon = new Wpf.TextBlock
            {
                Text = glyph,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFontFamily"),
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            icon.SetResourceReference(Wpf.TextBlock.ForegroundProperty, brushKey);
            Wpf.DockPanel.SetDock(icon, Wpf.Dock.Left);
            header.Children.Add(icon);
        }

        var titleBlock = new Wpf.TextBlock
        {
            Text = caption,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFontFamily"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBlock.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextPrimaryBrush");
        header.Children.Add(titleBlock);
        root.Children.Add(header);

        var body = new Wpf.TextBlock
        {
            Text = text,
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };
        body.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextSecondaryBrush");
        root.Children.Add(body);

        var footer = new Wpf.StackPanel
        {
            Orientation = Wpf.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        void AddButton(string label, MessageBoxResult value, string styleKey, bool isDefault = false, bool isCancel = false)
        {
            var button = new Wpf.Button
            {
                Content = UiLocalization.Text(label),
                MinWidth = 76,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel,
                Style = (Style)FindResource(styleKey)
            };
            button.Click += (_, _) => { result = value; DialogResult = value is MessageBoxResult.OK or MessageBoxResult.Yes; Close(); };
            footer.Children.Add(button);
        }

        var confirmStyle = image is MessageBoxImage.Warning or MessageBoxImage.Error ? "DangerButtonStyle" : "PrimaryButtonStyle";
        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                AddButton("取消", MessageBoxResult.Cancel, "ActionButtonStyle", isCancel: true);
                AddButton("确定", MessageBoxResult.OK, confirmStyle, isDefault: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("否", MessageBoxResult.No, "ActionButtonStyle", isCancel: true);
                AddButton("是", MessageBoxResult.Yes, confirmStyle, isDefault: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("取消", MessageBoxResult.Cancel, "ActionButtonStyle", isCancel: true);
                AddButton("否", MessageBoxResult.No, "ActionButtonStyle");
                AddButton("是", MessageBoxResult.Yes, confirmStyle, isDefault: true);
                break;
            default:
                AddButton("确定", MessageBoxResult.OK, "PrimaryButtonStyle", isDefault: true, isCancel: true);
                break;
        }
        root.Children.Add(footer);

        surface = new Wpf.Border
        {
            Child = root,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        surface.SetResourceReference(Wpf.Border.BackgroundProperty, "WindowBrush");
        surface.SetResourceReference(Wpf.Border.BorderBrushProperty, "BorderStrongBrush");
        Content = surface;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var appliedBackdrop = false;
        SourceInitialized += (_, _) =>
        {
            appliedBackdrop = WindowAppearance.Apply(this, light, backdropMode);
            surface.SetResourceReference(Wpf.Border.BackgroundProperty, appliedBackdrop ? "WindowTintBrush" : "WindowBrush");
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
    }

    internal static MessageBoxResult ShowDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted)
            return System.Windows.MessageBox.Show(text, caption, buttons, image);

        var palette = app.TryFindResource("ThemePalette") as ThemePalette;
        var light = palette?.IsLight ?? false;
        var main = app.Windows.OfType<MainWindow>().FirstOrDefault();
        var backdrop = main?.CurrentBackdropMode ?? "acrylic";
        var dialog = new GlassDialog(text, caption, buttons, image, light, backdrop);
        dialog.ShowDialog();
        return dialog.result != MessageBoxResult.None
            ? dialog.result
            : buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
    }
}
