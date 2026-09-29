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
    public FeatureSettingsPanel(DashboardViewModel vm)
    {
        this.vm = vm; Content = body;
        body.Children.Add(InsightUi.Text("语言 / Language", true));
        var languages = new Wpf.ComboBox { ItemsSource = new[] { "跟随系统", "简体中文", "English" }, Margin = new Thickness(0, 4, 0, 6) };
        languages.SelectedIndex = FeaturePreferences.Load(vm.Paths).Language switch { "zh" => 1, "en" => 2, _ => 0 };
        languages.SelectionChanged += (_, _) =>
        {
            var language = languages.SelectedIndex switch { 1 => "zh", 2 => "en", _ => "system" };
            (FeaturePreferences.Load(vm.Paths) with { Language = language }).Save(vm.Paths);
            UiLocalization.SetLanguage(language);
        };
        body.Children.Add(languages);
        BuildBridge(); BuildAccounting(); BuildNetwork(); BuildNotifications(); BuildMaintenance(); BuildUpdates(); BuildCli();
    }
    private Wpf.StackPanel Section(string title)
    {
        var panel = new Wpf.StackPanel { Margin = new Thickness(8, 4, 0, 10) };
        var expander = new Wpf.Expander { Header = title, Content = panel, Margin = new Thickness(0, 5, 0, 5) };
        expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        body.Children.Add(expander); return panel;
    }
    private void Toggle(Wpf.Panel panel, string label, Func<FeaturePreferences, bool> read, Func<FeaturePreferences, bool, FeaturePreferences> write, string? confirm = null)
    {
        var box = new Wpf.CheckBox { Content = label, IsChecked = read(FeaturePreferences.Load(vm.Paths)),
            Style = (Style)System.Windows.Application.Current.FindResource("ToggleStyle"), Margin = new Thickness(0, 4, 0, 5) };
        box.Click += (_, _) =>
        {
            if (box.IsChecked == true && confirm is not null && LocalizedMessageBox.Show(confirm, "VibeGauge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            { box.IsChecked = false; return; }
            try { write(FeaturePreferences.Load(vm.Paths), box.IsChecked == true).Save(vm.Paths); }
            catch (Exception e) { box.IsChecked = !box.IsChecked; LocalizedMessageBox.Show(e.Message, "VibeGauge"); }
        };
        panel.Children.Add(box);
    }
    private static Wpf.TextBox Input(string text)
    {
        var input = new Wpf.TextBox { Text = text, Padding = new Thickness(5), Margin = new Thickness(0, 3, 0, 6) };
        input.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); input.SetResourceReference(BackgroundProperty, "CardRaisedBrush");
        input.SetResourceReference(Wpf.TextBox.CaretBrushProperty, "TextPrimaryBrush"); return input;
    }
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    private void BuildBridge()
    {
        var panel = Section("Gemini / AGY 额度桥接");
        var status = InsightUi.Text(CliBridge.StatusInstalled(vm.Paths, "agy") ? "已连接 AGY 状态栏" : "未连接 AGY 状态栏");
        panel.Children.Add(status);
        foreach (var enable in new[] { true, false }) panel.Children.Add(InsightUi.Button(enable ? "连接 AGY 额度" : "恢复 AGY 状态栏", async (_, _) =>
        {
            try
            {
                var executable = ProxyManager.FindProxyExecutable() ?? throw new IOException("缺少 VibeGauge.Proxy.exe，请使用完整安装包");
                if (enable && LocalizedMessageBox.Show("将备份 .gemini/antigravity-cli/settings.json，并连接只读额度桥接。保留原状态栏，可随时恢复。", "AGY", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                CliBridge.Configure(vm.Paths, executable, enable, false, "agy");
                status.Text = CliBridge.StatusInstalled(vm.Paths, "agy") ? "已连接；新 AGY 会话生效" : "已恢复原状态栏";
                await vm.RefreshAsync();
            }
            catch (Exception e) { status.Text = e.Message; }
        }));
    }
    private void BuildAccounting()
    {
        var panel = Section("API 等价成本 / Coding Plans");
        panel.Children.Add(InsightUi.Text("prices.json 为每百万 Token 单价；plans.json 为本机请求额度估算。未填价格不会被当作零成本，所有金额均非实际账单。"));
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
            "将访问 AI 站点的公开 trace、执行本机 DNS / IPv6 检测，并只读查询本地 Clash、Wi-Fi 和 Tailscale。不发送 API Key 或用量日志。");
        Toggle(panel, "出口 IP 变化时通知", x => x.EgressNotifications, (x, b) => x with { EgressNotifications = b });
        var endpoint = Input(FeaturePreferences.Load(vm.Paths).ClashController);
        panel.Children.Add(InsightUi.Text("Clash / Mihomo 本机控制器")); panel.Children.Add(endpoint);
        panel.Children.Add(InsightUi.Button("保存控制器地址", (_, _) =>
        {
            try { (FeaturePreferences.Load(vm.Paths) with { ClashController = endpoint.Text.Trim() }).Save(vm.Paths); }
            catch (Exception e) { LocalizedMessageBox.Show(e.Message, "VibeGauge"); }
        }));
        panel.Children.Add(InsightUi.Text("有认证时，通过 VIBEGAUGE_CLASH_SECRET 环境变量提供密钥；不会保存或显示此密钥。"));
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
        Toggle(panel, "自动清理确认孤立的 MCP 进程", x => x.AutoReap, (x, b) => x with { AutoReap = b },
            "启用后每 30 分钟、唤醒后或内存吃紧时检查；连续确认至少 120 秒才清理当前用户的孤立 MCP。保护活动会话、监听服务、启动项和计划任务。不会自动删除任何会话文件。");
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
        var panel = Section("Windows 版本更新");
        Toggle(panel, "每天检查一次 Windows 正式新版", x => x.CheckUpdates, (x, b) => x with { CheckUpdates = b });
        var checker = new UpdateChecker(vm.Paths); var status = InsightUi.Text(checker.Status); panel.Children.Add(status);
        panel.Children.Add(InsightUi.Button("立即检查更新", async (sender, _) =>
        {
            var button = (Wpf.Button)sender; button.IsEnabled = false;
            try { await checker.CheckAsync(); status.Text = checker.Status; }
            finally { button.IsEnabled = true; }
        }));
        panel.Children.Add(InsightUi.Button("打开 Windows 发布页", (_, _) => Open("https://github.com/Fourgetu/vibe-gauge/releases")));
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
