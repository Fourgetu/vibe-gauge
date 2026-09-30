using System.Windows;
using System.Windows.Controls;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class StatisticsPanelChecks
{
    // Run on the existing WPF test application's STA; WPF allows one Application per process.
    internal static void Verify()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);
        var old = today.AddDays(-40);
        InteractionRecord Record(DateOnly day, string model, long context) =>
            new(model, "PI-Desktop", model, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)), context, 20, 5, 30, 10);
        var stats = UsageStatistics.Build([Record(today, "today-only", 100), Record(yesterday, "yesterday-only", 200), Record(old, "old-month-only", 500)], DateTimeOffset.Now);
        var panel = new StatisticsPanel();
        panel.Update(stats);
        Click(panel, "StatsRange_7d");
        Assert.Null(panel.SelectedDate);
        Assert.Equal(2, panel.CurrentPeriod.Calls);
        Assert.Equal(2, Models(panel).Length);
        Assert.Equal(Formatting.Tokens(360), Named<TextBlock>(panel, "StatsTotalTokens").Text);
        if (!Elements<Button>(panel).Any(x => x.Name == $"StatsDay{yesterday:yyyyMMdd}")) Click(panel, "StatsPrevious");
        Click(panel, $"StatsDay{yesterday:yyyyMMdd}");
        Assert.Equal(yesterday, panel.SelectedDate);
        Assert.False(panel.IsHourly);
        Assert.Equal("yesterday-only", Assert.Single(Models(panel)).Model);
        Assert.Equal(200, panel.CurrentPeriod.Context);
        Assert.Equal(Formatting.Tokens(230), Named<TextBlock>(panel, "StatsTotalTokens").Text);
        Assert.Equal(Formatting.Tokens(230), Named<TextBlock>(panel, "StatsModelTotal").Text);
        Assert.Contains(yesterday.ToString("yyyy-MM-dd"), Named<TextBlock>(panel, "StatsModelTitle").Text);

        panel.Update(stats);
        panel.RefreshTheme();
        Assert.Equal(yesterday, panel.SelectedDate);
        Assert.Equal("yesterday-only", Assert.Single(Models(panel)).Model);
        Click(panel, "StatsBackToRange");
        Assert.Null(panel.SelectedDate);
        Assert.Equal("7d", panel.SelectedRange);
        Assert.Equal(2, Models(panel).Length);

        panel.SelectDate(old);
        Click(panel, $"StatsDay{old:yyyyMMdd}");
        Assert.Equal("old-month-only", Assert.Single(Models(panel)).Model);
        Click(panel, "StatsHourlyMode");
        Assert.True(panel.IsHourly);
        var hour = Named<Button>(panel, $"StatsHour{old:yyyyMMdd}_00");
        Assert.Contains("调用 1 次", Assert.IsType<string>(hour.ToolTip));
        hour.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(old, panel.SelectedDate);
        Assert.Equal(500, panel.CurrentPeriod.Context);
        Assert.Equal(Formatting.Tokens(530), Named<TextBlock>(panel, "StatsTotalTokens").Text);
        Click(panel, "StatsPrevious");
        Assert.NotNull(Named<Button>(panel, $"StatsHourDay{old.AddDays(-7):yyyyMMdd}"));
        Assert.Equal(old, panel.SelectedDate);

        panel.SelectDate(today.AddDays(-3));
        Assert.Equal(0, panel.CurrentPeriod.Calls);
        Assert.Equal("0", Named<TextBlock>(panel, "StatsTotalTokens").Text);
        Assert.Empty(Models(panel));
        Assert.Contains("所选日期暂无", Named<TextBlock>(panel, "StatsEmpty").Text);
        panel.SelectDate(today.AddDays(1));
        Assert.Equal(today.AddDays(-3), panel.SelectedDate);
        Click(panel, "StatsRange_today");
        Assert.Null(panel.SelectedDate);
        Assert.Equal("today-only", Assert.Single(Models(panel)).Model);
        Click(panel, "StatsRange_all");
        Assert.Equal(2, Models(panel).Length);
        Click(panel, "StatsModelsToggle");
        Assert.Equal(3, Models(panel).Length);
        Assert.Equal(800, panel.CurrentPeriod.Context);
        Assert.Equal(Formatting.Tokens(890), Named<TextBlock>(panel, "StatsTotalTokens").Text);
        Click(panel, "StatsModelsToggle");

        var many = UsageStatistics.Build(Enumerable.Range(0, 15).Select(i => Record(today, "model-" + i, i + 50)), DateTimeOffset.Now);
        panel.Update(many);
        Assert.Equal(2, Models(panel).Length);
        Assert.Equal(panel.CurrentPeriod.TotalTokens, Named<Grid>(panel, "StatsModelBar").Children.OfType<Border>().Sum(x => (long)x.Tag));
        var summaryBeforeExpansion = Named<TextBlock>(panel, "StatsTotalTokens");
        Click(panel, "StatsModelsToggle");
        Assert.Same(summaryBeforeExpansion, Named<TextBlock>(panel, "StatsTotalTokens"));
        Assert.Equal(15, Models(panel).Length);
        Assert.Equal(panel.CurrentPeriod.Calls, Models(panel).Sum(x => x.Calls));
        Assert.Equal(panel.CurrentPeriod.TotalTokens, Models(panel).Sum(x => x.TotalTokens));
        Assert.Equal(15, Elements<TextBlock>(panel).Count(x => x.Name == "StatsModelTotal"));
        Assert.Equal(100, Elements<TextBlock>(panel).Where(x => x.Name == "StatsModelShare").Sum(x => (double)x.Tag), 8);
        Assert.Equal(panel.CurrentPeriod.TotalTokens, Named<Grid>(panel, "StatsModelBar").Children.OfType<Border>().Sum(x => (long)x.Tag));
        var unchangedHeading = Named<TextBlock>(panel, "StatsTotalTokens");
        panel.Update(many with { Days = many.Days.ToArray(), DailyModels = many.DailyModels!.ToArray() });
        Assert.Same(unchangedHeading, Named<TextBlock>(panel, "StatsTotalTokens"));
        panel.RefreshTheme();
        Assert.Equal(15, Models(panel).Length);
        Assert.NotSame(unchangedHeading, Named<TextBlock>(panel, "StatsTotalTokens"));
        Click(panel, "StatsModelsToggle");
        Assert.Equal(2, Models(panel).Length);
        Assert.Equal(panel.CurrentPeriod.TotalTokens, Elements<TextBlock>(panel).Where(x => x.Tag is UsageSourceSummary).Sum(x => ((UsageSourceSummary)x.Tag).TotalTokens));
        foreach (var width in new[] { 385, 500 })
        {
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, width, 800));
            panel.UpdateLayout();
            foreach (var button in Elements<Button>(panel).Where(x => x.Visibility == Visibility.Visible))
            {
                var bounds = button.TransformToAncestor(panel).TransformBounds(new Rect(button.RenderSize));
                Assert.InRange(bounds.Left, -1, width);
                Assert.InRange(bounds.Right, 0, width + 1);
            }
        }
    }

    private static ModelMix[] Models(StatisticsPanel panel) => Elements<TextBlock>(panel)
        .Where(x => x.Tag is ModelMix).Select(x => (ModelMix)x.Tag).ToArray();
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Elements<T>(root), x => x.Name == name);
    private static void Click(StatisticsPanel panel, string name) => Named<Button>(panel, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T value) yield return value;
            foreach (var nested in Elements<T>(child)) yield return nested;
        }
    }
}
