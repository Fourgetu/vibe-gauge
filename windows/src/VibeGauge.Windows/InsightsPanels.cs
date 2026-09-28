using System.Windows;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;

namespace VibeGauge.Windows;

internal static class InsightUi
{
    internal static Wpf.TextBlock Text(string value, bool heading = false) => new()
    {
        Text = value,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 5),
        FontSize = heading ? 14 : 11.5,
        Foreground = (Media.Brush)System.Windows.Application.Current.FindResource(heading ? "TextPrimaryBrush" : "TextSecondaryBrush"),
        FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal
    };
    internal static Wpf.Button Button(string text, RoutedEventHandler click)
    {
        var button = new Wpf.Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 5, 5),
            Style = (Style)System.Windows.Application.Current.FindResource("ActionButtonStyle")
        };
        button.Click += click;
        return button;
    }
    internal static void Row(Wpf.Panel panel, string title, string detail)
    {
        panel.Children.Add(Text(title, true));
        panel.Children.Add(Text(detail));
        panel.Children.Add(new Wpf.Border { Height = 1, Background = (Media.Brush)System.Windows.Application.Current.FindResource("BorderBrush"), Margin = new Thickness(0, 5, 0, 7) });
    }
}

public sealed class StatisticsPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new() { Margin = new Thickness(0, 0, 5, 12) };
    private UsageStatistics statistics = UsageStatistics.Empty;
    private DateOnly month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateOnly weekEnd = DateOnly.FromDateTime(DateTime.Today);
    private bool hourly;
    private string range = "30d";
    public DateOnly? SelectedDate { get; private set; }
    public string SelectedRange => range;
    public bool IsHourly => hourly;
    public UsagePeriod CurrentPeriod
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var start = range switch
            {
                "today" => today,
                "7d" => today.AddDays(-6),
                "30d" => today.AddDays(-29),
                _ => statistics.Days.Select(x => x.Date).DefaultIfEmpty(today).Min()
            };
            return SelectedDate is { } date ? statistics.ForPeriod(date, date) : statistics.ForPeriod(start, today);
        }
    }
    private string RangeLabel => range switch { "today" => "今日", "7d" => "近 7 天", "30d" => "近 30 天", _ => "全部历史" };
    private string ScopeLabel => SelectedDate is { } date ? date.ToString("yyyy-MM-dd") : RangeLabel;

    public StatisticsPanel() => Content = new Wpf.ScrollViewer
    {
        Content = body,
        VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = Wpf.ScrollBarVisibility.Disabled
    };

    public void Update(UsageStatistics? value) { statistics = value ?? UsageStatistics.Empty; Render(); }
    public void RefreshTheme() => Render();

    public void SelectRange(string value)
    {
        if (value is not ("today" or "7d" or "30d" or "all")) throw new ArgumentException("Unknown statistics range.", nameof(value));
        range = value;
        SelectedDate = null;
        var today = DateOnly.FromDateTime(DateTime.Today);
        month = new(today.Year, today.Month, 1);
        weekEnd = today;
        Render();
    }

    public void SelectDate(DateOnly date)
    {
        if (date > DateOnly.FromDateTime(DateTime.Today)) return;
        SelectedDate = date;
        month = new(date.Year, date.Month, 1);
        if (date < weekEnd.AddDays(-6) || date > weekEnd) weekEnd = date;
        Render();
    }

    public void ClearDateSelection() => SelectRange(range);

    public void SetHourly(bool value)
    {
        hourly = value;
        if (value) weekEnd = SelectedDate ?? DateOnly.FromDateTime(DateTime.Today);
        Render();
    }

    private void Navigate(int direction)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (hourly)
        {
            var end = weekEnd.AddDays(direction * 7);
            weekEnd = end > today ? today : end;
        }
        else
        {
            var next = month.AddMonths(direction);
            if (next <= new DateOnly(today.Year, today.Month, 1)) month = next;
        }
        Render();
    }

    private void Render()
    {
        body.Children.Clear();
        var buttons = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal };
        foreach (var (id, label) in new[] { ("today", "今日"), ("7d", "7D"), ("30d", "30D"), ("all", "全部") })
        {
            var button = InsightUi.Button(label, (_, _) => SelectRange(id));
            button.Name = "StatsRange_" + id;
            var active = SelectedDate is null && id == range;
            button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            if (active) { button.Background = Brush("HoverBrush"); button.Foreground = Brush("AccentBrush"); }
            buttons.Children.Add(button);
        }
        body.Children.Add(buttons);
        var period = CurrentPeriod;
        var scope = new Wpf.DockPanel();
        if (SelectedDate is not null)
        {
            var back = InsightUi.Button("返回" + RangeLabel, (_, _) => ClearDateSelection());
            back.Name = "StatsBackToRange";
            Wpf.DockPanel.SetDock(back, Wpf.Dock.Right);
            scope.Children.Add(back);
        }
        var title = InsightUi.Text(ScopeLabel + " · 用量", true);
        title.Name = "StatsScopeTitle";
        scope.Children.Add(title);
        body.Children.Add(scope);
        var interval = period.Start == period.End ? period.Start.ToString("yyyy-MM-dd") : $"{period.Start:yyyy-MM-dd} 至 {period.End:yyyy-MM-dd}";
        InsightUi.Row(body, period.Start == period.End ? $"{period.Calls:N0} 次调用" : $"{period.Calls:N0} 次调用 · {period.ActiveDays} 个活跃日",
            $"{interval}\n上下文 {Formatting.Tokens(period.Context)} · 输出 {Formatting.Tokens(period.Output)}\n缓存读取 {Formatting.Tokens(period.CacheRead)} · 思考 {Formatting.Tokens(period.Thinking)}");
        var cache = InsightUi.Text(period.CacheHitRate is { } hit ? $"缓存命中 {hit:0.0}% · 缓存写入 {Formatting.Tokens(period.CacheWrite)}" : "缓存命中 —");
        cache.ToolTip = "上下文包含缓存读取和缓存写入；思考 token 已包含在输出中，不重复相加。";
        body.Children.Add(cache);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var navigation = new Wpf.DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        var previous = InsightUi.Button("‹", (_, _) => Navigate(-1));
        previous.Name = "StatsPrevious";
        previous.ToolTip = hourly ? "前 7 天" : "上个月";
        var next = InsightUi.Button("›", (_, _) => Navigate(1));
        next.Name = "StatsNext";
        next.ToolTip = hourly ? "后 7 天" : "下个月";
        next.IsEnabled = hourly ? weekEnd < today : month < new DateOnly(today.Year, today.Month, 1);
        navigation.Children.Add(previous);
        navigation.Children.Add(next);
        var modes = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal };
        modes.Children.Add(ModeButton("\uE787", "月历", false));
        modes.Children.Add(ModeButton("\uE9D9", "按小时", true));
        Wpf.DockPanel.SetDock(modes, Wpf.Dock.Right);
        navigation.Children.Add(modes);
        var calendarTitle = InsightUi.Text(hourly
            ? $"{weekEnd.AddDays(-6):yyyy-MM-dd} 至 {weekEnd:MM-dd} · 小时"
            : month.ToString("yyyy 年 MM 月"));
        calendarTitle.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        calendarTitle.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(calendarTitle);
        body.Children.Add(navigation);
        body.Children.Add(hourly ? HourGrid() : Calendar());
        var modelTitle = InsightUi.Text(ScopeLabel + " · 模型分布", true);
        modelTitle.Name = "StatsModelTitle";
        body.Children.Add(modelTitle);
        body.Children.Add(InsightUi.Text("上下文 token 占比 · API 代理用量单独统计"));
        if (period.Models.Count == 0)
        {
            var empty = InsightUi.Text(SelectedDate is not null ? "所选日期暂无本地用量记录。" : "所选范围暂无本地用量记录。");
            empty.Name = "StatsEmpty";
            body.Children.Add(empty);
        }
        foreach (var model in period.Models)
        {
            var share = period.Context > 0 ? model.Context * 100d / period.Context : 0;
            var header = new Wpf.DockPanel();
            var percent = InsightUi.Text(period.Context > 0 ? $"{share:0.0}%" : "—");
            percent.Margin = new Thickness(8, 3, 0, 5);
            Wpf.DockPanel.SetDock(percent, Wpf.Dock.Right);
            header.Children.Add(percent);
            var name = InsightUi.Text($"{model.Source} · {model.Model}", true);
            name.Tag = model;
            header.Children.Add(name);
            body.Children.Add(header);
            body.Children.Add(new Wpf.ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = share,
                Height = 4,
                Foreground = Brush("GoodBrush"),
                Background = Brush("TrackBrush"),
                Style = (Style)System.Windows.Application.Current.FindResource("CompactProgressStyle"),
                BorderThickness = new Thickness(0)
            });
            var metrics = InsightUi.Text($"{model.Calls:N0} 次 · 上下文 {Formatting.Tokens(model.Context)} · 输出 {Formatting.Tokens(model.Output)}");
            metrics.ToolTip = $"缓存读取 {Formatting.Tokens(model.CacheRead)}\n缓存写入 {Formatting.Tokens(model.CacheWrite)}\n思考 {Formatting.Tokens(model.Thinking)}";
            body.Children.Add(metrics);
        }
    }

    private Wpf.Button ModeButton(string glyph, string label, bool value)
    {
        var button = new Wpf.Button
        {
            Name = value ? "StatsHourlyMode" : "StatsCalendarMode",
            Content = new Wpf.TextBlock
            {
                Text = glyph,
                FontFamily = new Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = Brush(value == hourly ? "AccentBrush" : "TextSecondaryBrush"),
                Margin = new Thickness(0)
            },
            Style = (Style)System.Windows.Application.Current.FindResource("IconButtonStyle"),
            ToolTip = label,
            Margin = new Thickness(2, 0, 0, 5)
        };
        if (value == hourly) { button.Background = Brush("HoverBrush"); button.Foreground = Brush("AccentBrush"); }
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => SetHourly(value);
        return button;
    }

    private Wpf.Button DayButton(DateOnly date, string name, string label, Media.Brush background, string tooltip)
    {
        var button = new Wpf.Button
        {
            Name = name,
            Tag = date,
            Content = label,
            Style = (Style)System.Windows.Application.Current.FindResource("HeatmapButtonStyle"),
            Background = background,
            BorderBrush = SelectedDate == date ? Brush("AccentBrush") : Media.Brushes.Transparent,
            Foreground = Brush("TextSecondaryBrush"),
            IsEnabled = date <= DateOnly.FromDateTime(DateTime.Today),
            ToolTip = tooltip
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip.Replace('\n', ' '));
        button.Click += (_, _) => SelectDate(date);
        return button;
    }

    private FrameworkElement Calendar()
    {
        var grid = new Wpf.Grid { Height = 206, Margin = new Thickness(0, 5, 0, 12) };
        for (var i = 0; i < 7; i++) { grid.ColumnDefinitions.Add(new()); grid.RowDefinitions.Add(new()); }
        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var col = 0; col < 7; col++)
        {
            var header = InsightUi.Text(labels[col]); header.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            Wpf.Grid.SetColumn(header, col); grid.Children.Add(header);
        }
        var first = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));
        var lookup = statistics.Days.ToDictionary(x => x.Date);
        var maximum = Math.Max(1, statistics.Days.Where(x => x.Date.Month == month.Month && x.Date.Year == month.Year).Select(x => x.Calls).DefaultIfEmpty().Max());
        for (var i = 0; i < 42; i++)
        {
            var date = first.AddDays(i);
            var data = lookup.GetValueOrDefault(date);
            var active = date.Month == month.Month && date.Year == month.Year;
            var cell = DayButton(date, $"StatsDay{date:yyyyMMdd}", date.Day.ToString(), Heat(active ? data?.Calls ?? 0 : 0, maximum),
                $"{date:yyyy-MM-dd}\n调用 {data?.Calls ?? 0:N0} 次\n上下文 {Formatting.Tokens(data?.Context ?? 0)}");
            cell.Margin = new Thickness(2);
            cell.Opacity = active ? 1 : .45;
            cell.Foreground = !active || (data?.Calls ?? 0) == 0 ? Brush("TextSecondaryBrush")
                : (data!.Calls / (double)maximum >= .25 ? new Media.SolidColorBrush(Media.Color.FromRgb(15, 40, 26)) : Media.Brushes.White);
            Wpf.Grid.SetRow(cell, i / 7 + 1); Wpf.Grid.SetColumn(cell, i % 7); grid.Children.Add(cell);
        }
        return grid;
    }

    private FrameworkElement HourGrid()
    {
        var grid = new Wpf.Grid { Height = 206, Margin = new Thickness(0, 5, 0, 12) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(42) });
        for (var i = 0; i < 24; i++) grid.ColumnDefinitions.Add(new());
        for (var i = 0; i < 8; i++) grid.RowDefinitions.Add(new());
        for (var h = 0; h <= 24; h += 6)
        {
            var text = InsightUi.Text(h.ToString());
            Wpf.Grid.SetColumn(text, Math.Min(24, h + 1));
            grid.Children.Add(text);
        }
        var lookup = statistics.Hours.Where(x => x.Date >= weekEnd.AddDays(-6) && x.Date <= weekEnd)
            .ToDictionary(x => (x.Date, x.Hour), x => x.Calls);
        var maximum = Math.Max(1, lookup.Values.DefaultIfEmpty().Max());
        for (var d = 0; d < 7; d++)
        {
            var date = weekEnd.AddDays(d - 6);
            var label = DayButton(date, $"StatsHourDay{date:yyyyMMdd}", date.ToString("MM/dd"), Brush("TransparentBrush"), date.ToString("yyyy-MM-dd"));
            label.Margin = new Thickness(0, 1, 2, 1);
            Wpf.Grid.SetRow(label, d + 1); grid.Children.Add(label);
            for (var h = 0; h < 24; h++)
            {
                var count = lookup.GetValueOrDefault((date, h));
                var cell = DayButton(date, $"StatsHour{date:yyyyMMdd}_{h:00}", "", Heat(count, maximum),
                    $"{date:yyyy-MM-dd} {h:00}:00–{h + 1:00}:00\n调用 {count:N0} 次");
                cell.BorderBrush = Media.Brushes.Transparent;
                cell.Margin = new Thickness(1, 3, 1, 3);
                Wpf.Grid.SetColumn(cell, h + 1); Wpf.Grid.SetRow(cell, d + 1); grid.Children.Add(cell);
            }
        }
        return grid;
    }
    private static Media.Brush Heat(int count, int max) => count == 0 ? (Media.Brush)System.Windows.Application.Current.FindResource("HeatZeroBrush") :
        new Media.SolidColorBrush(Media.Color.FromRgb(38, (byte)(90 + 105 * Math.Sqrt(count / (double)max)), 112));
    private static Media.Brush Brush(string name) => (Media.Brush)System.Windows.Application.Current.FindResource(name);
}

public sealed class SessionsPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new();
    public SessionsPanel() => Content = body;
    public void Update(SessionSummary? summary)
    {
        body.Children.Clear();
        if (summary is null || summary.Active.Count == 0 && summary.Pending.Count == 0) return;
        if (summary.Pending.Count > 0)
        {
            Heading("待处理", $"{summary.Pending.Count} 个会话", "WarningBrush");
            foreach (var pending in summary.Pending.Take(6))
            {
                var row = new Wpf.DockPanel { Margin = new Thickness(0, 3, 0, 3), ToolTip = pending.Directory };
                var age = Math.Max(1, (int)(DateTimeOffset.Now - pending.Since).TotalMinutes);
                var time = InsightUi.Text($"{age} 分钟前"); Wpf.DockPanel.SetDock(time, Wpf.Dock.Right); row.Children.Add(time);
                var name = InsightUi.Text(ProjectName(pending.Directory), true); name.Margin = new Thickness(0, 0, 7, 0);
                Wpf.DockPanel.SetDock(name, Wpf.Dock.Left); row.Children.Add(name);
                var kind = pending.Kind is "permission" or "等待批准" ? "等待批准" : "等待输入";
                row.Children.Add(InsightUi.Text($"{kind} · {pending.Tool}")); body.Children.Add(row);
            }
        }
        if (summary.Active.Count > 0) Heading("会话上下文", $"近 2 小时活跃 {summary.Active.Count} 个", "TextSecondaryBrush");
        foreach (var session in summary.Active.Take(8))
        {
            var row = new Wpf.Grid { Margin = new Thickness(0, 4, 0, 2), ToolTip = session.Directory };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(75) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(43) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(38) });
            var label = new Wpf.StackPanel();
            var line = new Wpf.TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5 };
            line.Inlines.Add(new System.Windows.Documents.Run(ProjectName(session.Directory)) { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new System.Windows.Documents.Run($"  {session.Tool} · {Formatting.ModelDisplayName(session.Model)}") { Foreground = Brush("TextSecondaryBrush") });
            label.Children.Add(line);
            if (session.Compactions > 0) label.Children.Add(InsightUi.Text($"今日压缩 {session.Compactions} 次"));
            row.Children.Add(label);
            var meter = new Wpf.ProgressBar
            {
                Value = Math.Clamp(session.UsedPercent ?? 0, 0, 100),
                Style = (Style)System.Windows.Application.Current.FindResource("CompactProgressStyle"),
                Foreground = Brush(session.UsedPercent >= 90 ? "DangerBrush" : session.UsedPercent >= 80 ? "WarningBrush" : "AccentBrush"),
                Margin = new Thickness(0, 5, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Wpf.Grid.SetColumn(meter, 1); row.Children.Add(meter);
            var pct = InsightUi.Text(session.UsedPercent is { } used ? $"{used:0}%" : "—", true);
            pct.FontSize = 12; pct.Margin = new Thickness(6, 0, 0, 0); pct.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            Wpf.Grid.SetColumn(pct, 2); row.Children.Add(pct);
            var size = InsightUi.Text(session.Window is { } window ? Formatting.Tokens(window) : "");
            size.FontSize = 10; size.Margin = new Thickness(5, 0, 0, 0); size.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            Wpf.Grid.SetColumn(size, 3); row.Children.Add(size); body.Children.Add(row);
        }
    }

    private void Heading(string text, string count, string color)
    {
        body.Children.Add(new Wpf.Border { Height = 1, Background = Brush("BorderBrush"), Margin = new Thickness(0, 3, 0, 10) });
        var header = new Wpf.DockPanel();
        var total = InsightUi.Text(count); Wpf.DockPanel.SetDock(total, Wpf.Dock.Right); header.Children.Add(total);
        var title = InsightUi.Text(text, true); title.Foreground = Brush(color); header.Children.Add(title); body.Children.Add(header);
    }
    private static Media.Brush Brush(string name) => (Media.Brush)System.Windows.Application.Current.FindResource(name);
    private static string ProjectName(string directory) => directory.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() is { Length: > 0 } name ? name : "~";
}

public sealed class NetworkPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel adapters = new();
    private readonly Wpf.TextBlock result = InsightUi.Text("出口检测仅在点击时联网，不发送账号或用量数据。");
    public NetworkPanel()
    {
        var body = new Wpf.StackPanel { Margin = new Thickness(0, 0, 5, 12) };
        body.Children.Add(InsightUi.Text("网络与代理", true));
        var buttons = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal };
        foreach (var ipv6 in new[] { false, true })
            buttons.Children.Add(InsightUi.Button(ipv6 ? "检查 IPv6 直连" : "检测出口", async (sender, _) =>
            {
                var button = (Wpf.Button)sender; button.IsEnabled = false;
                try { result.Text = await NetworkMonitor.ProbeEgressAsync(ipv6); } finally { button.IsEnabled = true; }
            }));
        body.Children.Add(buttons); body.Children.Add(result); body.Children.Add(adapters);
        Content = new Wpf.ScrollViewer { Content = body, VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Wpf.ScrollBarVisibility.Disabled };
    }
    public void Update(IReadOnlyList<NetworkAdapterInfo>? value)
    {
        adapters.Children.Clear();
        foreach (var row in value ?? [])
            InsightUi.Row(adapters, $"{row.Name} · {row.Kind}",
                $"{row.Addresses}\n网关：{row.Gateway}\nDNS：{row.Dns}\n↓ {row.ReceiveBytesPerSecond / 1024:0.0} KB/s   ↑ {row.SendBytesPerSecond / 1024:0.0} KB/s");
        if (adapters.Children.Count == 0) adapters.Children.Add(InsightUi.Text("未检测到活动网卡"));
    }
}
