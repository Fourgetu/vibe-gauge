using System.Windows;
using VibeGauge.Core;
using Wpf = System.Windows.Controls;

namespace VibeGauge.Windows;

public sealed class RemoteApiUsagePanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel body = new();
    private PlatformStatus[] last = [];
    private TokenUnit unit;
    private string period = "today";
    public RemoteApiUsagePanel() { Content = body; Visibility = Visibility.Collapsed; }
    public void Update(IEnumerable<PlatformStatus> platforms, TokenUnit unit)
    {
        var values = platforms.Where(x => x.Tier == CodexProxyRsParser.Kind).ToArray();
        if (last.SequenceEqual(values) && this.unit == unit) return;
        last = values; this.unit = unit; Render();
    }
    private void Render()
    {
        Visibility = last.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var expanded = body.Children.OfType<Wpf.Border>().Select(x => x.Child).OfType<Wpf.StackPanel>()
            .SelectMany(x => x.Children.OfType<Wpf.Expander>()).Where(x => x.IsExpanded).Select(x => x.Tag).ToHashSet();
        body.Children.Clear();
        body.Children.Add(InsightUi.Text("Codex Proxy RS · 服务端统计", true));
        body.Children.Add(InsightUi.Text("独立统计当前 Key 的全部设备调用；不与本地代理或 Codex 日志相加。"));
        var controls = new Wpf.WrapPanel();
        foreach (var (key, title) in new[] { ("today", "今日"), ("30d", "近 30 天"), ("all", "已同步累计") })
        {
            var button = InsightUi.Button(title, (_, _) => { period = key; Render(); });
            button.IsEnabled = period != key; controls.Children.Add(button);
        }
        body.Children.Add(controls);
        foreach (var provider in last)
        {
            var section = new Wpf.StackPanel();
            var card = new Wpf.Border { Style = (Style)FindResource("CardStyle"), Child = section, Margin = new Thickness(0, 3, 0, 6) };
            body.Children.Add(card);
            section.Children.Add(InsightUi.Text(provider.Name, true));
            if (provider.DataState != ProviderDataState.Available)
            {
                var warning = InsightUi.Text(provider.Detail);
                warning.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "WarningBrush"); section.Children.Add(warning);
            }
            if (provider.RemoteUsage is not { } report) continue;
            var range = period == "today" ? report.Today : report.Recent;
            var metrics = period == "all" ? report.Cumulative : range.Summary;
            section.Children.Add(InsightUi.Text($"{Formatting.Tokens(metrics.Total, unit)} Token · {metrics.Requests:N0} 次", true));
            section.Children.Add(InsightUi.Text($"上下文 {Formatting.Tokens(metrics.Input, unit)} · 输出 {Formatting.Tokens(metrics.Output, unit)}"));
            section.Children.Add(InsightUi.Text($"缓存读取 {Formatting.Tokens(metrics.Cached, unit)} · 缓存写入 {Formatting.Tokens(metrics.CacheWrite, unit)} · 思考 {Formatting.Tokens(metrics.Reasoning, unit)}"));
            section.Children.Add(InsightUi.Text(metrics.CostUsd is { } cost ? $"站点计费 {cost:0.00####} USD" +
                (metrics.CostIncomplete ? " · 费用不完整" : "") : "站点未回报费用"));
            section.Children.Add(InsightUi.Text($"上次成功 {report.CapturedAt.LocalDateTime:MM-dd HH:mm:ss}"));
            if (period == "all")
                section.Children.Add(InsightUi.Text(report.Retained.Count > 0
                    ? $"已同步历史范围 {report.Retained[0].Start.UtcDateTime:yyyy-MM-dd} — {report.Recent.End.UtcDateTime:yyyy-MM-dd} UTC\n首次同步近 30 天；更早或离线期间缺失的历史不计入，非完整账单。"
                    : "暂无完整趋势可用于累计"));
            else section.Children.Add(InsightUi.Text($"范围 {range.Start:yyyy-MM-dd HH:mm zzz} — {range.End:MM-dd HH:mm zzz}"));
            if (report.Note.Length > 0) section.Children.Add(InsightUi.Text(report.Note));
            if (range.AnalyticsState.Length > 0) section.Children.Add(InsightUi.Text("服务端分析状态：" + range.AnalyticsState));
            if (report.Recent.Buckets.Count > 0)
            {
                var trend = new Wpf.Primitives.UniformGrid { Rows = 1, Height = 35, Margin = new Thickness(0, 5, 0, 5) };
                var buckets = report.Recent.Buckets.TakeLast(90).ToArray();
                var max = Math.Max(1, buckets.Max(x => x.Metrics.Total));
                foreach (var bucket in buckets)
                {
                    var bar = new Wpf.Border { Height = Math.Max(2, 35d * bucket.Metrics.Total / max), VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 1, 0), ToolTip = $"{bucket.Label}\n{bucket.Start:O}\n{bucket.Metrics.Total:N0} Token · {bucket.Metrics.Requests:N0} 次" };
                    bar.SetResourceReference(Wpf.Border.BackgroundProperty, "AccentBrush"); trend.Children.Add(bar);
                }
                section.Children.Add(InsightUi.Text("服务端近期趋势")); section.Children.Add(trend);
            }
            var recent = new Wpf.StackPanel();
            var expander = new Wpf.Expander { Header = "近 30 天最近 20 次请求", Content = recent, Tag = provider.Name,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, IsExpanded = expanded.Contains(provider.Name) };
            expander.SetResourceReference(Wpf.Control.ForegroundProperty, "TextSecondaryBrush");
            foreach (var call in report.Calls)
                recent.Children.Add(InsightUi.Text($"{call.Model} · {call.Status}\n{call.Time} · " +
                    (call.Tokens is { } tokens ? Formatting.Tokens(tokens, unit) + " Token" : "用量未知") +
                    (call.LatencyMs is { } ms ? $" · {ms:N0} ms" : "")));
            section.Children.Add(expander);
        }
        if (IsLoaded) UiLocalization.RefreshTree(body);
    }
}
