using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;

namespace VibeGauge.Windows;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel viewModel;
    private bool allowClose;
    private bool moving;
    private bool pinned;
    private string themeMode;
    private string backdropMode;
    private readonly WindowPlacementStore placement;
    private readonly ThemeSettings themeSettings;
    private readonly ThemePalette palette;
    private readonly TopEdgeAutoHide edgeHide;
    private readonly System.Windows.Threading.DispatcherTimer transparencySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly System.Windows.Threading.DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2000) };
    private (bool Light, int Value)? pendingTransparency;
    private bool syncingAppearance = true;
    private VibeGauge.Core.DashboardSnapshot? latestSnapshot;
    public bool IsLightTheme => palette.IsLight;
    public string CurrentThemeMode => themeMode;
    public string CurrentBackdropMode => backdropMode;
    public bool HasUserPosition { get; private set; }
    public bool UsesSystemBackdrop { get; private set; }

    public MainWindow(DashboardViewModel viewModel, bool autoHide = true)
    {
        themeSettings = new ThemeSettings(viewModel.Paths.LocalDataRoot);
        themeMode = themeSettings.ThemeMode;
        backdropMode = themeSettings.BackdropMode;
        palette = (ThemePalette)System.Windows.Application.Current.FindResource("ThemePalette");
        palette.IsLight = ThemeSettings.ResolveIsLight(themeMode);
        palette.SetTransparency(true, themeSettings.GetTransparency(true));
        palette.SetTransparency(false, themeSettings.GetTransparency(false));
        InitializeComponent();
        transparencySaveTimer.Tick += (_, _) => SaveTransparency();
        toastTimer.Tick += (_, _) => HideToast();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        UpdateTheme();
        DataContext = this.viewModel = viewModel;
        placement = new WindowPlacementStore(viewModel.Paths.LocalDataRoot);
        if (placement.Load() is { } previous)
        {
            Left = previous.Left; Top = previous.Top; Width = previous.Width; Height = previous.Height;
            pinned = previous.Pinned; PinButton.IsChecked = pinned; HasUserPosition = true;
        }
        SizeChanged += (_, _) => HeaderMemory.Visibility = ActualWidth >= 520 && viewModel.IsOverview ? Visibility.Visible : Visibility.Collapsed;
        PlansView.ProviderSelected += name => viewModel.OpenProviderDetail(name);
        edgeHide = new TopEdgeAutoHide(this, autoHide);
        viewModel.SnapshotChanged += (_, snapshot) =>
        {
            latestSnapshot = snapshot;
            UpdateSelectedPanel();
        };
        viewModel.LightweightChanged += (_, adapters) =>
        {
            if (IsVisible && viewModel.IsNetworkSelected) NetworkView.Update(adapters, null);
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SelectedProviderName))
            {
                HeaderMemory.Visibility = ActualWidth >= 520 && viewModel.IsOverview ? Visibility.Visible : Visibility.Collapsed;
                UpdateSelectedPanel();
                AnimateViewTransition(viewModel.IsProviderDetailOpen ? ProviderDetailView : ContentHost);
            }
            if (e.PropertyName == nameof(viewModel.IsStatsSelected) && viewModel.IsStatsSelected ||
                e.PropertyName == nameof(viewModel.IsApiSelected) && viewModel.IsApiSelected ||
                e.PropertyName == nameof(viewModel.SelectedTokenUnit) ||
                e.PropertyName == nameof(viewModel.IsSubscriptionSelected) && viewModel.IsSubscriptionSelected ||
                e.PropertyName == nameof(viewModel.IsNetworkSelected) && viewModel.IsNetworkSelected)
            {
                UpdateSelectedPanel();
                if (e.PropertyName != nameof(viewModel.SelectedTokenUnit)) AnimateViewTransition(ContentHost);
            }
        };
        IsVisibleChanged += (_, _) => { viewModel.SetWindowVisible(IsVisible); if (IsVisible) UpdateSelectedPanel(); };
        if (autoHide) Deactivated += (_, _) =>
        {
            if (!pinned && !moving && !edgeHide.IsDocked && !IsMouseOver) Hide();
        };
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || themeMode != "system") return;
        Dispatcher.BeginInvoke(() =>
        {
            var next = ThemeSettings.IsSystemLightTheme();
            if (palette.IsLight == next) return;
            palette.IsLight = next;
            UpdateTheme();
        });
    }

    private void AnimateViewTransition(UIElement target)
    {
        if (!IsActive || !SystemParameters.ClientAreaAnimation || !target.IsVisible) return;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0.84, 1, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    internal static void NotifyToast(string message)
    {
        var main = System.Windows.Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
        main?.ShowToast(message);
    }

    internal void ShowToast(string message)
    {
        toastTimer.Stop();
        ToastMessage.Text = UiLocalization.Text(message);
        ToastBanner.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            ToastBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else ToastBanner.Opacity = 1;
        toastTimer.Start();
    }

    private void HideToast()
    {
        toastTimer.Stop();
        if (!SystemParameters.ClientAreaAnimation)
        {
            ToastBanner.Opacity = 0;
            ToastBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(ToastBanner.Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { if (ToastBanner.Opacity <= 0.01) ToastBanner.Visibility = Visibility.Collapsed; };
        ToastBanner.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateSelectedPanel()
    {
        // Collection and tray monitoring continue while hidden; rebuild the
        // selected detail view from the latest snapshot when the window reopens.
        if (!IsVisible) return;
        if ((viewModel.CurrentSnapshot ?? latestSnapshot) is not { } snapshot) return;
        if (viewModel.SelectedProviderName is { } selected)
        {
            var provider = snapshot.Platforms.FirstOrDefault(x => x.Name == selected);
            if (provider is not null) ProviderDetailView.Update(provider, snapshot, viewModel.Paths, viewModel.SelectedTokenUnit);
            return;
        }
        if (viewModel.IsApiSelected) RemoteApiView.Update(snapshot.Platforms, viewModel.SelectedTokenUnit);
        else if (viewModel.IsStatsSelected) StatisticsView.Update(snapshot.Statistics, viewModel.SelectedTokenUnit);
        else if (viewModel.IsSubscriptionSelected) PlansView.Update(viewModel.VisibleSessions(snapshot.Sessions), viewModel.SelectedTokenUnit, snapshot.Usage.Projects);
        else if (viewModel.IsNetworkSelected) NetworkView.Update(snapshot.Network, snapshot.Diagnostics);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTheme();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
    }

    internal void ToggleThemeQuick()
    {
        SaveTransparency();
        palette.IsLight = !palette.IsLight;
        themeMode = palette.IsLight ? "light" : "dark";
        themeSettings.Save(palette.IsLight);
        UpdateTheme();
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => ToggleThemeQuick();

    private void ThemeMode_Click(object sender, RoutedEventArgs e)
    {
        if (syncingAppearance || sender is not FrameworkElement { Tag: string mode }) return;
        SaveTransparency();
        themeMode = mode;
        themeSettings.SaveThemeMode(mode);
        palette.IsLight = ThemeSettings.ResolveIsLight(mode);
        UpdateTheme();
    }

    private void BackdropMode_Click(object sender, RoutedEventArgs e)
    {
        if (syncingAppearance || sender is not FrameworkElement { Tag: string mode }) return;
        SaveTransparency();
        backdropMode = mode;
        themeSettings.SaveBackdropMode(mode);
        UpdateTheme();
    }

    internal void SetThemeForCapture(bool light)
    {
        themeMode = light ? "light" : "dark";
        palette.IsLight = light;
        UpdateTheme();
    }

    private void UpdateTheme()
    {
        UsesSystemBackdrop = WindowAppearance.Apply(this, palette.IsLight, backdropMode);
        WindowSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            UsesSystemBackdrop ? "WindowTintBrush" : "WindowBrush");
        WindowSurface.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
            UsesSystemBackdrop ? "WindowRimBrush" : "BorderStrongBrush");
        syncingAppearance = true;
        ThemeSystemRadio.IsChecked = themeMode == "system";
        ThemeDarkRadio.IsChecked = themeMode == "dark";
        ThemeLightRadio.IsChecked = themeMode == "light";
        BackdropAcrylicRadio.IsChecked = backdropMode == "acrylic";
        BackdropMicaRadio.IsChecked = backdropMode == "mica";
        BackdropSolidRadio.IsChecked = backdropMode == "solid";
        TransparencySlider.Value = palette.GetTransparency(palette.IsLight);
        TransparencySlider.IsEnabled = UsesSystemBackdrop;
        TransparencyValue.Text = $"{TransparencySlider.Value:0}%";
        syncingAppearance = false;
        ThemeIcon.Text = palette.IsLight ? "\uE708" : "\uE706";
        ThemeButton.ToolTip = palette.IsLight ? "切换为深色毛玻璃" : "切换为浅色毛玻璃";
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, (string)ThemeButton.ToolTip);
        StatisticsView.RefreshTheme();
        if (DataContext is DashboardViewModel { IsProviderDetailOpen: true }) UpdateSelectedPanel();
    }

    private void Transparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (syncingAppearance) return;
        var value = (int)Math.Round(e.NewValue);
        palette.SetTransparency(palette.IsLight, value);
        TransparencyValue.Text = $"{value}%";
        pendingTransparency = (palette.IsLight, value);
        transparencySaveTimer.Stop();
        transparencySaveTimer.Start();
    }

    private void SaveTransparency()
    {
        transparencySaveTimer.Stop();
        if (pendingTransparency is not { } pending) return;
        themeSettings.SaveTransparency(pending.Light, pending.Value);
        pendingTransparency = null;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0086 && UsesSystemBackdrop && !SystemParameters.HighContrast && WindowState != WindowState.Minimized) // WM_NCACTIVATE: material appearance only
        {
            handled = true;
            return WindowAppearance.KeepBackdropActive(hwnd);
        }
        if (message == 0x0231) { moving = true; edgeHide.BeginMove(); } // WM_ENTERSIZEMOVE
        if (message == 0x0232)
        {
            moving = false;
            HasUserPosition = true;
            edgeHide.CompleteMove();
            SavePlacement();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveTransparency();
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        toastTimer.Stop();
        viewModel.Dispose();
        base.OnClosing(e);
    }

    public void ExitApplication()
    {
        allowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        pinned = PinButton.IsChecked == true;
        PinButton.ToolTip = pinned ? "取消固定，点击外部收起" : "固定窗口，点击外部不收起";
        HasUserPosition = true;
        SavePlacement();
    }

    private void SavePlacement() => placement.Save(new WindowPlacement(Left, edgeHide.PlacementTop, ActualWidth, ActualHeight, pinned));

    internal void PrepareForTrayShow() => edgeHide.Reveal();
    internal void OnTrayShown() { if (HasUserPosition) edgeHide.CompleteMove(); }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        viewModel.InvalidateOfficial();
        await viewModel.RefreshAsync();
    }

    private async void ToggleProxy_Click(object sender, RoutedEventArgs e) => await viewModel.ToggleProxyAsync();

    private void CopyProxyPrefix_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(viewModel.ProxyEndpoint);
        ShowToast("已复制代理前缀");
    }

    private void ApiRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string range }) viewModel.SelectApiRange(range);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string raw } && int.TryParse(raw, out var index))
            viewModel.SelectTab(index);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (viewModel.IsProviderDetailOpen) viewModel.CloseProviderDetail(); else Hide();
        e.Handled = true;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.OrphanCount == 0) return;
        var answer = LocalizedMessageBox.Show(
            $"将终止 {viewModel.OrphanCount} 个父进程已经退出、未监听端口且未被 Windows 服务或启动项托管的 MCP 进程。\n\n操作前会再次核对 PID、创建时间和命令指纹。",
            "清理断链 MCP", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        var result = await viewModel.CleanAsync();
        LocalizedMessageBox.Show($"已终止 {result.Killed} 个进程，释放约 {result.FreedMemoryMb:0} MB；跳过 {result.Skipped} 个。", "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
    private void ProviderBack_Click(object sender, RoutedEventArgs e) => viewModel.CloseProviderDetail();
}
