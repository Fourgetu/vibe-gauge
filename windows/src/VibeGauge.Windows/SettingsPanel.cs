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
        body.Children.Add(new FeatureSettingsPanel(vm));
    }
    internal static void BuildProxy(DashboardViewModel vm, Wpf.Panel body)
    {
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
