using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;

namespace VibeGauge.Windows;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel viewModel;
    private bool allowClose;
    private bool moving;
    private bool pinned;
    private readonly WindowPlacementStore placement;
    private readonly ThemeSettings themeSettings;
    private readonly ThemePalette palette;
    private readonly TopEdgeAutoHide edgeHide;
    private VibeGauge.Core.DashboardSnapshot? latestSnapshot;
    public bool IsLightTheme => palette.IsLight;
    public bool HasUserPosition { get; private set; }
    public bool UsesSystemBackdrop { get; private set; }

    public MainWindow(DashboardViewModel viewModel, bool autoHide = true)
    {
        themeSettings = new ThemeSettings(viewModel.Paths.LocalDataRoot);
        palette = (ThemePalette)System.Windows.Application.Current.FindResource("ThemePalette");
        palette.IsLight = themeSettings.IsLight;
        InitializeComponent();
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
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SelectedProviderName))
            {
                HeaderMemory.Visibility = ActualWidth >= 520 && viewModel.IsOverview ? Visibility.Visible : Visibility.Collapsed;
                UpdateSelectedPanel();
            }
            if (e.PropertyName == nameof(viewModel.IsStatsSelected) && viewModel.IsStatsSelected ||
                e.PropertyName == nameof(viewModel.IsSubscriptionSelected) && viewModel.IsSubscriptionSelected ||
                e.PropertyName == nameof(viewModel.IsNetworkSelected) && viewModel.IsNetworkSelected)
                UpdateSelectedPanel();
        };
        if (autoHide) Deactivated += (_, _) =>
        {
            if (!pinned && !moving && !edgeHide.IsDocked && !IsMouseOver) Hide();
        };
    }

    private void UpdateSelectedPanel()
    {
        if ((viewModel.CurrentSnapshot ?? latestSnapshot) is not { } snapshot) return;
        if (viewModel.SelectedProviderName is { } selected)
        {
            var provider = snapshot.Platforms.FirstOrDefault(x => x.Name == selected);
            if (provider is not null) ProviderDetailView.Update(provider, snapshot, viewModel.Paths, viewModel.SelectedTokenUnit);
            return;
        }
        if (viewModel.IsStatsSelected) StatisticsView.Update(snapshot.Statistics, viewModel.SelectedTokenUnit);
        else if (viewModel.IsSubscriptionSelected) PlansView.Update(viewModel.VisibleSessions(snapshot.Sessions), viewModel.SelectedTokenUnit);
        else if (viewModel.IsNetworkSelected) NetworkView.Update(snapshot.Network, snapshot.Diagnostics);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTheme();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        palette.IsLight = !palette.IsLight;
        themeSettings.Save(palette.IsLight);
        UpdateTheme();
    }

    internal void SetThemeForCapture(bool light)
    {
        palette.IsLight = light;
        UpdateTheme();
    }

    private void UpdateTheme()
    {
        UsesSystemBackdrop = WindowAppearance.Apply(this, palette.IsLight);
        WindowSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            UsesSystemBackdrop ? "WindowTintBrush" : "WindowBrush");
        ThemeIcon.Text = palette.IsLight ? "\uE708" : "\uE706";
        ThemeButton.ToolTip = palette.IsLight ? "切换为深色毛玻璃" : "切换为浅色毛玻璃";
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, (string)ThemeButton.ToolTip);
        StatisticsView.RefreshTheme();
        if (DataContext is DashboardViewModel { IsProviderDetailOpen: true }) UpdateSelectedPanel();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await viewModel.RefreshAsync();

    private async void ToggleProxy_Click(object sender, RoutedEventArgs e) => await viewModel.ToggleProxyAsync();

    private void CopyProxyPrefix_Click(object sender, RoutedEventArgs e) =>
        System.Windows.Clipboard.SetText(viewModel.ProxyEndpoint);

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
