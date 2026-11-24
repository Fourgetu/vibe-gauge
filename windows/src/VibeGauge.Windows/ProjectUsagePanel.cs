using System.Windows;
using System.Windows.Documents;
using VibeGauge.Core;
using Wpf = System.Windows.Controls;

namespace VibeGauge.Windows;

public sealed class ProjectUsagePanel : Wpf.UserControl
{
    private readonly Wpf.StackPanel rows = new();
    private readonly Wpf.Button toggle;
    private IReadOnlyList<ProjectUsage> projects = [];
    private TokenUnit unit;
    private bool expanded;

    public ProjectUsagePanel()
    {
        var body = new Wpf.StackPanel { Margin = new Thickness(0, 11, 0, 4) };
        var header = new Wpf.DockPanel();
        toggle = InsightUi.Button("显示全部项目", (_, _) => { expanded = !expanded; Render(); });
        toggle.Name = "ProjectUsageToggle";
        toggle.Padding = new Thickness(8, 3, 8, 3);
        toggle.FontSize = 11;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.Margin = new Thickness(6, 0, 0, 0);
        Wpf.DockPanel.SetDock(toggle, Wpf.Dock.Right);
        header.Children.Add(toggle);
        var titleBlock = new Wpf.StackPanel();
        titleBlock.Children.Add(Text("按项目 · 今日", 12.5, true));
        titleBlock.Children.Add(Text("Claude + Codex · 上下文含缓存；条形相对最大项目", 10.5, muted: true));
        header.Children.Add(titleBlock);
        body.Children.Add(header);
        rows.Name = "ProjectUsageRows";
        body.Children.Add(rows);
        Content = body;
    }

    public void Update(IReadOnlyList<ProjectUsage>? value, TokenUnit tokenUnit)
    {
        projects = value ?? []; unit = tokenUnit; Render();
    }

    private void Render()
    {
        rows.Children.Clear();
        if (projects.Count == 0) rows.Children.Add(Text("今日暂无项目用量", 11, muted: true));
        var maximum = Math.Max(1, projects.Select(x => x.ContextTokens).DefaultIfEmpty(0).Max());
        foreach (var project in expanded ? projects : projects.Take(3))
        {
            var row = new Wpf.StackPanel { Margin = new Thickness(0, 6, 0, 1), ToolTip = project.Path.Length > 0 ? project.Path : "日志未提供项目路径" };
            var heading = new Wpf.DockPanel();
            var meta = Text(string.Join("+", project.Contributors) + $" · {project.Turns} 次", 10.5, muted: true);
            meta.Margin = new Thickness(8, 0, 0, 0);
            meta.VerticalAlignment = VerticalAlignment.Center;
            Typography.SetNumeralAlignment(meta, FontNumeralAlignment.Tabular);
            Wpf.DockPanel.SetDock(meta, Wpf.Dock.Right);
            heading.Children.Add(meta);

            var amount = Text(Formatting.Tokens(project.ContextTokens, unit), 11.5, true);
            amount.Margin = new Thickness(8, 0, 0, 0);
            amount.VerticalAlignment = VerticalAlignment.Center;
            Typography.SetNumeralAlignment(amount, FontNumeralAlignment.Tabular);
            Wpf.DockPanel.SetDock(amount, Wpf.Dock.Right);
            heading.Children.Add(amount);

            var title = Text(project.DisplayName, 12, true);
            title.TextWrapping = TextWrapping.NoWrap;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(title);
            row.Children.Add(heading);

            var bar = new Wpf.ProgressBar { Maximum = maximum, Value = project.ContextTokens, Height = 4, Margin = new Thickness(0, 4, 0, 0) };
            bar.SetResourceReference(StyleProperty, "CompactProgressStyle");
            bar.SetResourceReference(Wpf.Control.ForegroundProperty, "AccentBrush");
            row.Children.Add(bar);
            rows.Children.Add(row);
        }
        toggle.Visibility = projects.Count > 3 ? Visibility.Visible : Visibility.Collapsed;
        toggle.Content = expanded ? "收起项目" : "显示全部项目";
    }

    private static Wpf.TextBlock Text(string value, double size, bool bold = false, bool muted = false)
    {
        var text = new Wpf.TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(Wpf.TextBlock.ForegroundProperty, bold ? "TextPrimaryBrush" : muted ? "TextMutedBrush" : "TextSecondaryBrush");
        return text;
    }
}
