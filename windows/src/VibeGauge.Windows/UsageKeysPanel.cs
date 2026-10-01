using System.Windows;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Wpf = System.Windows.Controls;

namespace VibeGauge.Windows;

public sealed class UsageKeysPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new();
    private readonly Wpf.StackPanel saved = new();
    private CancellationTokenSource? probe;
    public UsageKeysPanel()
    {
        Content = body;
        body.Children.Add(InsightUi.Text("额度监控 · API 密钥", true));
        const string customOption = "自定义 · sub2api";
        var host = new Wpf.ComboBox { Name = "UsageProvider", ItemsSource = UsageKeyVault.Hosts.Append(customOption).ToArray(), SelectedIndex = 0, Margin = new Thickness(0, 3, 0, 8) };
        var providerText = new FrameworkElementFactory(typeof(Wpf.TextBlock));
        providerText.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextPrimaryBrush");
        providerText.SetBinding(Wpf.TextBlock.TextProperty, new System.Windows.Data.Binding("."));
        host.ItemTemplate = new DataTemplate { VisualTree = providerText };
        var secret = new Wpf.PasswordBox
        {
            Name = "UsageSecret",
            MaxLength = 2000,
            Padding = new Thickness(5),
            Margin = new Thickness(0, 0, 0, 6)
        };
        secret.SetResourceReference(Wpf.PasswordBox.ForegroundProperty, "TextPrimaryBrush");
        secret.SetResourceReference(Wpf.PasswordBox.BackgroundProperty, "CardRaisedBrush");
        secret.SetResourceReference(Wpf.PasswordBox.CaretBrushProperty, "TextPrimaryBrush");
        secret.SetResourceReference(Wpf.PasswordBox.BorderBrushProperty, "BorderStrongBrush");
        var custom = new Wpf.StackPanel { Name = "UsageCustomFields", Visibility = Visibility.Collapsed };
        var label = new Wpf.TextBox { Name = "UsageSiteName", MaxLength = 40, Margin = new Thickness(0, 3, 0, 6) };
        var endpoint = new Wpf.TextBox { Name = "UsageEndpoint", MaxLength = 1500, Margin = new Thickness(0, 3, 0, 6) };
        custom.Children.Add(InsightUi.Text("站点名称（可选）"));
        custom.Children.Add(label);
        custom.Children.Add(InsightUi.Text("站点地址"));
        custom.Children.Add(endpoint);
        custom.Children.Add(InsightUi.Text("https://your-site.example 或其 /v1 地址"));
        custom.Children.Add(InsightUi.Text("仅查询 sub2api /v1/usage；密钥只发往所填站点，不跟随跳转。"));
        var status = InsightUi.Text("");
        status.Name = "UsageStatus";
        status.Visibility = Visibility.Collapsed;
        void Feedback(string message, bool error = false)
        {
            status.Text = message;
            status.Visibility = Visibility.Visible;
            status.SetResourceReference(Wpf.TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextSecondaryBrush");
        }
        bool IsFixture() => DataContext is DashboardViewModel vm && vm.Paths.Home != new AppPaths().Home;
        body.Children.Add(host);
        body.Children.Add(custom);
        body.Children.Add(InsightUi.Text("API Key"));
        body.Children.Add(secret);
        var save = InsightUi.Button("保存密钥", async (_, _) =>
        {
            try
            {
                if (IsFixture()) { Feedback("预览模式不保存真实凭据", true); return; }
                if ((string)host.SelectedItem == customOption)
                    UsageKeyVault.SaveCustom(endpoint.Text, secret.Password.Trim(), label.Text);
                else UsageKeyVault.Save((string)host.SelectedItem, secret.Password.Trim());
                secret.Clear();
                Feedback("已保存到 Windows 凭据管理器；在「订阅」查看额度，每 3 分钟更新。");
                Populate();
                if (DataContext is DashboardViewModel vm) { vm.InvalidateOfficial(); await vm.RefreshAsync(); }
            }
            catch (ArgumentException e) { Feedback(e.Message, true); }
            catch (System.ComponentModel.Win32Exception) { Feedback("保存失败，Windows 凭据管理器暂不可用", true); }
        });
        save.Name = "UsageSave";
        var test = InsightUi.Button("测试连接", async (sender, _) =>
        {
            if (IsFixture()) { Feedback("预览模式不发送密钥", true); return; }
            var button = (Wpf.Button)sender;
            using var cancellation = new CancellationTokenSource();
            probe = cancellation;
            try
            {
                var address = Sub2ApiEndpoint.Normalize(endpoint.Text);
                var key = secret.Password.Trim();
                UsageKeyVault.ValidateKey(key);
                host.IsEnabled = custom.IsEnabled = secret.IsEnabled = save.IsEnabled = button.IsEnabled = false;
                Feedback("正在查询 " + new Uri(address).Authority + "…");
                var result = await Sub2ApiClient.Shared.ProbeAsync(address, key, "连接测试", cancellation.Token);
                Feedback((result.DataState == ProviderDataState.Available ? "连接成功（尚未保存）\n" : "") + result.Detail,
                    result.DataState != ProviderDataState.Available);
            }
            catch (ArgumentException e) { Feedback(e.Message, true); }
            finally
            {
                probe = null;
                host.IsEnabled = custom.IsEnabled = secret.IsEnabled = save.IsEnabled = button.IsEnabled = true;
            }
        });
        test.Name = "UsageTest";
        test.Visibility = Visibility.Collapsed;
        endpoint.TextChanged += (_, _) => status.Visibility = Visibility.Collapsed;
        label.TextChanged += (_, _) => status.Visibility = Visibility.Collapsed;
        secret.PasswordChanged += (_, _) => status.Visibility = Visibility.Collapsed;
        host.SelectionChanged += (_, _) =>
        {
            custom.Visibility = test.Visibility = (string)host.SelectedItem == customOption ? Visibility.Visible : Visibility.Collapsed;
            secret.Clear();
            status.Visibility = Visibility.Collapsed;
        };
        var actions = new Wpf.WrapPanel();
        actions.Children.Add(test);
        actions.Children.Add(save);
        body.Children.Add(actions);
        body.Children.Add(status);
        body.Children.Add(saved);
        Loaded += (_, _) => Populate();
        Unloaded += (_, _) => { probe?.Cancel(); secret.Clear(); };
    }
    private void Populate()
    {
        saved.Children.Clear();
        try
        {
            if (DataContext is DashboardViewModel vm && vm.Paths.Home != new VibeGauge.Core.AppPaths().Home) return;
            foreach (var key in UsageKeyVault.List())
            {
                var row = new Wpf.DockPanel();
                var remove = InsightUi.Button("×", async (_, _) =>
                {
                    try
                    {
                        UsageKeyVault.Remove(key); Populate();
                        if (DataContext is DashboardViewModel viewModel) { viewModel.InvalidateOfficial(); await viewModel.RefreshAsync(); }
                    }
                    catch (Exception e) { LocalizedMessageBox.Show("删除失败：" + e.GetType().Name, "VibeGauge"); }
                });
                remove.ToolTip = "删除已登记密钥";
                Wpf.DockPanel.SetDock(remove, Wpf.Dock.Right); row.Children.Add(remove);
                var text = InsightUi.Text(key.DisplayName + " · " + key.Fingerprint + (key.IsCustom ? "\n" + key.Host + " · sub2api" : ""));
                text.ToolTip = key.Host;
                row.Children.Add(text); saved.Children.Add(row);
            }
        }
        catch (System.ComponentModel.Win32Exception) { saved.Children.Add(InsightUi.Text("Windows 凭据管理器暂不可用")); }
    }
}
