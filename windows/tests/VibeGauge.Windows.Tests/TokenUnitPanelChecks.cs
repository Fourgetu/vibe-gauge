using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class TokenUnitPanelChecks
{
    internal static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-unit-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose(); // Unit changes must work from cached data without starting background scans.
        MainWindow? window = null;
        try
        {
            var at = new DateTimeOffset(DateTime.Today).AddDays(-1).AddHours(12);
            var day = DateOnly.FromDateTime(at.LocalDateTime);
            var record = new InteractionRecord("record", "ZCode", "demo", at, 1200000, 1000000, 0, 34000, 2000);
            var source = new UsageSourceSummary("ZCode", UsageDataState.Available, 1, 1200000, 1000000, 0, 34000, 2000, "");
            var display = new DesktopTokenDisplay(source, source, true);
            var text = display.Format();
            var usage = new UsageSummary(1, 1200000, 1000000, 0, 34000, 2000, [source], [record]);
            var statistics = UsageStatistics.Build([record], DateTimeOffset.Now);
            var sessions = new SessionSummary([new("session", "Codex", "C:\\Demo", "demo", 20, 272000, DateTimeOffset.Now, 0)], []);
            var snapshot = new DashboardSnapshot(DateTimeOffset.Now, new(75, 8, 32, 0, 5, 100, 500, 0), ProcessReport.Empty,
                [new("ZCode", "", true, 1, ProviderDataState.Available, text.Detail, CompactDetail: text.Compact, DesktopTokens: display)],
                usage, new(UsageDataState.Available, 1, 1200000, 34000, 2000, [new("Site", 1, 1200000, 1000000, 0, 34000, 2000)], ""),
                Statistics: statistics, Sessions: sessions);
            typeof(DashboardViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [snapshot]);
            window = new MainWindow(vm, autoHide: false) { Width = 420, Height = 560, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            vm.SelectTab(2);
            Layout(window);
            var toggle = (CheckBox)window.FindName("TokenUnitToggle");
            Assert.True(toggle.IsVisible);
            Assert.False(toggle.IsChecked);
            Assert.Contains("万", vm.TotalTokensText);
            window.StatisticsView.Update(statistics);
            window.StatisticsView.SelectDate(day);
            window.StatisticsView.SetHourly(true);
            var changes = 0;
            vm.SnapshotChanged += (_, value) => { Assert.Same(snapshot, value); changes++; };

            foreach (var light in new[] { false, true })
            {
                window.SetThemeForCapture(light);
                foreach (var international in new[] { true, false })
                {
                    vm.SelectTab(2);
                    toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, international);
                    Layout(window);
                    var unit = international ? TokenUnit.International : TokenUnit.Chinese;
                    Assert.Equal(international, vm.UseInternationalTokens);
                    Assert.Equal(unit, new TokenUnitSettings(paths.LocalDataRoot).Load());
                    Assert.Equal(Formatting.Tokens(1234000, unit), vm.TotalTokensText);
                    Assert.Contains(Formatting.Tokens(1234000, unit), vm.Platforms.Single().DisplayDetail);
                    Assert.Contains(Formatting.Tokens(34000, unit), vm.ApiDetail);
                    Assert.Equal(usage.TotalTokens, snapshot.Usage.TotalTokens);
                    Assert.True(BoundsWithin(toggle, window));

                    vm.SelectTab(3);
                    Layout(window);
                    Assert.Equal(day, window.StatisticsView.SelectedDate);
                    Assert.True(window.StatisticsView.IsHourly);
                    Assert.Contains(Formatting.Tokens(1234000, unit), Texts(window.StatisticsView));
                    Assert.Equal(1234000, window.StatisticsView.CurrentPeriod.TotalTokens);
                    vm.SelectTab(0);
                    Layout(window);
                    Assert.Contains(Formatting.Tokens(272000, unit), Texts(window.PlansView));
                }
            }
            Assert.Equal(4, changes);
        }
        finally
        {
            if (window is not null)
            {
                typeof(MainWindow).GetField("allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Layout(FrameworkElement element)
    {
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
    }

    private static bool BoundsWithin(FrameworkElement element, FrameworkElement parent)
    {
        var bounds = element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= 0 && bounds.Right <= parent.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= parent.ActualHeight;
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) yield return text.Text;
            foreach (var value in Texts(child)) yield return value;
        }
    }
}
