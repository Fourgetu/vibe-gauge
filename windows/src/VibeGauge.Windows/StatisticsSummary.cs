using System.Windows;
using VibeGauge.Core;
using Wpf = System.Windows.Controls;
using Media = System.Windows.Media;

namespace VibeGauge.Windows;

public sealed partial class StatisticsPanel
{
    private static readonly string[] MixColors = ["AccentBrush", "WarningBrush", "LocalBrush", "GoodBrush", "StatsPinkBrush", "StatsTealBrush"];
    private bool modelsExpanded;

    private FrameworkElement SummaryCards(UsagePeriod period)
    {
        var grid = new Wpf.Primitives.UniformGrid { Name = "StatsMetrics", Columns = 2, Rows = 2, Margin = new Thickness(-4, 0, -4, 0) };
        var cost = statistics.Prices?.Estimate(period.Models);
        var invalidPrices = statistics.Prices?.Error.Length > 0;
        var allUnpriced = period.Models.Count > 0 && cost?.UnpricedModels == period.Models.Select(x => x.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var costValue = invalidPrices ? "价格表无效" : cost is not { Configured: true } ? "未配置价目表"
            : allUnpriced ? "未定价" : $"{cost.Amount:N2} {cost.Currency}";
        var costDetail = cost is { Configured: true, UnpricedModels: > 0 } ? $"{cost.UnpricedModels} 个模型未定价" : "仅按本地价目表计算";
        if (statistics.Prices?.Source == "OpenRouter") costDetail = "OpenRouter 基础 Token 单价" +
            (cost is { UnpricedModels: > 0 } ? $" · {cost.UnpricedModels} 个模型未定价" : "");
        grid.Children.Add(Metric("总 Token", Tokens(period.TotalTokens), "输入（含缓存）+ 输出", "StatsTotalTokens",
            "总 Token = 上下文 + 输出；缓存已包含在上下文中，思考已包含在输出中，不重复相加。"));
        grid.Children.Add(Metric("API 等价成本", costValue, costDetail, "StatsCost",
            invalidPrices ? statistics.Prices!.Error : cost?.Description ?? "未配置 prices.json · 不估算成本"));
        grid.Children.Add(Metric("活跃天数", period.ActiveDays.ToString("N0"), "有 Token 用量的日历日", "StatsActiveDays"));
        grid.Children.Add(Metric("缓存命中率", period.CacheHitRate is { } rate ? $"{rate:0.0}%" : "—",
            "缓存读取 / 全部输入", "StatsCacheHit", "上下文包含缓存读取和缓存写入；思考 token 已包含在输出中，不重复相加。"));
        return grid;
    }

    private static Wpf.Border Metric(string title, string value, string detail, string name, string? tooltip = null)
    {
        var content = new Wpf.StackPanel();
        var label = InsightUi.Text(title); label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, 0, 4); content.Children.Add(label);
        var number = InsightUi.Text(value, true); number.Name = name; number.FontSize = 22;
        number.TextWrapping = TextWrapping.NoWrap; number.Margin = new Thickness(0);
        content.Children.Add(new Wpf.Viewbox { Child = number, Stretch = Media.Stretch.Uniform,
            StretchDirection = Wpf.StretchDirection.DownOnly, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Height = 30 });
        var caption = InsightUi.Text(detail); caption.FontSize = 10.5; caption.Margin = new Thickness(0, 4, 0, 0);
        content.Children.Add(caption);
        return new Wpf.Border { Name = name + "Card", Child = content, Background = Brush("PanelBrush"),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(4), ToolTip = tooltip };
    }

    private FrameworkElement TokenDistribution(UsagePeriod period)
    {
        var content = new Wpf.StackPanel();
        content.Children.Add(SectionHeading("Token 分布"));
        var parts = new[] { ("新输入", period.NewInput, "AccentBrush"), ("输出", period.Output, "WarningBrush"), ("缓存", period.CacheTotal, "GoodBrush") };
        content.Children.Add(StackedBar("StatsTokenBar", parts.Select(x => (x.Item2, Brush(x.Item3), x.Item1 + " " + Tokens(x.Item2))).ToArray()));
        for (var i = 0; i < parts.Length; i++)
        {
            var (label, value, color) = parts[i];
            var extra = i == 1 && period.Thinking > 0 ? $"含思考 {Tokens(period.Thinking)}" : "";
            var row = LegendRow(label, Tokens(value), Brush(color), "StatsTokenPart" + i, extra);
            row.ToolTip = i switch
            {
                0 => "新输入 = 全部输入 − 缓存读取 − 缓存写入",
                1 => "思考已包含在输出中，不重复相加。",
                _ => $"缓存读取 {Tokens(period.CacheRead)} · 缓存写入 {Tokens(period.CacheWrite)}"
            };
            content.Children.Add(row);
        }
        return SectionCard(content, "StatsTokenDistribution");
    }

    private FrameworkElement ModelDistribution(UsagePeriod period)
    {
        var content = new Wpf.StackPanel();
        var title = SectionHeading(ScopeLabel + " · 模型构成"); title.Name = "StatsModelTitle";
        content.Children.Add(title);
        var models = period.ModelComposition;
        content.Children.Add(StackedBar("StatsModelBar", models.Select((m, i) =>
            (m.TotalTokens, Brush(MixColors[i % MixColors.Length]), m.Model + " " + Tokens(m.TotalTokens))).ToArray()));
        if (models.Count == 0)
        {
            var empty = InsightUi.Text(SelectedDate is not null ? "所选日期暂无本地用量记录。" : "所选范围暂无本地用量记录。");
            empty.Name = "StatsEmpty"; content.Children.Add(empty);
        }
        var details = new Wpf.StackPanel();
        AddModelRows(details, period, models);
        content.Children.Add(details);
        if (models.Count > 2)
        {
            string ToggleLabel() => modelsExpanded ? "收起为前 2 个模型" : $"显示全部 {models.Count} 个模型";
            var toggle = InsightUi.Button(ToggleLabel(), (_, _) => { });
            toggle.Name = "StatsModelsToggle";
            toggle.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            toggle.Margin = new Thickness(0, 6, 0, 0);
            toggle.Click += (_, _) =>
            {
                modelsExpanded = !modelsExpanded;
                details.Children.Clear();
                AddModelRows(details, period, models);
                toggle.Content = ToggleLabel();
                if (!modelsExpanded) title.BringIntoView();
            };
            content.Children.Add(toggle);
        }
        var note = InsightUi.Text("按 Token 计（输入含缓存 + 输出），不是订阅额度占比。同名模型跨来源合并；API 代理用量单独统计，不重复计入。");
        note.FontSize = 10.5; note.Margin = new Thickness(0, 8, 0, 0); content.Children.Add(note);
        return SectionCard(content, "StatsModelDistribution");
    }

    private void AddModelRows(Wpf.StackPanel content, UsagePeriod period, IReadOnlyList<ModelMix> models)
    {
        for (var i = 0; i < (modelsExpanded ? models.Count : Math.Min(2, models.Count)); i++)
        {
            var model = models[i];
            var share = period.TotalTokens > 0 ? model.TotalTokens * 100d / period.TotalTokens : 0;
            var row = new Wpf.Grid { Margin = new Thickness(0, 5, 0, 0) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(48) });
            row.Children.Add(Dot(Brush(MixColors[i % MixColors.Length])));
            var name = InsightUi.Text(string.IsNullOrWhiteSpace(model.Model) || model.Model == "?" ? "未知模型" : model.Model, true);
            name.Tag = model; name.FontSize = 12; name.Margin = new Thickness(0, 0, 5, 0);
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            Wpf.Grid.SetColumn(name, 1); row.Children.Add(name);
            var amount = InsightUi.Text(Tokens(model.TotalTokens), true); amount.Name = "StatsModelTotal";
            amount.FontSize = 12; amount.Margin = new Thickness(4, 0, 0, 0);
            Wpf.Grid.SetColumn(amount, 2); row.Children.Add(amount);
            var percent = InsightUi.Text(period.TotalTokens > 0 ? $"{share:0.0}%" : "—", true);
            percent.Name = "StatsModelShare"; percent.Tag = share; percent.FontSize = 12;
            percent.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; percent.Margin = new Thickness(4, 0, 0, 0);
            Wpf.Grid.SetColumn(percent, 3); row.Children.Add(percent);
            row.ToolTip = $"{model.Model}\n{model.Source} · {model.Calls:N0} 次调用\n上下文 {Tokens(model.Context)} · 输出 {Tokens(model.Output)}\n缓存读取 {Tokens(model.CacheRead)} · 缓存写入 {Tokens(model.CacheWrite)}\n思考 {Tokens(model.Thinking)}";
            content.Children.Add(row);
            var detail = InsightUi.Text($"缓存读取 {Tokens(model.CacheRead)} · 输出 {Tokens(model.Output)}");
            detail.FontSize = 11; detail.Margin = new Thickness(12, 2, 0, 5); content.Children.Add(detail);
        }
    }

    private FrameworkElement AgentConsumption(UsagePeriod period)
    {
        var section = new Wpf.StackPanel { Name = "StatsAgents" };
        section.Children.Add(SectionHeading(ScopeLabel + " · Agent 用量"));
        var sources = period.Sources;
        foreach (var source in sources)
        {
            var content = new Wpf.StackPanel();
            var heading = new Wpf.DockPanel();
            var share = period.TotalTokens > 0 ? source.TotalTokens * 100d / period.TotalTokens : 0;
            var percent = InsightUi.Text(period.TotalTokens > 0 ? $"{share:0.0}%" : "—");
            percent.Name = "StatsAgentShare"; percent.Tag = share;
            Wpf.DockPanel.SetDock(percent, Wpf.Dock.Right); heading.Children.Add(percent);
            var name = SectionHeading(source.Name); name.Name = "StatsAgentName"; name.Tag = source;
            heading.Children.Add(name); content.Children.Add(heading);
            var total = InsightUi.Text($"总 Token {Tokens(source.TotalTokens)}", true);
            total.Name = "StatsAgentTotal"; total.FontSize = 18; total.Margin = new Thickness(0, 0, 0, 6);
            content.Children.Add(total);
            content.Children.Add(new Wpf.ProgressBar { Minimum = 0, Maximum = 100, Value = share,
                Height = 4, Margin = new Thickness(0, 0, 0, 7), Foreground = Brush("AccentBrush"),
                Background = Brush("TrackBrush"), BorderThickness = new Thickness(0),
                Style = (Style)System.Windows.Application.Current.FindResource("CompactProgressStyle") });
            var calls = InsightUi.Text($"{source.Turns:N0} 次调用"); calls.Margin = new Thickness(0, 0, 0, 4);
            content.Children.Add(calls);
            var metrics = InsightUi.Text($"输入 {Tokens(source.ContextTokens)} · 输出 {Tokens(source.OutputTokens)}\n缓存读取 {Tokens(source.CacheReadTokens)} · 缓存写入 {Tokens(source.CacheWriteTokens)}\n思考 {Tokens(source.ThinkingTokens)}");
            metrics.Margin = new Thickness(0, 0, 0, 4); content.Children.Add(metrics);
            var models = period.Models.Where(x => x.Source == source.Name).ToArray();
            var estimate = statistics.Prices?.Estimate(models);
            var costText = statistics.Prices?.Error.Length > 0 ? "价格表无效"
                : estimate is not { Configured: true } ? "未配置价目表"
                : estimate.UnpricedModels == models.Select(x => x.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count() ? "未定价"
                : $"{estimate.Amount:N2} {estimate.Currency}";
            var cost = InsightUi.Text("API 等价成本 · " + costText);
            cost.Name = "StatsAgentCost"; cost.ToolTip = statistics.Prices?.Error.Length > 0 ? statistics.Prices.Error : estimate?.Description;
            content.Children.Add(cost);
            if (estimate is { Configured: true, UnpricedModels: > 0 })
                content.Children.Add(InsightUi.Text($"{estimate.UnpricedModels} 个模型未定价"));
            section.Children.Add(SectionCard(content, "StatsAgentCard"));
        }
        if (sources.Count == 0)
        {
            var empty = InsightUi.Text(SelectedDate is not null ? "所选日期暂无本地用量记录。" : "所选范围暂无本地用量记录。");
            empty.Name = "StatsAgentsEmpty"; section.Children.Add(empty);
        }
        return section;
    }

    private static Wpf.TextBlock SectionHeading(string value)
    {
        var text = InsightUi.Text(value, true); text.Margin = new Thickness(0, 0, 0, 10); return text;
    }

    private static Wpf.Border SectionCard(Wpf.StackPanel content, string name) => new()
    {
        Name = name, Child = content, Background = Brush("PanelBrush"), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10)
    };

    private static FrameworkElement Dot(Media.Brush color) => new System.Windows.Shapes.Ellipse
    { Fill = color, Width = 6, Height = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

    private static Wpf.Grid LegendRow(string label, string value, Media.Brush color, string name, string extra)
    {
        var row = new Wpf.Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        row.Children.Add(Dot(color));
        var title = InsightUi.Text(label); title.Margin = new Thickness(0); Wpf.Grid.SetColumn(title, 1); row.Children.Add(title);
        var values = new Wpf.WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var number = InsightUi.Text(value); number.Name = name; number.Margin = new Thickness(0); number.FontWeight = FontWeights.SemiBold;
        values.Children.Add(number);
        if (extra.Length > 0)
        {
            var detail = InsightUi.Text(extra); detail.Margin = new Thickness(6, 0, 0, 0); values.Children.Add(detail);
        }
        Wpf.Grid.SetColumn(values, 2); row.Children.Add(values);
        return row;
    }

    private static Wpf.Grid StackedBar(string name, (long Value, Media.Brush Color, string Tip)[] parts)
    {
        var bar = new Wpf.Grid { Name = name, Height = 8, Margin = new Thickness(0, 0, 0, 7), Background = Brush("TrackBrush") };
        foreach (var (value, color, tip) in parts.Where(x => x.Value > 0))
        {
            var index = bar.ColumnDefinitions.Count;
            bar.ColumnDefinitions.Add(new() { Width = new GridLength(value, GridUnitType.Star) });
            var segment = new Wpf.Border { Background = color, ToolTip = tip, Tag = value };
            Wpf.Grid.SetColumn(segment, index); bar.Children.Add(segment);
        }
        bar.SizeChanged += (_, _) => bar.Clip = new Media.RectangleGeometry(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight), 4, 4);
        return bar;
    }
}
