using System.Windows;
using System.Windows.Documents;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;

namespace VibeGauge.Windows;

internal static class InsightUi
{
    internal static Wpf.TextBlock Text(string value, bool heading = false)
    {
        var block = new Wpf.TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 5),
            FontSize = heading ? 14 : 11.5,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal
        };
        block.SetResourceReference(Wpf.TextBlock.ForegroundProperty, heading ? "TextPrimaryBrush" : "TextSecondaryBrush");
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

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

    internal static Wpf.Button IconButton(string glyph, string tooltip, RoutedEventHandler click, double fontSize = 13)
    {
        var icon = new Wpf.TextBlock
        {
            Text = glyph,
            FontFamily = (Media.FontFamily)System.Windows.Application.Current.FindResource("IconFontFamily"),
            FontSize = fontSize
        };
        var button = new Wpf.Button
        {
            Content = icon,
            ToolTip = tooltip,
            Style = (Style)System.Windows.Application.Current.FindResource("IconButtonStyle")
        };
        icon.SetBinding(Wpf.TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Wpf.Control.Foreground))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Wpf.Button), 1)
        });
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += click;
        return button;
    }

    internal static void Row(Wpf.Panel panel, string title, string detail)
    {
        panel.Children.Add(Text(title, true));
        panel.Children.Add(Text(detail));
        var divider = new Wpf.Border { Height = 1, Margin = new Thickness(0, 5, 0, 7) };
        divider.SetResourceReference(Wpf.Border.BackgroundProperty, "BorderBrush");
        panel.Children.Add(divider);
    }
}

public sealed partial class StatisticsPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new() { Margin = new Thickness(0, 0, 5, 12) };
    private UsageStatistics statistics = UsageStatistics.Empty;
    private DateOnly month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateOnly weekEnd = DateOnly.FromDateTime(DateTime.Today);
    private bool hourly;
    private string range = "30d";
    private DateOnly renderedToday;
    private TokenUnit tokenUnit;
    private string Tokens(long value) => Formatting.Tokens(value, tokenUnit);
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

    public void Update(UsageStatistics? value, TokenUnit unit = TokenUnit.Chinese)
    {
        var next = value ?? UsageStatistics.Empty;
        var unchanged = tokenUnit == unit && body.Children.Count > 0 && renderedToday == DateOnly.FromDateTime(DateTime.Today) &&
            statistics.Days.SequenceEqual(next.Days) && statistics.Models.SequenceEqual(next.Models) &&
            statistics.Hours.SequenceEqual(next.Hours) && statistics.ActivityHours.SequenceEqual(next.ActivityHours) &&
            (statistics.DailyModels ?? []).SequenceEqual(next.DailyModels ?? []) &&
            statistics.Prices?.Error == next.Prices?.Error &&
            statistics.Prices?.Estimate(CurrentPeriod.Models) == next.Prices?.Estimate(CurrentPeriod.Models);
        statistics = next;
        tokenUnit = unit;
        if (!unchanged) Render();
    }
    public void RefreshTheme() { if (body.Children.Count > 0) Render(); }

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
        renderedToday = DateOnly.FromDateTime(DateTime.Today);
        body.Children.Clear();

        var scopeRow = new Wpf.DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        var rangeRow = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal };
        foreach (var (id, label) in new[] { ("today", "今日"), ("7d", "7D"), ("30d", "30D"), ("all", "全部") })
        {
            var active = SelectedDate is null && id == range;
            var button = new Wpf.Button
            {
                Name = "StatsRange_" + id,
                Content = label,
                Tag = active ? "Selected" : null,
                Style = (Style)System.Windows.Application.Current.FindResource("SegmentButtonStyle")
            };
            button.Click += (_, _) => SelectRange(id);
            rangeRow.Children.Add(button);
        }
        var rangeHost = new Wpf.Border
        {
            Child = rangeRow,
            Style = (Style)System.Windows.Application.Current.FindResource("SegmentedHostStyle"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Wpf.DockPanel.SetDock(rangeHost, Wpf.Dock.Right);
        scopeRow.Children.Add(rangeHost);

        var title = InsightUi.Text(ScopeLabel + " · 用量", true);
        title.Name = "StatsScopeTitle";
        title.FontFamily = (Media.FontFamily)System.Windows.Application.Current.FindResource("DisplayFontFamily");
        title.FontSize = 14.5;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(0, 0, 8, 0);
        scopeRow.Children.Add(title);
        body.Children.Add(scopeRow);

        if (SelectedDate is not null)
        {
            var back = InsightUi.Button("返回" + RangeLabel, (_, _) => ClearDateSelection());
            back.Name = "StatsBackToRange";
            back.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            back.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(back);
        }

        var period = CurrentPeriod;
        body.Children.Add(SummaryCards(period));
        var interval = period.Start == period.End ? period.Start.ToString("yyyy-MM-dd") : $"{period.Start:yyyy-MM-dd} 至 {period.End:yyyy-MM-dd}";
        var metaLine = InsightUi.Text($"{interval} · {period.Calls:N0} 次调用");
        metaLine.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
        metaLine.FontSize = 11;
        metaLine.Margin = new Thickness(2, 4, 0, 9);
        body.Children.Add(metaLine);

        body.Children.Add(HeatmapCard());
        body.Children.Add(TokenDistribution(period));
        body.Children.Add(ModelDistribution(period));
        body.Children.Add(AgentConsumption(period));
    }

    private FrameworkElement HeatmapCard()
    {
        var content = new Wpf.StackPanel();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var navigation = new Wpf.DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        var navButtons = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var previous = InsightUi.IconButton("\uE76B", hourly ? "前 7 天" : "上个月", (_, _) => Navigate(-1), fontSize: 11);
        previous.Name = "StatsPrevious";
        var next = InsightUi.IconButton("\uE76C", hourly ? "后 7 天" : "下个月", (_, _) => Navigate(1), fontSize: 11);
        next.Name = "StatsNext";
        next.IsEnabled = hourly ? weekEnd < today : month < new DateOnly(today.Year, today.Month, 1);
        navButtons.Children.Add(previous);
        navButtons.Children.Add(next);
        Wpf.DockPanel.SetDock(navButtons, Wpf.Dock.Left);
        navigation.Children.Add(navButtons);

        var modeRow = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal };
        modeRow.Children.Add(ModeButton("\uE787", "月历", false));
        modeRow.Children.Add(ModeButton("\uE9D9", "按小时", true));
        var modeHost = new Wpf.Border
        {
            Child = modeRow,
            Style = (Style)System.Windows.Application.Current.FindResource("SegmentedHostStyle"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Wpf.DockPanel.SetDock(modeHost, Wpf.Dock.Right);
        navigation.Children.Add(modeHost);

        var calendarTitle = InsightUi.Text(hourly
            ? $"{weekEnd.AddDays(-6):MM-dd} 至 {weekEnd:MM-dd} · 按小时"
            : month.ToString("yyyy 年 MM 月"), true);
        calendarTitle.FontSize = 13;
        calendarTitle.Margin = new Thickness(4, 0, 4, 0);
        calendarTitle.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        calendarTitle.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(calendarTitle);
        content.Children.Add(navigation);

        content.Children.Add(hourly ? HourGrid() : Calendar());
        content.Children.Add(HeatmapLegend());
        return SectionCard(content, "StatsHeatmapCard");
    }

    private FrameworkElement HeatmapLegend()
    {
        var dock = new Wpf.DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var legend = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var less = InsightUi.Text("少");
        less.FontSize = 11; less.Margin = new Thickness(0, 0, 5, 0);
        less.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
        legend.Children.Add(less);
        foreach (var key in new[] { "HeatZeroBrush", "HeatLevel1Brush", "HeatLevel2Brush", "HeatLevel3Brush", "HeatLevel4Brush" })
        {
            var swatch = new Wpf.Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(1.5, 0, 1.5, 0) };
            swatch.SetResourceReference(Wpf.Border.BackgroundProperty, key);
            legend.Children.Add(swatch);
        }
        var more = InsightUi.Text("多");
        more.FontSize = 11; more.Margin = new Thickness(5, 0, 0, 0);
        more.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
        legend.Children.Add(more);
        Wpf.DockPanel.SetDock(legend, Wpf.Dock.Right);
        dock.Children.Add(legend);

        var peak = statistics.Days.Where(x => x.Date.Year == month.Year && x.Date.Month == month.Month && x.Calls > 0)
            .OrderByDescending(x => x.Calls).FirstOrDefault();
        var note = InsightUi.Text(peak is not null && !hourly ? $"高峰 {peak.Date:MM-dd} · {peak.Calls:N0} 次" : "");
        note.FontSize = 11; note.Margin = new Thickness(0);
        note.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
        dock.Children.Add(note);
        return dock;
    }

    private Wpf.Button ModeButton(string glyph, string label, bool value)
    {
        var active = value == hourly;
        var icon = new Wpf.TextBlock
        {
            Text = glyph,
            FontFamily = (Media.FontFamily)System.Windows.Application.Current.FindResource("IconFontFamily"),
            FontSize = 13,
            Margin = new Thickness(0)
        };
        icon.SetResourceReference(Wpf.TextBlock.ForegroundProperty, active ? "SelectedSegmentTextBrush" : "TextSecondaryBrush");
        var button = new Wpf.Button
        {
            Name = value ? "StatsHourlyMode" : "StatsCalendarMode",
            Content = icon,
            Width = 26,
            Height = 23,
            Style = (Style)System.Windows.Application.Current.FindResource("IconButtonStyle"),
            ToolTip = label
        };
        if (active) button.SetResourceReference(Wpf.Control.BackgroundProperty, "SelectedSegmentBrush");
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => SetHourly(value);
        return button;
    }

    private Wpf.Button DayButton(DateOnly date, string name, string label, Media.Brush background, string tooltip)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var borderBrush = SelectedDate == date
            ? Brush("AccentBrush")
            : date == today ? Brush("BorderStrongBrush") : Media.Brushes.Transparent;
        var button = new Wpf.Button
        {
            Name = name,
            Tag = date,
            Content = label,
            Style = (Style)System.Windows.Application.Current.FindResource("HeatmapButtonStyle"),
            Background = background,
            BorderBrush = borderBrush,
            Foreground = Brush("TextSecondaryBrush"),
            IsEnabled = date <= today,
            ToolTip = tooltip
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip.Replace('\n', ' '));
        button.Click += (_, _) => SelectDate(date);
        return button;
    }

    private FrameworkElement Calendar()
    {
        var grid = new Wpf.Grid { Height = 198, Margin = new Thickness(0, 2, 0, 2) };
        for (var i = 0; i < 7; i++) { grid.ColumnDefinitions.Add(new()); grid.RowDefinitions.Add(new()); }
        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var col = 0; col < 7; col++)
        {
            var header = InsightUi.Text(labels[col]);
            header.FontSize = 11.5;
            header.FontWeight = FontWeights.SemiBold;
            header.Margin = new Thickness(0, 0, 0, 3);
            header.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextSecondaryBrush");
            header.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            Wpf.Grid.SetColumn(header, col); grid.Children.Add(header);
        }
        var first = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));
        var lookup = statistics.Days.ToDictionary(x => x.Date);
        var maximum = Math.Max(1, statistics.Days.Where(x => x.Date.Month == month.Month && x.Date.Year == month.Year).Select(x => x.Calls).DefaultIfEmpty().Max());
        var isLight = ((ThemePalette)System.Windows.Application.Current.FindResource("ThemePalette")).IsLight;
        for (var i = 0; i < 42; i++)
        {
            var date = first.AddDays(i);
            var data = lookup.GetValueOrDefault(date);
            var active = date.Month == month.Month && date.Year == month.Year;
            var calls = active ? data?.Calls ?? 0 : 0;
            var level = HeatLevel(calls, maximum);
            var cell = DayButton(date, $"StatsDay{date:yyyyMMdd}", date.Day.ToString(), HeatBrush(level),
                $"{date:yyyy-MM-dd}\n调用 {data?.Calls ?? 0:N0} 次\n总 Token {Tokens(data?.TotalTokens ?? 0)}\n上下文 {Tokens(data?.Context ?? 0)} · 输出 {Tokens(data?.Output ?? 0)}");
            cell.Margin = new Thickness(2);
            // Avoid stacking opacity on top of the disabled trigger for future dates.
            cell.Opacity = active || !cell.IsEnabled ? 1 : .66;
            cell.Foreground = level == 0
                ? Brush(active ? "TextPrimaryBrush" : "TextMutedBrush")
                : isLight
                    ? (level >= 3 ? Media.Brushes.White : new Media.SolidColorBrush(Media.Color.FromRgb(14, 44, 24)))
                    : (level >= 4 ? new Media.SolidColorBrush(Media.Color.FromRgb(10, 32, 18)) : Media.Brushes.White);
            Wpf.Grid.SetRow(cell, i / 7 + 1); Wpf.Grid.SetColumn(cell, i % 7); grid.Children.Add(cell);
        }
        return grid;
    }

    private FrameworkElement HourGrid()
    {
        var grid = new Wpf.Grid { Height = 198, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(42) });
        for (var i = 0; i < 24; i++) grid.ColumnDefinitions.Add(new());
        for (var i = 0; i < 8; i++) grid.RowDefinitions.Add(new());
        for (var h = 0; h <= 24; h += 6)
        {
            var text = InsightUi.Text(h.ToString());
            text.FontSize = 10.5;
            text.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
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
                var cell = DayButton(date, $"StatsHour{date:yyyyMMdd}_{h:00}", "", HeatBrush(HeatLevel(count, maximum)),
                    $"{date:yyyy-MM-dd} {h:00}:00–{h + 1:00}:00\n调用 {count:N0} 次");
                cell.BorderBrush = Media.Brushes.Transparent;
                cell.Margin = new Thickness(1, 3, 1, 3);
                Wpf.Grid.SetColumn(cell, h + 1); Wpf.Grid.SetRow(cell, d + 1); grid.Children.Add(cell);
            }
        }
        return grid;
    }

    internal static int HeatLevel(int count, int max)
    {
        if (count <= 0) return 0;
        var ratio = count / (double)Math.Max(1, max);
        return ratio >= 0.75 ? 4 : ratio >= 0.45 ? 3 : ratio >= 0.2 ? 2 : 1;
    }

    private static Media.Brush HeatBrush(int level) => Brush(level switch
    {
        1 => "HeatLevel1Brush",
        2 => "HeatLevel2Brush",
        3 => "HeatLevel3Brush",
        4 => "HeatLevel4Brush",
        _ => "HeatZeroBrush"
    });

    private static Media.Brush Brush(string name) => (Media.Brush)System.Windows.Application.Current.FindResource(name);
}

public sealed class SessionsPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new();
    private SessionSummary? previous;
    private long previousMinute = -1;
    private TokenUnit tokenUnit;
    public SessionsPanel() => Content = body;
    public void Update(SessionSummary? summary, TokenUnit unit = TokenUnit.Chinese)
    {
        var minute = DateTimeOffset.Now.ToUnixTimeSeconds() / 60;
        if (tokenUnit == unit && previousMinute == minute && previous is not null && summary is not null &&
            previous.Active.SequenceEqual(summary.Active) && previous.Pending.SequenceEqual(summary.Pending)) return;
        previous = summary;
        tokenUnit = unit;
        previousMinute = minute;
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
        if (summary.Active.Count > 0) Heading(summary.Active.Any(x => x.RecordedTokens is not null) ? "会话用量与上下文" : "会话上下文", $"近 2 小时活跃 {summary.Active.Count} 个", "TextSecondaryBrush");
        foreach (var session in summary.Active.Take(8))
        {
            var row = new Wpf.Grid { Margin = new Thickness(0, 4, 0, 2), ToolTip = session.Directory };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(75) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(43) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = new Wpf.StackPanel();
            var line = new Wpf.TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5 };
            line.Inlines.Add(new Run(session.Tool == AntigravityUsage.SourceName ? session.Directory : ProjectName(session.Directory)) { FontWeight = FontWeights.SemiBold });
            var source = session.Model.Length > 0 ? $"{session.Tool} · {Formatting.ModelDisplayName(session.Model)}" : session.Tool;
            line.Inlines.Add(new Run("  " + source) { Foreground = Brush("TextMutedBrush") });
            label.Children.Add(line);
            if (session.Compactions > 0) label.Children.Add(InsightUi.Text($"今日压缩 {session.Compactions} 次"));
            row.Children.Add(label);
            if (session.RecordedTokens is { } recorded)
            {
                Wpf.Grid.SetColumnSpan(label, 4);
                label.Children.Add(InsightUi.Text($"已记录 {Formatting.Tokens(recorded, tokenUnit)} Token · {session.RecordedEntries} 条用量记录"));
                label.Children.Add(InsightUi.Text("累计用量，不代表当前上下文占用"));
                body.Children.Add(row);
                continue;
            }
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
            var size = InsightUi.Text(session.Window is { } window ? Formatting.Tokens(window, tokenUnit) : "");
            size.FontSize = 10.5; size.Margin = new Thickness(5, 0, 0, 0); size.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            size.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
            Wpf.Grid.SetColumn(size, 3); row.Children.Add(size); body.Children.Add(row);
        }
    }

    private void Heading(string text, string count, string color)
    {
        var divider = new Wpf.Border { Height = 1, Margin = new Thickness(0, 3, 0, 10) };
        divider.SetResourceReference(Wpf.Border.BackgroundProperty, "BorderBrush");
        body.Children.Add(divider);
        var header = new Wpf.DockPanel();
        var total = InsightUi.Text(count);
        total.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "TextMutedBrush");
        Wpf.DockPanel.SetDock(total, Wpf.Dock.Right); header.Children.Add(total);
        var title = InsightUi.Text(text, true); title.Foreground = Brush(color); header.Children.Add(title); body.Children.Add(header);
    }
    private static Media.Brush Brush(string name) => (Media.Brush)System.Windows.Application.Current.FindResource(name);
    private static string ProjectName(string directory) => directory.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() is { Length: > 0 } name ? name : "~";
}
