using System.IO;
using System.Windows;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;

namespace VibeGauge.Windows;

public sealed class SettingsPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new();
    public SettingsPanel()
    {
        Content = body;
        Loaded += (_, _) => { if (body.Children.Count == 0 && DataContext is DashboardViewModel vm) Build(vm); };
    }
    private void Build(DashboardViewModel vm)
    {
        body.Children.Add(InsightUi.Text("Claude Code 连接", true));
        var controls = new Wpf.WrapPanel();
        foreach (var (label, enable, hooks) in new[] { ("连接额度", true, false), ("恢复状态栏", false, false),
            ("开启待处理观察", true, true), ("关闭观察", false, true) })
            controls.Children.Add(InsightUi.Button(label, async (_, _) =>
            {
                try
                {
                    var executable = ProxyManager.FindProxyExecutable() ?? throw new IOException("请使用完整安装包，缺少 VibeGauge.Proxy.exe");
                    if (enable && LocalizedMessageBox.Show(
                        hooks ? "将备份 Claude settings.json 并添加只观察 Hook。不批准请求、不读取提示词、不改其他 Hook。" :
                        "将备份 Claude settings.json 并连接状态栏以读取额度和上下文；可使用“恢复状态栏”还原。",
                        "连接 Claude Code", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                    CliBridge.Configure(vm.Paths, executable, enable, hooks);
                    await vm.RefreshAsync();
                    LocalizedMessageBox.Show("设置已保存。新的 Claude Code 会话将使用更新后的配置。", "VibeGauge");
                }
                catch (Exception error) { LocalizedMessageBox.Show("连接失败：" + error.Message, "VibeGauge"); }
            }));
        body.Children.Add(controls);
        using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\VibeGauge");
        var notifications = new Wpf.CheckBox
        {
            Content = "额度预计耗尽时通知（每周期一次）",
            IsChecked = registry?.GetValue("ForecastNotifications") is int enabled && enabled != 0,
            Style = (Style)System.Windows.Application.Current.FindResource("ToggleStyle"),
            Margin = new Thickness(0, 6, 0, 8)
        };
        notifications.Click += (_, _) =>
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\VibeGauge");
            key.SetValue("ForecastNotifications", notifications.IsChecked == true ? 1 : 0);
        };
        body.Children.Add(notifications);
        body.Children.Add(new FeatureSettingsPanel(vm));
        body.Children.Add(InsightUi.Text("代理连接", true));
        var config = ProxyConfiguration.Load(vm.Paths.LocalDataRoot);
        body.Children.Add(InsightUi.Text("本地端口"));
        var port = new Wpf.TextBox { Text = config.Port.ToString(), MaxLength = 5, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(5) };
        port.Foreground = (Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush");
        port.Background = (Media.Brush)System.Windows.Application.Current.FindResource("CardRaisedBrush");
        port.CaretBrush = port.Foreground;
        body.Children.Add(port);
        body.Children.Add(InsightUi.Text("上游 HTTP 代理（留空跟随系统，direct 为直连）"));
        var route = new Wpf.TextBox { Text = config.Upstream ?? "", Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(5) };
        route.Foreground = port.Foreground;
        route.Background = port.Background;
        route.CaretBrush = port.Foreground;
        body.Children.Add(route);
        body.Children.Add(InsightUi.Button("保存代理设置", async (_, _) =>
        {
            try
            {
                if (!int.TryParse(port.Text, out var number)) throw new ArgumentException("端口必须是 1024–65535 的整数");
                await vm.ConfigureProxyAsync(number, route.Text.Trim());
                LocalizedMessageBox.Show("已保存。请在 API 页重新启动代理；端口改变后需要更新各 CLI 的 BASE_URL。", "VibeGauge");
            }
            catch (Exception error) { LocalizedMessageBox.Show(error.Message, "VibeGauge"); }
        }));
    }
}
