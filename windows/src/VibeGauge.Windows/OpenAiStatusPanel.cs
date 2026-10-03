using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using VibeGauge.Core;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;

namespace VibeGauge.Windows;

public sealed class OpenAiStatusPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel rows = new();
    private readonly Wpf.TextBlock note = Text("打开网络页时查询官方状态", 10.5);
    private readonly Wpf.Button refresh;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(3) };
    private readonly Func<CancellationToken, Task<OpenAiStatusSnapshot>> fetch;
    private readonly bool automatic;
    private CancellationTokenSource? pending;
    private OpenAiStatusSnapshot? lastGood;
    private DateTimeOffset lastAttempt;

    public OpenAiStatusPanel() : this(true, OpenAiStatus.FetchAsync) { }
    public OpenAiStatusPanel(bool automatic, Func<CancellationToken, Task<OpenAiStatusSnapshot>> fetch)
    {
        this.automatic = automatic; this.fetch = fetch;
        var body = new Wpf.StackPanel();
        var heading = new Wpf.DockPanel();
        refresh = InsightUi.Button("↻", async (_, _) => await RefreshAsync());
        refresh.ToolTip = "刷新 OpenAI 官方状态";
        System.Windows.Automation.AutomationProperties.SetName(refresh, "刷新 OpenAI 官方状态");
        Wpf.DockPanel.SetDock(refresh, Wpf.Dock.Right); heading.Children.Add(refresh);
        var title = Text("OpenAI 官方运行状态", 13, true);
        heading.Children.Add(title); body.Children.Add(heading); body.Children.Add(note); body.Children.Add(rows);
        var footer = new Wpf.DockPanel { Margin = new Thickness(0, 9, 0, 0) };
        var link = InsightUi.Button("官方网站 ↗", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(OpenAiStatus.Url) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { note.Text = "无法打开默认浏览器"; }
        });
        Wpf.DockPanel.SetDock(link, Wpf.Dock.Right); footer.Children.Add(link);
        footer.Children.Add(Text("全球服务状态 · 非本机连通性\n绿色 正常 · 黄色 降级 · 红色 中断", 9.5));
        body.Children.Add(footer);
        var card = new Wpf.Border { Child = body, CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 10, 0, 0) };
        card.SetResourceReference(Wpf.Border.BackgroundProperty, "CardBrush");
        Content = card;
        Loaded += (_, _) => VisibilityChanged();
        IsVisibleChanged += (_, _) => VisibilityChanged();
        Unloaded += (_, _) => { timer.Stop(); pending?.Cancel(); };
        timer.Tick += async (_, _) => { if (IsVisible) await RefreshAsync(); };
    }

    private async void VisibilityChanged()
    {
        if (!automatic || !IsLoaded || !IsVisible) { timer.Stop(); return; }
        timer.Start();
        if (DateTimeOffset.UtcNow - lastAttempt >= TimeSpan.FromMinutes(3)) await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (pending is not null) return;
        using var request = new CancellationTokenSource(); pending = request;
        lastAttempt = DateTimeOffset.UtcNow;
        refresh.IsEnabled = false; note.Text = "正在查询官方状态…";
        try
        {
            var snapshot = await fetch(request.Token);
            if (!request.IsCancellationRequested) ShowSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { lastAttempt = DateTimeOffset.MinValue; ShowFailure("查询已取消"); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.IO.IOException or System.IO.InvalidDataException or
            System.Text.Json.JsonException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException or FormatException or KeyNotFoundException)
        { ShowFailure("官方状态暂不可用"); }
        finally { pending = null; refresh.IsEnabled = true; }
    }

    private void ShowFailure(string message)
    {
        note.Text = lastGood is null ? message + "，点击 ↻ 重试" :
            $"{message} · 以下为旧数据\n上次成功 {lastGood.CapturedAt.LocalDateTime:MM-dd HH:mm:ss}";
        note.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "WarningBrush");
    }

    public void ShowSnapshot(OpenAiStatusSnapshot snapshot)
    {
        lastGood = snapshot;
        note.Text = $"近 {snapshot.HistoryDays} 天 · {snapshot.CapturedAt.LocalDateTime:HH:mm} 更新 · 每 3 分钟查询";
        note.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
        var expanded = rows.Children.OfType<Wpf.Expander>().Where(x => x.IsExpanded).Select(x => x.Tag).ToHashSet();
        rows.Children.Clear();
        foreach (var group in snapshot.Groups)
        {
            var children = new Wpf.StackPanel { Margin = new Thickness(8, 5, 0, 0) };
            var header = Row(group);
            var expander = new Wpf.Expander { Tag = group.Id, Header = header, Content = children,
                Style = (Style)FindResource("StatusGroupStyle"),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 6, 0, 4) };
            expander.SetResourceReference(Wpf.Control.ForegroundProperty, "TextPrimaryBrush");
            // Component histories are created only when requested, keeping the collapsed page small.
            expander.Expanded += (_, _) =>
            {
                if (children.Children.Count != 0) return;
                foreach (var component in group.Components) children.Children.Add(Row(component));
            };
            rows.Children.Add(expander); expander.IsExpanded = expanded.Contains(group.Id);
        }
    }

    private static Wpf.StackPanel Row(ServiceStatusRow row)
    {
        var body = new Wpf.StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var header = new Wpf.DockPanel { LastChildFill = true, ToolTip = Label(row.Health) };
        var uptime = Text(row.Uptime is { } percent ? $"{percent:0.00}%" : "", 11);
        uptime.ToolTip = "官方公布的区间可用率";
        Wpf.DockPanel.SetDock(uptime, Wpf.Dock.Right); header.Children.Add(uptime);
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Color(row.Health), Margin = new Thickness(0, 0, 6, 0), ToolTip = Label(row.Health) };
        Wpf.DockPanel.SetDock(dot, Wpf.Dock.Left); header.Children.Add(dot);
        var labels = new Wpf.WrapPanel();
        labels.Children.Add(Text(row.Name, 12, true));
        if (row.Components.Count > 0)
        {
            var count = Text($"{row.Components.Count} 个组件", 10);
            count.Margin = new Thickness(7, 3, 0, 0); labels.Children.Add(count);
        }
        header.Children.Add(labels); body.Children.Add(header);
        if (row.Days.Count > 0)
        {
            var bars = new Wpf.Primitives.UniformGrid { Rows = 1, Height = 16, Margin = new Thickness(0, 5, 0, 0) };
            foreach (var day in row.Days)
            {
                var tooltip = $"{day.Date:yyyy-MM-dd} UTC · {Label(day.Health)}" +
                    (day.Incidents.Count > 0 ? "\n" + string.Join("\n", day.Incidents) : "");
                bars.Children.Add(new Wpf.Border { Background = Color(day.Health), CornerRadius = new CornerRadius(.8),
                    Margin = new Thickness(0, 0, 1, 0), ToolTip = tooltip });
            }
            body.Children.Add(bars);
            bars.ToolTip = $"{row.Days[0].Date:yyyy-MM-dd} – {row.Days[^1].Date:yyyy-MM-dd} UTC";
        }
        return body;
    }
    private static Wpf.TextBlock Text(string text, double size, bool bold = false)
    {
        var value = new Wpf.TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 2, 0, 2) };
        value.SetResourceReference(Wpf.TextBlock.ForegroundProperty, bold ? "TextPrimaryBrush" : "TextMutedBrush"); return value;
    }
    private static Media.Brush Color(ServiceHealth health) => health switch
    {
        ServiceHealth.Operational => Media.Brushes.MediumSeaGreen,
        ServiceHealth.Degraded => Media.Brushes.Goldenrod,
        ServiceHealth.PartialOutage => Media.Brushes.DarkOrange,
        ServiceHealth.Outage => Media.Brushes.IndianRed,
        ServiceHealth.Maintenance => Media.Brushes.CornflowerBlue,
        _ => Media.Brushes.Gray
    };
    private static string Label(ServiceHealth health) => health switch
    {
        ServiceHealth.Operational => "运行正常", ServiceHealth.Degraded => "性能下降",
        ServiceHealth.PartialOutage => "部分中断", ServiceHealth.Outage => "服务中断",
        ServiceHealth.Maintenance => "维护中", _ => "状态未知"
    };
}
