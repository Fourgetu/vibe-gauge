using System.Diagnostics;
using System.IO;
using System.Windows;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Wpf = System.Windows.Controls;

namespace VibeGauge.Windows;

public sealed class FeatureSettingsPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new();
    private readonly DashboardViewModel vm;
    internal bool IsOverview { get; private set; }
    public FeatureSettingsPanel(DashboardViewModel vm)
    {
        this.vm = vm; Content = body;
        ShowOverview();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && !IsOverview) { ShowOverview(); e.Handled = true; }
        };
    }
    private void ShowOverview()
    {
        IsOverview = true; body.Children.Clear();
        var panel = Section("设置");
        Toggle(panel, "定时自动清理", x => x.AutoReap, (x, b) => x with { AutoReap = b },
            "启用后每 30 分钟、唤醒后或内存吃紧时检查；连续确认至少 120 秒才清理当前用户的孤立 MCP。保护活动会话、监听服务、启动项和计划任务。不会自动删除任何会话文件。",
            "每 30 分钟、唤醒后及内存吃紧时检查，仅清理确认孤立的 MCP 进程。");
        var startup = SettingRows.Toggle(panel, "登录时自动启动", "随 Windows 登录启动，常驻系统托盘。", vm.StartupEnabled);
        startup.Click += (_, _) =>
        {
            try { vm.StartupEnabled = startup.IsChecked == true; }
            catch (Exception error) { startup.IsChecked = vm.StartupEnabled; LocalizedMessageBox.Show(error.Message, "VibeGauge"); }
        };
        Toggle(panel, "阈值通知", x => x.QuotaNotifications || x.MemoryNotifications || x.DiskNotifications,
            (x, b) => x with { QuotaNotifications = b, MemoryNotifications = b, DiskNotifications = b },
            description: "额度、内存或磁盘到达设定阈值时提醒，可在详细设置中分别调整。");
        Toggle(panel, "出口变化通知", x => x.EgressNotifications, (x, b) => x with { EgressNotifications = b },
            description: "AI 出口 IP 或国家变化时提醒；通过手动或定时网络诊断检测。");
        Toggle(panel, "检查软件更新", x => x.CheckUpdates, (x, b) => x with { CheckUpdates = b },
            description: "每天检查 GitHub 上的 VibeGauge 新版本；只提示，不自动安装。");
        BridgeToggle(panel, "Claude Code 额度连接", "claude", false);
        BridgeToggle(panel, "Antigravity (agy) 额度连接", "agy", false);
        BridgeToggle(panel, "待处理会话", "claude", true);
        using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\VibeGauge");
        var forecast = SettingRows.Toggle(panel, "额度预测通知", "预计在重置前耗尽时提醒，每个周期一次。", registry?.GetValue("ForecastNotifications") is int enabled && enabled != 0);
        forecast.Click += (_, _) =>
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\VibeGauge");
                key.SetValue("ForecastNotifications", forecast.IsChecked == true ? 1 : 0);
            }
            catch (Exception error) { forecast.IsChecked = !forecast.IsChecked; LocalizedMessageBox.Show(error.Message, "VibeGauge"); }
        };
        var more = Section("详细设置");
        void Page(string title, string description, Action build) => SettingRows.Navigate(more, title, description, () => ShowPage(build));
        Page("通知与阈值", "分别调整通知项目和触发阈值。", BuildNotifications);
        Page("网络诊断", "自动诊断频率、本机或软路由控制器。", BuildNetwork);
        Page("API 成本与套餐", "配置价格和本机请求额度估算。", BuildAccounting);
        Page("磁盘占用与维护", "查看目录占用，预览旧文件清理。", BuildMaintenance);
        Page("软件版本", "查看 VibeGauge 版本，手动检查或打开发布页。", BuildUpdates);
        Page("官方 CLI", "查看安装状态和登录指引。", BuildCli);
        Page("代理连接", "配置本地端口和上游 HTTP 代理。", () => SettingsPanel.BuildProxy(vm, Section("代理连接")));
    }
    private void ShowPage(Action build)
    {
        IsOverview = false; body.Children.Clear();
        var back = InsightUi.Button("‹ 返回设置", (_, _) => { ShowOverview(); BringTopIntoView(); });
        back.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; back.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(back); build(); BringTopIntoView();
    }
    private void BringTopIntoView() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (body.Children.Count > 0 && body.Children[0] is FrameworkElement first) first.BringIntoView();
    }), System.Windows.Threading.DispatcherPriority.Loaded);
    private Wpf.StackPanel Section(string title) => SettingRows.Card(body, title);
    private void Toggle(Wpf.Panel panel, string label, Func<FeaturePreferences, bool> read, Func<FeaturePreferences, bool, FeaturePreferences> write,
        string? confirm = null, string? description = null)
    {
        var box = SettingRows.Toggle(panel, label, description ?? "", read(FeaturePreferences.Load(vm.Paths)));
        box.Click += (_, _) =>
        {
            if (box.IsChecked == true && confirm is not null && LocalizedMessageBox.Show(confirm, "VibeGauge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            { box.IsChecked = false; return; }
            try { write(FeaturePreferences.Load(vm.Paths), box.IsChecked == true).Save(vm.Paths); }
            catch (Exception e) { box.IsChecked = !box.IsChecked; LocalizedMessageBox.Show(e.Message, "VibeGauge"); }
        };
    }
    private void BridgeToggle(Wpf.Panel panel, string label, string tool, bool hooks)
    {
        var description = hooks ? "观察 Claude Code 等待批准或输入的会话；只记事件和时间，不代你批准，关闭即移除。" :
            "从状态栏读取官方额度，原状态栏照常显示；关闭即可恢复。";
        bool Connected() => hooks ? CliBridge.HooksInstalled(vm.Paths) : CliBridge.StatusInstalled(vm.Paths, tool);
        var box = SettingRows.Toggle(panel, label, description, Connected());
        box.Click += async (_, _) =>
        {
            var enable = box.IsChecked == true;
            if (enable && LocalizedMessageBox.Show(hooks ? "将备份 Claude settings.json 并添加只观察 Hook。不批准请求、不读取提示词、不改其他 Hook。" :
                "将备份客户端配置并连接只读额度桥接。保留原状态栏，可随时恢复。", "VibeGauge", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            { box.IsChecked = Connected(); return; }
            box.IsEnabled = false;
            try
            {
                var executable = ProxyManager.FindProxyExecutable() ?? throw new IOException("缺少 VibeGauge.Proxy.exe，请使用完整安装包");
                CliBridge.Configure(vm.Paths, executable, enable, hooks, tool);
                await vm.RefreshAsync();
            }
            catch (Exception error) { LocalizedMessageBox.Show("连接失败：" + error.Message, "VibeGauge"); }
            finally { box.IsChecked = Connected(); box.IsEnabled = true; }
        };
    }
    private static Wpf.TextBox Input(string text)
    {
        var input = new Wpf.TextBox { Text = text, Padding = new Thickness(5), Margin = new Thickness(0, 3, 0, 6) };
        input.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); input.SetResourceReference(BackgroundProperty, "CardRaisedBrush");
        input.SetResourceReference(Wpf.TextBox.CaretBrushProperty, "TextPrimaryBrush"); return input;
    }
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    private void BuildAccounting()
    {
        var panel = Section("API 等价成本 / Coding Plans");
        panel.Children.Add(InsightUi.Text("prices.json 为每百万 Token 单价；plans.json 为本机请求额度估算。未填价格不会被当作零成本，所有金额均非实际账单。"));
        var priceStatus = InsightUi.Text(""); priceStatus.Name = "OpenRouterPriceStatus";
        var priceUpdate = InsightUi.Button("使用 / 更新 OpenRouter 价格", async (sender, _) =>
        {
            var button = (Wpf.Button)sender; button.IsEnabled = false;
            priceStatus.Text = "正在获取 OpenRouter 公开价格…";
            try
            {
                var snapshot = await OpenRouterPriceUpdater.UpdateAsync(vm.Paths);
                priceStatus.Text = $"已应用 OpenRouter 价格：{snapshot.ModelCount} 个模型 · USD · {snapshot.CapturedAt:yyyy-MM-dd}";
                await vm.RefreshAsync();
            }
            catch { priceStatus.Text = "价格更新失败，原有价格表保持不变；请检查网络后重试。"; }
            finally { button.IsEnabled = true; }
        });
        priceUpdate.Name = "OpenRouterPriceUpdate"; panel.Children.Add(priceUpdate);
        panel.Children.Add(InsightUi.Text("获取公开单价，无需密钥，不上传用量。替换价格表前自动备份；按当前基础 Token 单价估算，不含阶梯价和工具等附加费。自定义模型名未匹配时显示未定价。"));
        panel.Children.Add(priceStatus);
        foreach (var file in new[] { "prices.json", "plans.json" }) panel.Children.Add(InsightUi.Button("编辑 " + file, (_, _) =>
        {
            try
            {
                var path = vm.Paths.ResolveOwnDataFile(file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) File.WriteAllText(path, file == "prices.json" ? "{\n  \"_currency\": \"USD\",\n  \"_asof\": \"\"\n}\n" : "{}\n");
                var info = new ProcessStartInfo("notepad.exe") { UseShellExecute = false }; info.ArgumentList.Add(path); Process.Start(info);
            }
            catch (Exception e) { LocalizedMessageBox.Show(e.Message, "VibeGauge"); }
        }));
        panel.Children.Add(InsightUi.Text("价格模型行：{\"in\":单价,\"out\":单价,\"cache_read\":单价,\"cache_write\":单价}\n套餐厂商行：plan、requests（5h/weekly/monthly）、reset（rolling/first_use/monday/subscription_day）、weights、models、timezone、subscribed_on。"));
    }
    private void BuildNetwork()
    {
        var panel = Section("网络诊断设置");
        Toggle(panel, "每 5 分钟执行完整网络诊断", x => x.NetworkDiagnostics, (x, b) => x with { NetworkDiagnostics = b },
            "将访问 AI 站点的公开 trace、执行本机 DNS / IPv6 检测，并只读查询所配置控制器、Wi-Fi 和 Tailscale。不发送 AI API Key 或用量日志。");
        var networkOptions = FeaturePreferences.Load(vm.Paths);
        var lan = SettingRows.Toggle(panel, "连接局域网软路由", "开启后可填写 OpenClash / Mihomo 的私有 IP 地址。", networkOptions.LanClashController);
        lan.Name = "ClashLan";
        var endpoint = Input(networkOptions.ClashController);
        endpoint.Name = "ClashEndpoint";
        panel.Children.Add(InsightUi.Text("Clash / OpenClash / Mihomo 控制器地址")); panel.Children.Add(endpoint);
        var sourcePanel = new Wpf.StackPanel();
        sourcePanel.Children.Add(InsightUi.Text("来源设备 IP（选填）"));
        var sourceIp = Input(networkOptions.ClashSourceIp); sourceIp.Name = "ClashSourceIp"; sourcePanel.Children.Add(sourceIp);
        sourcePanel.Children.Add(InsightUi.Text("留空查询软路由上所有设备；填写这台电脑的局域网 IP 可只看它的连接。HTTP 仅用于可信局域网，也支持 HTTPS。"));
        panel.Children.Add(sourcePanel);
        ClashControllerCredentials Credentials() => new(vm.Paths, allowLan: lan.IsChecked == true);
        panel.Children.Add(InsightUi.Text("控制器密钥（secret）"));
        var secret = new Wpf.PasswordBox { Name = "ClashSecret", MaxLength = 2000, Padding = new Thickness(5), Margin = new Thickness(0, 3, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(secret, "控制器密钥");
        secret.SetResourceReference(Wpf.PasswordBox.ForegroundProperty, "TextPrimaryBrush");
        secret.SetResourceReference(Wpf.PasswordBox.BackgroundProperty, "CardRaisedBrush");
        secret.SetResourceReference(Wpf.PasswordBox.CaretBrushProperty, "TextPrimaryBrush");
        secret.SetResourceReference(Wpf.PasswordBox.BorderBrushProperty, "BorderStrongBrush");
        panel.Children.Add(secret);
        panel.Children.Add(InsightUi.Text("填写 ClashMi / Clash / Mihomo 的控制器 secret，不是 Google API Key。留空保存会保留现有密钥。"));
        var credentialStatus = InsightUi.Text(""); credentialStatus.Name = "ClashCredentialStatus"; panel.Children.Add(credentialStatus);
        var feedback = InsightUi.Text(""); feedback.Name = "ClashFeedback";
        void Feedback(string message, bool error = false)
        {
            feedback.Text = message;
            feedback.SetResourceReference(Wpf.TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextSecondaryBrush");
        }
        void RefreshCredentialStatus()
        {
            try
            {
                credentialStatus.Text = Credentials().ReadSaved(endpoint.Text) is not null ? "已保存此控制器的密钥；输入新值可替换。" :
                    ClashControllerCredentials.IsLocal(ClashControllerCredentials.NormalizeEndpoint(endpoint.Text, lan.IsChecked == true)) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VIBEGAUGE_CLASH_SECRET")) ? "当前使用环境变量中的密钥；保存新密钥后优先使用新值。" : "尚未保存此控制器的密钥。";
            }
            catch (ArgumentException) { credentialStatus.Text = ClashControllerCredentials.InvalidEndpoint; }
            catch (InvalidOperationException) { credentialStatus.Text = ClashControllerCredentials.UnreadableSecret; }
        }
        var actions = new Wpf.WrapPanel();
        var save = InsightUi.Button("保存控制器设置", (_, _) =>
        {
            try
            {
                var address = ClashControllerCredentials.NormalizeEndpoint(endpoint.Text, lan.IsChecked == true).AbsoluteUri.TrimEnd('/');
                var filter = lan.IsChecked == true ? ClashControllerCredentials.NormalizeSourceIp(sourceIp.Text) : "";
                if (secret.Password.Length > 0) Credentials().Save(address, secret.Password);
                (FeaturePreferences.Load(vm.Paths) with { ClashController = address, LanClashController = lan.IsChecked == true, ClashSourceIp = filter }).Save(vm.Paths);
                secret.Clear(); RefreshCredentialStatus(); Feedback("已保存，下一次检测立即生效，无需重启。");
            }
            catch (ArgumentException error) { Feedback(error.Message, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { Feedback("保存失败，请检查本机数据目录是否可写。", true); }
        });
        save.Name = "ClashSave"; actions.Children.Add(save);
        var test = InsightUi.Button("测试连接", async (_, _) =>
        {
            endpoint.IsEnabled = secret.IsEnabled = actions.IsEnabled = lan.IsEnabled = sourceIp.IsEnabled = false;
            Feedback("正在测试控制器…");
            try
            {
                var result = await new ClashControllerClient(vm.Paths).ReadAsync(endpoint.Text, secret.Password.Length > 0 ? secret.Password : null, lan.IsChecked == true, sourceIp.Text);
                Feedback(result.Available ? "连接成功，可读取活动连接表。测试不会保存输入；返回网络页点击完整诊断可更新 Gemini 链路。" : result.Error, !result.Available);
            }
            finally { endpoint.IsEnabled = secret.IsEnabled = actions.IsEnabled = lan.IsEnabled = sourceIp.IsEnabled = true; }
        });
        test.Name = "ClashTest"; actions.Children.Add(test);
        var remove = InsightUi.Button("移除已保存密钥", (_, _) =>
        {
            try { Credentials().Remove(endpoint.Text); secret.Clear(); RefreshCredentialStatus(); Feedback("已移除此控制器的密钥；本机控制器仍可使用环境变量中的密钥。"); }
            catch (ArgumentException error) { Feedback(error.Message, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Feedback("移除失败，请检查本机数据目录是否可写。", true); }
        });
        remove.Name = "ClashRemove"; actions.Children.Add(remove);
        panel.Children.Add(actions); panel.Children.Add(feedback);
        panel.Children.Add(InsightUi.Text("密钥按控制器地址分别加密保存，仅发送给所填控制器。软路由不会使用本机环境变量中的密钥。"));
        endpoint.TextChanged += (_, _) => { secret.Clear(); RefreshCredentialStatus(); feedback.Text = ""; };
        void RefreshMode()
        {
            sourcePanel.Visibility = lan.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            secret.Clear(); RefreshCredentialStatus(); feedback.Text = "";
        }
        lan.Click += (_, _) => RefreshMode();
        secret.PasswordChanged += (_, _) => feedback.Text = "";
        secret.Unloaded += (_, _) => secret.Clear();
        RefreshMode();
    }
    private void BuildNotifications()
    {
        var panel = Section("通知与阈值");
        Toggle(panel, "等待审批 / 输入超过 1 分钟", x => x.PendingNotifications, (x, b) => x with { PendingNotifications = b });
        Toggle(panel, "官方额度到达阈值", x => x.QuotaNotifications, (x, b) => x with { QuotaNotifications = b });
        Toggle(panel, "可用内存不足", x => x.MemoryNotifications, (x, b) => x with { MemoryNotifications = b });
        Toggle(panel, "系统盘空间不足", x => x.DiskNotifications, (x, b) => x with { DiskNotifications = b });
        var options = FeaturePreferences.Load(vm.Paths);
        var quota = Input(options.QuotaThreshold.ToString()); var memory = Input(options.MemoryFreeThreshold.ToString()); var disk = Input(options.DiskFreeGbThreshold.ToString());
        foreach (var (label, box) in new[] { ("额度已用阈值 %（50–100）", quota), ("内存可用阈值 %（1–50）", memory), ("系统盘可用阈值 GB（1–500）", disk) })
        { panel.Children.Add(InsightUi.Text(label)); panel.Children.Add(box); }
        panel.Children.Add(InsightUi.Button("保存通知阈值", (_, _) =>
        {
            if (!int.TryParse(quota.Text, out var q) || q is < 50 or > 100 || !int.TryParse(memory.Text, out var m) || m is < 1 or > 50 || !int.TryParse(disk.Text, out var d) || d is < 1 or > 500)
            { LocalizedMessageBox.Show("请输入标注范围内的整数", "VibeGauge"); return; }
            (FeaturePreferences.Load(vm.Paths) with { QuotaThreshold = q, MemoryFreeThreshold = m, DiskFreeGbThreshold = d }).Save(vm.Paths);
        }));
    }
    private void BuildMaintenance()
    {
        var panel = Section("磁盘占用 / 安全维护");
        var inventory = new DiskInventory(vm.Paths);
        var listing = InsightUi.Text("点击扫描后显示各客户端目录；模型文件与数据库只读。");
        panel.Children.Add(InsightUi.Button("扫描 AI 目录占用", async (sender, _) =>
        {
            var button = (Wpf.Button)sender; button.IsEnabled = false;
            try { listing.Text = string.Join("\n", (await Task.Run(inventory.Scan)).Select(x => $"{x.Area.Name}：{x.Bytes / 1048576d:N1} MB · {x.Files:N0} 文件{(x.Partial ? "（部分不可读）" : "")}")); }
            catch (Exception e) { listing.Text = e.Message; }
            finally { button.IsEnabled = true; }
        }));
        panel.Children.Add(listing);
        var area = new Wpf.ComboBox { ItemsSource = inventory.Areas.Where(x => x.Sessions || x.Cache).ToArray(), DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 5) };
        panel.Children.Add(area);
        panel.Children.Add(InsightUi.Text("保留最近天数（7–3650）；NPX 也按最后修改时间筛选"));
        var days = Input(FeaturePreferences.Load(vm.Paths).RetentionDays.ToString()); panel.Children.Add(days);
        panel.Children.Add(InsightUi.Button("预览并清理旧文件", async (sender, _) =>
        {
            var button = (Wpf.Button)sender; button.IsEnabled = false;
            try
            {
                if (!int.TryParse(days.Text, out var count)) throw new ArgumentException("保留期必须为整数");
                if (area.SelectedItem is not InventoryArea selected) return;
                var plan = await Task.Run(() => inventory.Preview(selected, count, DateTimeOffset.Now));
                (FeaturePreferences.Load(vm.Paths) with { RetentionDays = count }).Save(vm.Paths);
                if (plan.Files.Count == 0) { listing.Text = "没有符合保留期的可清理文件"; return; }
                if (LocalizedMessageBox.Show($"目录：{selected.Root}\n将永久删除 {plan.Files.Count:N0} 个文件，约 {plan.Bytes / 1048576d:N1} MB。\n{(selected.Cache ? "NPX 依赖将在下次使用时重新下载。" : "删除后不能从客户端恢复这些会话；已读取的用量账本保留。未曾读取的损坏记录无法恢复。不要在客户端正在使用这些文件时操作。")}\n\n确认继续？", "确认永久删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                if (selected.Cache && Process.GetProcesses().Any(p => { using (p) return p.ProcessName is "node" or "bun" or "deno"; }))
                    throw new IOException("检测到 Node/Bun/Deno 正在运行；请先退出相关任务再清理 NPX 缓存。");
                var outcome = await Task.Run(() => inventory.Delete(plan, vm.EnsureDurableHistory));
                listing.Text = $"已删除 {outcome.Deleted:N0} 个，跳过 {outcome.Skipped:N0} 个，释放 {outcome.Bytes / 1048576d:N1} MB";
                await vm.RefreshAsync();
            }
            catch (Exception e) { listing.Text = "已停止清理：" + e.Message; }
            finally { button.IsEnabled = true; }
        }));
    }
    private void BuildUpdates()
    {
        var panel = Section("VibeGauge 软件更新");
        panel.Children.Add(InsightUi.Text("当前版本 " + vm.AppVersionText, true));
        panel.Children.Add(InsightUi.Text("检查 VibeGauge 的 Windows 发行包，不检查 Windows 系统更新。"));
        var checker = new UpdateChecker(vm.Paths); var status = InsightUi.Text(checker.Status); panel.Children.Add(status);
        panel.Children.Add(InsightUi.Button("立即检查更新", async (sender, _) =>
        {
            var button = (Wpf.Button)sender; button.IsEnabled = false;
            try { await checker.CheckAsync(); status.Text = checker.Status; }
            finally { button.IsEnabled = true; }
        }));
        panel.Children.Add(InsightUi.Button("打开 VibeGauge 发布页", (_, _) => Open("https://github.com/Fourgetu/vibe-gauge/releases")));
    }
    private void BuildCli()
    {
        var panel = Section("官方 CLI 安装 / 登录");
        foreach (var (name, login, url, usage) in new[] {
            ("Ark CLI", "arkcli auth login volc-sso", "https://www.npmjs.com/package/@volcengine/ark-cli", "arkcli usage plan --product coding-plan --format json"),
            ("百炼 CLI", "bl auth login --console", "https://bailian.aliyun.com/cli/", "bl usage coding-plan --output json") })
        {
            panel.Children.Add(InsightUi.Text(name, true));
            var executable = OfficialSources.FindExecutable(vm.Paths, name == "Ark CLI" ? "arkcli" : "bl");
            var installed = InsightUi.Text(executable is null ? "未检测到原生 CLI 可执行文件；安装后重启应用再检查" : "已安装；请先登录后查询额度");
            installed.ToolTip = executable; panel.Children.Add(installed);
            var controls = new Wpf.WrapPanel();
            controls.Children.Add(InsightUi.Button("安装文档", (_, _) => Open(url)));
            controls.Children.Add(InsightUi.Button("复制登录命令", (_, _) => System.Windows.Clipboard.SetText(login)));
            controls.Children.Add(InsightUi.Button("复制状态命令", (_, _) => System.Windows.Clipboard.SetText(usage)));
            if (name == "Ark CLI") controls.Children.Add(InsightUi.Button("复制 Windows 安装命令", (_, _) => System.Windows.Clipboard.SetText(
                "$previousCI = $env:CI; try { $env:CI = '1'; npm install -g '@volcengine/ark-cli' } finally { $env:CI = $previousCI }")));
            panel.Children.Add(controls);
        }
        panel.Children.Add(InsightUi.Text("安装、登录均由你在终端确认；不执行 macOS 脚本，不自动登录。凭据过期时重新登录后刷新。"));
        panel.Children.Add(InsightUi.Button("登录后刷新官方额度", async (_, _) => { vm.InvalidateOfficial(); await vm.RefreshAsync(); }));
    }
}
