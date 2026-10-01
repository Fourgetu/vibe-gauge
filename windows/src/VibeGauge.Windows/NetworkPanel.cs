using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;

namespace VibeGauge.Windows;

public sealed class NetworkPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel exits = new(), proxy = new(), local = new(), details = new();
    private readonly Wpf.TextBlock summary = Label("尚未探测，点击右侧刷新", 12);
    private readonly Wpf.TextBlock result = Label("出口检测仅在点击时联网，不发送账号或用量数据。", 11);
    private readonly Wpf.Expander proxyDetails, adapterDetails, diagnosticDetails;
    private readonly Wpf.StackPanel proxyRows = new(), adapterRows = new();
    private readonly NetworkDiagnostics standalone = new(new AppPaths());
    private NetworkDiagnosticsReport? lastDiagnostics;
    private IReadOnlyList<NetworkAdapterInfo> lastAdapters = [];
    private string lastAges = "";

    public NetworkPanel()
    {
        var body = new Wpf.StackPanel { Margin = new Thickness(0, 0, 5, 12) };
        var header = new Wpf.DockPanel { Margin = new Thickness(2, 0, 0, 5) };
        var refresh = new Wpf.Button { Name = "NetworkRefresh", Content = "↻", FontSize = 21,
            ToolTip = "完整诊断", Style = (Style)System.Windows.Application.Current.FindResource("IconButtonStyle") };
        AutomationProperties.SetName(refresh, "完整诊断");
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try
            {
                RenderDiagnostics(await (DataContext is ViewModels.DashboardViewModel vm
                    ? vm.RefreshDiagnosticsAsync() : standalone.RefreshAsync()));
            }
            finally { refresh.IsEnabled = true; }
        };
        Wpf.DockPanel.SetDock(refresh, Wpf.Dock.Right); header.Children.Add(refresh);
        header.Children.Add(Label("网络", 15, primary: true, bold: true));
        body.Children.Add(header);
        summary.Name = "NetworkSummary"; summary.Margin = new Thickness(2, 0, 2, 12);
        body.Children.Add(summary);

        var egressCard = Card(body, "AI 出口", "NetworkEgressCard");
        egressCard.Children.Add(exits);
        var note = Label("本应用探测到的出口；使用独立代理的客户端可能不同。", 10.5);
        note.Margin = new Thickness(0, 5, 0, 0); egressCard.Children.Add(note);
        var proxyCard = Card(body, "代理内核", "NetworkProxyCard");
        proxyCard.Children.Add(proxy);
        proxyDetails = Disclosure("全部代理组与连接", proxyRows, "NetworkProxyDetails"); proxyCard.Children.Add(proxyDetails);
        var localCard = Card(body, "本地网络", "NetworkLocalCard");
        localCard.Children.Add(local);
        adapterDetails = Disclosure("全部网卡", adapterRows, "NetworkAdapterDetails"); localCard.Children.Add(adapterDetails);

        var diagnosticBody = new Wpf.StackPanel();
        var actions = new Wpf.WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var ipv6 in new[] { false, true })
            actions.Children.Add(InsightUi.Button(ipv6 ? "检查 IPv6 直连" : "检测出口", async (sender, _) =>
            {
                var button = (Wpf.Button)sender; button.IsEnabled = false;
                try { result.Text = await NetworkMonitor.ProbeEgressAsync(ipv6); }
                finally { button.IsEnabled = true; }
            }));
        diagnosticBody.Children.Add(actions); diagnosticBody.Children.Add(result); diagnosticBody.Children.Add(details);
        diagnosticDetails = Disclosure("诊断详情", diagnosticBody, "NetworkDiagnosticDetails");
        body.Children.Add(diagnosticDetails);
        RenderExits(null); RenderProxy(null); RenderLocal();
        Content = new Wpf.ScrollViewer { Content = body, VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Wpf.ScrollBarVisibility.Disabled };
    }

    public void Update(IReadOnlyList<NetworkAdapterInfo>? value, NetworkDiagnosticsReport? report = null)
    {
        var next = value ?? [];
        var changed = !lastAdapters.SequenceEqual(next);
        lastAdapters = next;
        if (report is not null) RenderDiagnostics(report);
        if (changed) RenderLocal();
    }

    private void RenderDiagnostics(NetworkDiagnosticsReport report)
    {
        var ages = string.Join("|", report.Exits.Select(x => Age(x.CapturedAt)));
        if (ReferenceEquals(lastDiagnostics, report) && lastAges == ages) return;
        lastDiagnostics = report; lastAges = ages;
        // Keep the complete comparison semantics, including the trace denominator.
        summary.Text = report.Comparison.Replace(" · Gemini 使用活动连接链判断", "");
        summary.ToolTip = report.Comparison;
        RenderExits(report); RenderProxy(report); RenderLocal();
        details.Children.Clear();
        details.Children.Add(Label($"诊断时间 {report.CapturedAt.LocalDateTime:MM-dd HH:mm:ss}", 11));
        foreach (var exit in report.Exits) InsightUi.Row(details, exit.Provider, ExitDetail(exit));
        InsightUi.Row(details, "DNS / IPv6", report.Dns + "\n" + report.Ipv6);
        if (report.Local.Count > 0) InsightUi.Row(details, "Wi-Fi / Tailscale", string.Join("\n", report.Local));
        if (report.Changes.Count > 0) InsightUi.Row(details, "出口变化", string.Join("\n", report.Changes));
    }

    private void RenderExits(NetworkDiagnosticsReport? report)
    {
        exits.Children.Clear();
        var entries = report?.Exits ?? NetworkDiagnostics.Targets.Select(x => new EgressInfo(x.Provider, x.Host, "", "", "", ""))
            .Append(new("Gemini", "gemini.google.com", "", "", "", "")).ToArray();
        foreach (var exit in entries)
        {
            var row = new Wpf.StackPanel { Margin = new Thickness(0, 0, 0, 8), ToolTip = ExitDetail(exit) };
            var isGemini = exit.Provider == "Gemini";
            var hasError = exit.Error.Length > 0;
            var value = report is null ? "尚未探测" : hasError ? (isGemini ? "未取得连接链" : "探测失败") :
                isGemini ? "基于活动连接" : exit.Ip;
            var top = Pair(exit.Provider, value, 124, bold: true);
            row.Children.Add(top);
            var caption = report is null ? "等待刷新" : hasError ? exit.Error : isGemini ? FirstChain(exit.Chain) :
                string.Join(" · ", new[] { exit.Region, exit.Colo }.Where(x => x.Length > 0));
            if (exit.Provider == "Cloudflare") caption = "对照探测" + (caption.Length > 0 ? " · " + caption : "");
            var bottom = new Wpf.Grid();
            bottom.ColumnDefinitions.Add(new Wpf.ColumnDefinition());
            bottom.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = GridLength.Auto });
            var meta = Label(string.Join(" · ", new[] { exit.LatencyMs is { } ms ? $"{ms} ms" : "", Age(exit.CapturedAt) }.Where(x => x.Length > 0)), 10.5);
            meta.Margin = new Thickness(8, 2, 0, 0); Wpf.Grid.SetColumn(meta, 1);
            // Long controller errors and routes remain available in the tooltip and details.
            var sub = Label(caption, 11); sub.TextWrapping = TextWrapping.NoWrap; sub.TextTrimming = TextTrimming.CharacterEllipsis;
            bottom.Children.Add(sub); bottom.Children.Add(meta); row.Children.Add(bottom); exits.Children.Add(row);
        }
    }

    private void RenderProxy(NetworkDiagnosticsReport? report)
    {
        proxy.Children.Clear(); proxyRows.Children.Clear();
        proxyDetails.Visibility = report?.Proxy.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (report is null) { proxy.Children.Add(Label("等待刷新")); return; }
        var rows = report.Proxy;
        var groups = rows.Where(x => x.StartsWith("代理组 ", StringComparison.Ordinal)).ToArray();
        var errors = rows.Where(x => x.StartsWith("proxies：", StringComparison.Ordinal) || x.StartsWith("connections：", StringComparison.Ordinal)).ToArray();
        var connections = report.Connections ?? [];
        var status = errors.Length > 0 ? "部分接口不可用" : groups.Length > 0 || connections.Count > 0 ? "已读取控制器" : "未取得控制器状态";
        var state = Label(status + " · Clash / Mihomo", 12, bold: true);
        state.SetResourceReference(Wpf.TextBlock.ForegroundProperty, errors.Length > 0 ? "WarningBrush" : groups.Length > 0 || connections.Count > 0 ? "GoodBrush" : "TextSecondaryBrush");
        proxy.Children.Add(state);
        foreach (var scope in rows.Where(x => x.StartsWith("软路由", StringComparison.Ordinal)))
        {
            var scopeLabel = Label(scope, 10.5); scopeLabel.ToolTip = scope;
            scopeLabel.TextWrapping = TextWrapping.NoWrap; scopeLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            proxy.Children.Add(scopeLabel);
        }
        foreach (var group in groups.OrderByDescending(x => x.StartsWith("代理组 AI →", StringComparison.Ordinal) || x.StartsWith("代理组 Proxy →", StringComparison.Ordinal))
            .ThenByDescending(x => connections.Any(c => c.Chain.Split(" → ").Contains(GroupName(x)))).Take(2))
        {
            var parts = group[4..].Split(" → ", 2);
            proxy.Children.Add(Pair(parts[0], parts.Length > 1 ? parts[1] : "—", 85));
        }
        var gemini = connections.Where(x => x.Provider == "Gemini").ToArray();
        if (gemini.Length > 0) proxy.Children.Add(Pair("Gemini", $"{gemini.Length} 个活动连接", 85));
        if (errors.Length > 0 || groups.Length == 0 && connections.Count == 0 && rows.Count > 0)
        {
            var error = errors.FirstOrDefault() ?? rows.FirstOrDefault() ?? "未取得控制器状态";
            proxy.Children.Add(Label(error, 11));
        }
        foreach (var text in rows) proxyRows.Children.Add(Label(text, 11));
    }

    private void RenderLocal()
    {
        local.Children.Clear(); adapterRows.Children.Clear();
        adapterDetails.Visibility = lastAdapters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Prefer a physical adapter with a gateway; do not sum tunnel traffic twice.
        var primary = lastAdapters.OrderByDescending(x => x.Gateway.Length > 0)
            .ThenByDescending(x => x.Kind is "Ethernet" or "Wireless80211").FirstOrDefault();
        if (primary is null) { local.Children.Add(Label("未检测到活动网卡")); return; }
        var addresses = primary.Addresses.Split(" · ").Select(x => IPAddress.TryParse(x, out var ip) ? ip : null).Where(x => x is not null).ToArray();
        local.Children.Add(Pair("网关", (primary.Gateway.Length > 0 ? primary.Gateway : "—") + " · " + primary.Name, 70));
        local.Children.Add(Pair("IPv4", string.Join(", ", addresses.Where(x => x!.AddressFamily == AddressFamily.InterNetwork)), 70));
        var ipv6 = addresses.Where(x => x!.AddressFamily == AddressFamily.InterNetworkV6 && !x.IsIPv6LinkLocal && !IPAddress.IsLoopback(x)).ToArray();
        local.Children.Add(Pair("IPv6", ipv6.Length > 0 ? string.Join(", ", ipv6.Select(x => x!.ToString())) : "未检测到非链路本地 IPv6", 70));
        local.Children.Add(Pair("DNS", primary.Dns, 70));
        var ssid = lastDiagnostics?.Local.FirstOrDefault(x => x.StartsWith("SSID", StringComparison.OrdinalIgnoreCase));
        if (ssid is not null) local.Children.Add(Pair("Wi-Fi", ssid[(ssid.IndexOf(':') + 1)..].Trim(), 70));
        local.Children.Add(Pair("实时速率", $"↓ {Rate(primary.ReceiveBytesPerSecond)}   ↑ {Rate(primary.SendBytesPerSecond)}", 70));
        foreach (var adapter in lastAdapters)
            InsightUi.Row(adapterRows, adapter.Name + " · " + adapter.Kind,
                $"{adapter.Addresses}\n网关：{adapter.Gateway}\nDNS：{adapter.Dns}\n↓ {Rate(adapter.ReceiveBytesPerSecond)}   ↑ {Rate(adapter.SendBytesPerSecond)}");
    }

    private static string GroupName(string row) => row[4..].Split(" → ", 2)[0];
    private static string FirstChain(string chain)
    {
        var lines = chain.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "未回报链路";
        var first = lines[0]; var hostEnd = first.IndexOf(" · ", StringComparison.Ordinal);
        return hostEnd >= 0 ? first[(hostEnd + 3)..] : first;
    }
    private static string Rate(double value) => value >= 1024 * 1024 ? $"{value / (1024 * 1024):0.0} MB/s" : $"{value / 1024:0.0} KB/s";
    private static string Age(DateTimeOffset? value) => value is { } captured
        ? DateTimeOffset.Now - captured < TimeSpan.FromMinutes(1) ? "刚刚" : $"{Math.Max(0, (DateTimeOffset.Now - captured).TotalMinutes):0}分钟前" : "";
    private static string ExitDetail(EgressInfo exit) => exit.Host + "\n" + (exit.Error.Length > 0 ? exit.Error : exit.Chain.Length > 0 ? exit.Chain : $"{exit.Ip} · {exit.Region} · {exit.Colo}") +
        (exit.LatencyMs is { } ms ? $" · {ms} ms" : "") + (exit.CapturedAt is { } captured ? $" · 采集 {captured.LocalDateTime:MM-dd HH:mm:ss}" : "");

    private static Wpf.TextBlock Label(string text, double size = 12, bool primary = false, bool bold = false)
    {
        var label = new Wpf.TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 2, 0, 2) };
        label.SetResourceReference(Wpf.TextBlock.ForegroundProperty, primary ? "TextPrimaryBrush" : "TextSecondaryBrush");
        return label;
    }
    private static Wpf.Grid Pair(string name, string value, double labelWidth, bool bold = false)
    {
        var row = new Wpf.Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new Wpf.ColumnDefinition { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new Wpf.ColumnDefinition());
        var title = Label(name, 12, primary: true, bold: true);
        title.Margin = new Thickness(0, 2, 8, 2); row.Children.Add(title);
        var text = Label(value, 12, primary: bold, bold: bold);
        text.ToolTip = value; text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
        if (bold) text.FontFamily = new Media.FontFamily("Consolas");
        Wpf.Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }
    private static Wpf.StackPanel Card(Wpf.Panel parent, string title, string name)
    {
        var content = new Wpf.StackPanel();
        var heading = Label(title, 14, bold: true); heading.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(heading);
        var card = new Wpf.Border { Name = name, Child = content, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 12, 13, 10), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(Wpf.Border.BackgroundProperty, "CardBrush"); parent.Children.Add(card); return content;
    }
    private static Wpf.Expander Disclosure(string title, Wpf.StackPanel content, string name)
    {
        var expander = new Wpf.Expander { Name = name, Header = title, Content = content, FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        expander.SetResourceReference(Wpf.Control.ForegroundProperty, "TextSecondaryBrush"); return expander;
    }
}
