using System.Windows;
using VibeGauge.Core;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;
using Shapes = System.Windows.Shapes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace VibeGauge.Windows;

public sealed class ProviderDetailPanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new() { Margin = new Thickness(0, 0, 5, 12) };
    private readonly Wpf.ScrollViewer scroll;
    private string? providerName;
    private TokenUnit tokenUnit;
    private string Tokens(long value) => Formatting.Tokens(value, tokenUnit);
    private static Media.Brush Brush(string key) => (Media.Brush)System.Windows.Application.Current.FindResource(key);

    public ProviderDetailPanel()
    {
        Content = scroll = new Wpf.ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Wpf.ScrollBarVisibility.Disabled
        };
    }

    public void Update(PlatformStatus provider, DashboardSnapshot snapshot, AppPaths paths, TokenUnit unit)
    {
        var offset = providerName == provider.Name ? scroll.VerticalOffset : 0;
        providerName = provider.Name;
        tokenUnit = unit;
        body.Children.Clear();
        var now = DateTimeOffset.Now;
        var metadata = provider.Metadata ?? new ProviderMetadata([], ProviderDetails.Sources(paths, provider));

        var windows = ProviderDetails.Windows(provider).ToArray();
        if (windows.Length > 0) Quotas("额度窗口", windows, now);
        if (provider.ExtraQuotas is { Count: > 0 } extra)
        {
            var section = Section("模型与额外额度池");
            foreach (var quota in extra)
            {
                var trust = quota.Window.Trust(now);
                var stale = trust is "等待新回报" or "可能过期";
                var percent = quota.Window.EffectivePercent(now);
                Pair(section, quota.Label, stale ? "— · " + trust : $"{percent}% · {trust}");
                section.Children.Add(new Wpf.ProgressBar { Value = stale ? 0 : percent,
                    Style = (Style)System.Windows.Application.Current.FindResource("CompactProgressStyle"),
                    Foreground = stale ? Brush("TextMutedBrush") : QuotaVisuals.BrushForPercent(percent),
                    Margin = new Thickness(0, 0, 0, 4),
                    ToolTip = $"上次回报 {quota.Window.UsedPercent}% · 采集 {quota.Window.CapturedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}" });
                section.Children.Add(InsightUi.Text(quota.Window.ResetDescription(now)));
            }
        }
        var forecasts = windows.Concat(provider.ExtraQuotas ?? []).Select(x => (Quota: x, Burn: QuotaForecast.Calculate(x.Window, now, snapshot.Statistics?.ProfileFor(provider.Name))))
            .Where(x => x.Burn is not null).ToArray();
        if (forecasts.Length > 0)
        {
            var section = Section("消耗速率与预测");
            foreach (var (quota, burn) in forecasts)
            {
                Pair(section, quota.Label, $"{burn!.PercentPerHour:0.0}% / 小时 · {burn.Basis}");
                section.Children.Add(InsightUi.Text($"重置时预计 {burn.ProjectedPercent}% · " +
                    (burn.ExhaustAt is { } exhaust ? $"预计 {Formatting.Countdown(exhaust, now)} 后耗尽" : "当前速率下可用至重置")));
            }
            section.Children.Add(InsightUi.Text("根据已回报的额度估算，不代表额外可用额度。"));
        }

        var summary = Section("订阅与状态");
        Pair(summary, "套餐 / 类型", provider.Tier);
        Pair(summary, "客户端", provider.IsRunning ? $"运行中 · {provider.Sessions} 个根进程" : "未运行");
        if (!string.IsNullOrWhiteSpace(provider.Detail) && provider.DesktopTokens is null)
            summary.Children.Add(InsightUi.Text(provider.Detail));
        if (provider.ModelCount is { } count) Pair(summary, "已加载模型", count.ToString("N0"));
        foreach (var row in metadata.Rows) Pair(summary, row.Label, row.Value);

        AddUsage(provider, snapshot);
        var sessions = snapshot.Sessions?.Active.Where(x => ProviderDetails.Matches(provider.Name, x.Tool)).ToArray() ?? [];
        if (sessions.Length > 0)
        {
            var section = Section($"会话上下文 · {sessions.Length}");
            foreach (var session in sessions)
            {
                var title = InsightUi.Text(string.IsNullOrEmpty(session.Directory) ? "未知工作目录" : session.Directory, true);
                title.ToolTip = session.Directory;
                section.Children.Add(title);
                section.Children.Add(InsightUi.Text($"{Formatting.ModelDisplayName(session.Model)} · " +
                    (session.UsedPercent is { } percent ? $"上下文 {percent:0.#}%" : "暂无上下文比例") +
                    (session.Window is { } capacity ? $" / {Tokens(capacity)} Token" : "")));
                section.Children.Add(InsightUi.Text($"最后活动 {session.UpdatedAt.ToLocalTime():MM-dd HH:mm} · 今日压缩 {session.Compactions} 次"));
                foreach (var compact in (session.CompactionEvents ?? []).TakeLast(5))
                    section.Children.Add(InsightUi.Text($"压缩 {compact.At.ToLocalTime():HH:mm} · Token {compact.PreTokens?.ToString("N0") ?? "?"} → {compact.PostTokens?.ToString("N0") ?? "?"}"));
            }
        }
        var processes = snapshot.Processes.ProviderProcesses?.Where(x => ProviderDetails.Matches(provider.Name, x.Provider)).ToArray() ?? [];
        if (processes.Length > 0)
        {
            var section = Section($"客户端根进程 · {processes.Length}");
            foreach (var process in processes)
            {
                var age = process.StartedAt is { } started ? $"运行 {Math.Max(0, (now - started).TotalMinutes):N0} 分钟" : "启动时间未知";
                Pair(section, $"PID {process.ProcessId}", $"{process.MemoryMb:N0} MB · {age}");
                var executable = InsightUi.Text(process.Executable);
                executable.ToolTip = process.Executable;
                section.Children.Add(executable);
            }
            section.Children.Add(InsightUi.Text("仅列出识别到的根进程；内存不含全部子进程。"));
        }
        if (metadata.Sources.Count > 0)
        {
            var section = Section("数据来源");
            foreach (var source in metadata.Sources.Distinct())
            {
                var text = InsightUi.Text(source);
                text.ToolTip = source;
                section.Children.Add(text);
            }
        }
        var updated = InsightUi.Text($"最近扫描 {snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        updated.Foreground = Brush("TextMutedBrush");
        body.Children.Add(updated);
        // Reuse the scroller so live refreshes never bounce back to the top.
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(offset);
    }

    private void AddUsage(PlatformStatus provider, DashboardSnapshot snapshot)
    {
        var today = provider.DesktopTokens?.Today ?? snapshot.Usage.Sources.FirstOrDefault(x => ProviderDetails.Matches(provider.Name, x.Name));
        var hasHistory = false;
        if (today is not null) Usage("今日用量 · 本地记录", today);
        if (provider.DesktopTokens?.Total is { } total) Usage("累计用量 · 本地留存", total);
        else if (snapshot.Statistics?.DailyModels is { } history)
        {
            var models = history.Where(x => ProviderDetails.Matches(provider.Name, x.Usage.Source)).Select(x => x.Usage).ToArray();
            if (models.Length > 0)
            {
                hasHistory = true;
                Usage("累计用量 · 本地留存", new(provider.Name, UsageDataState.Available, models.Sum(x => x.Calls),
                    models.Sum(x => x.Context), models.Sum(x => x.CacheRead), models.Sum(x => x.CacheWrite),
                    models.Sum(x => x.Output), models.Sum(x => x.Thinking), ""));
            }
        }
        if (provider.Name == "Codex" && provider.DesktopTokens is not null)
            body.Children.Add(InsightUi.Text("本地 Codex 日志统计，包含账号和 API 模式历史；不代表 API 账单。"));
        if (provider.ReportedTokens is { HasValues: true } reported)
        {
            var section = Section("站点回报用量");
            var text = InsightUi.Text(reported.Format(tokenUnit), true);
            text.ToolTip = $"今日 {reported.Today?.ToString("N0") ?? "未回报"} · 累计 {reported.Total?.ToString("N0") ?? "未回报"} Token";
            section.Children.Add(text);
            section.Children.Add(InsightUi.Text("站点统计范围由供应商决定，不与本地日志重复相加。"));
        }
        var api = snapshot.Api.Providers.FirstOrDefault(x => x.Name == provider.Name);
        if (api is not null)
        {
            var section = Section("API 用量 · 今日代理日志");
            Pair(section, "总 Token", Tokens(api.TotalTokens), $"{api.TotalTokens:N0} Token");
            Pair(section, "调用 / 错误", $"{api.Calls:N0} / {api.Errors:N0}");
            Pair(section, "平均延迟", $"{api.AverageLatencyMs:N0} ms");
            Pair(section, "上下文 / 输出", $"{Tokens(api.ContextTokens)} / {Tokens(api.OutputTokens)}",
                $"上下文 {api.ContextTokens:N0} · 输出 {api.OutputTokens:N0} Token");
        }
        var mix = snapshot.Statistics?.Models.Where(x => ProviderDetails.Matches(provider.Name, x.Source)).ToArray() ?? [];
        if (mix.Length > 0)
        {
            var section = Section("今日模型分布");
            foreach (var model in mix.OrderByDescending(x => x.TotalTokens))
                Pair(section, Formatting.ModelDisplayName(model.Model), $"{Tokens(model.TotalTokens)} · {model.Calls:N0} 次",
                    $"{model.Model}\n总 Token {model.TotalTokens:N0} · 上下文 {model.Context:N0} · 输出 {model.Output:N0}");
        }
        if (today is null && !hasHistory && provider.DesktopTokens?.Total is null && api is null && provider.ReportedTokens?.HasValues != true)
            Section("Token 用量").Children.Add(InsightUi.Text("暂无该客户端的用量记录；未记录不等于实际用量为零。"));
    }

    private void Usage(string title, UsageSourceSummary source)
    {
        var section = Section(title);
        if (source.State == UsageDataState.ReadFailed && source.Note.Length > 0) section.Children.Add(InsightUi.Text(source.Note));
        if (source.Turns == 0 && source.State != UsageDataState.Available)
        {
            section.Children.Add(InsightUi.Text(source.Note.Length > 0 ? source.Note : "暂无已记录的用量"));
            return;
        }
        Pair(section, "总 Token", Tokens(source.TotalTokens), $"{source.TotalTokens:N0} Token");
        Pair(section, "调用", $"{source.Turns:N0} 次");
        foreach (var (label, value) in new[] { ("上下文", source.ContextTokens), ("输出", source.OutputTokens),
            ("缓存读取", source.CacheReadTokens), ("缓存写入", source.CacheWriteTokens), ("思考", source.ThinkingTokens) })
            Pair(section, label, Tokens(value), $"{value:N0} Token");
        if (source.CacheHitRate is { } hit) Pair(section, "缓存命中", $"{hit:0.0}%");
        section.Children.Add(InsightUi.Text("总量 = 上下文 + 输出；缓存与思考为明细，不重复相加。"));
    }

    private void Quotas(string title, IReadOnlyList<NamedQuota> quotas, DateTimeOffset now)
    {
        var section = Section(title);
        var grid = new Wpf.Primitives.UniformGrid { Columns = 2 };
        foreach (var quota in quotas)
        {
            var w = quota.Window;
            var trust = w.Trust(now);
            var stale = trust is "等待新回报" or "可能过期";
            var pct = w.EffectivePercent(now);
            var color = stale ? Brush("TextMutedBrush") : QuotaVisuals.BrushForPercent(pct);
            var column = new Wpf.StackPanel { Margin = new Thickness(3, 4, 3, 10) };
            var label = InsightUi.Text(quota.Label, true);
            label.TextAlignment = TextAlignment.Center;
            column.Children.Add(label);
            var ring = new Wpf.Grid { Width = 76, Height = 76, Margin = new Thickness(0, 3, 0, 4) };
            var track = Brush("TextSecondaryBrush").Clone();
            track.Opacity = .18;
            ring.Children.Add(new Shapes.Ellipse { Stroke = track, StrokeThickness = 7, Margin = new Thickness(.5) });
            if (!stale && pct >= 100) ring.Children.Add(new Shapes.Ellipse { Stroke = color, StrokeThickness = 7, Margin = new Thickness(.5) });
            else if (!stale)
            {
                var angle = Math.Max(.004, pct / 100d) * Math.PI * 2;
                var figure = new Media.PathFigure { StartPoint = new Point(38, 4), IsClosed = false };
                figure.Segments.Add(new Media.ArcSegment(new Point(38 + 34 * Math.Sin(angle), 38 - 34 * Math.Cos(angle)),
                    new Size(34, 34), 0, pct > 50, Media.SweepDirection.Clockwise, true));
                ring.Children.Add(new Shapes.Path { Data = new Media.PathGeometry([figure]), Stroke = color, StrokeThickness = 7,
                    StrokeStartLineCap = Media.PenLineCap.Round, StrokeEndLineCap = Media.PenLineCap.Round });
            }
            var value = InsightUi.Text(stale ? "—" : $"{pct}%", true);
            value.FontSize = 19;
            value.Foreground = stale ? Brush("TextMutedBrush") : pct > 80 ? color : Brush("TextPrimaryBrush");
            value.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.Margin = new Thickness(0);
            ring.Children.Add(value);
            ring.ToolTip = $"{quota.Label}\n{trust}\n上次回报 {w.UsedPercent}%\n采集 {w.CapturedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}\n{w.ResetDescription(now)} · {w.ResetsAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知"}";
            column.Children.Add(ring);
            foreach (var line in new[] { trust, w.ResetDescription(now),
                w.Age(now) is { } age ? $"额度回报 {Math.Max(0, age.TotalMinutes):N0} 分钟前" : "未回报采集时间" })
            {
                var text = InsightUi.Text(line);
                text.TextAlignment = TextAlignment.Center;
                text.Margin = new Thickness(0, 1, 0, 1);
                column.Children.Add(text);
            }
            grid.Children.Add(column);
        }
        section.Children.Add(grid);
    }

    private Wpf.StackPanel Section(string title)
    {
        var content = new Wpf.StackPanel();
        content.Children.Add(InsightUi.Text(title, true));
        body.Children.Add(new Wpf.Border { Background = Brush("CardBrush"), BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 9),
            Margin = new Thickness(0, 0, 0, 8), Child = content });
        return content;
    }

    private static void Pair(Wpf.Panel panel, string label, string value, string? tooltip = null)
    {
        var row = new Wpf.Grid { ToolTip = tooltip ?? $"{label}：{value}" };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        var name = InsightUi.Text(label);
        name.Margin = new Thickness(0, 3, 10, 5);
        row.Children.Add(name);
        var detail = InsightUi.Text(value);
        detail.Foreground = Brush("TextPrimaryBrush");
        detail.TextAlignment = TextAlignment.Right;
        Wpf.Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        panel.Children.Add(row);
    }
}
