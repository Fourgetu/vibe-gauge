using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class ClientVisibilityPanelChecks
{
    internal static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-client-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose();
        MainWindow? window = null;
        try
        {
            var sources = new[] { "WorkBuddy", "DSH Desktop" }.Select(name =>
                new UsageSourceSummary(name, UsageDataState.Available, 1, 1200000, 1000000, 0, 34000, 2000, "")).ToArray();
            var recent = sources.Select(x => new InteractionRecord(x.Name, x.Name, "demo", DateTimeOffset.Now, 1200000, 1000000, 0, 34000, 2000)).ToArray();
            var usage = new UsageSummary(2, 2400000, 2000000, 0, 68000, 4000, sources, recent);
            var providers = new QuotaScanner(paths).Scan(ProcessReport.Empty with { WorkBuddyProcesses = 1, DshProcesses = 1 },
                usage, workBuddyTotal: sources[0], dshTotal: sources[1]).Append(new PlatformStatus("我的长名称自定义站点 · 12345678", "sub2api", false, 0,
                    ProviderDataState.Available, "余额 100 USD", AlwaysShowDetail: true)).ToArray();
            var snapshot = new DashboardSnapshot(DateTimeOffset.Now, new(75, 8, 32, 0, 5, 100, 500, 0), ProcessReport.Empty,
                providers, usage, new(UsageDataState.NotDetected, 0, 0, 0, 0, [], ""));
            var apply = typeof(DashboardViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
            apply.Invoke(vm, [snapshot]);
            Assert.Equal(9, vm.Platforms.Count);
            Assert.Equal(9, vm.ClientOptions.Count);
            window = new MainWindow(vm, autoHide: false) { Width = 420, Height = 560, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            var controls = (ItemsControl)window.FindName("ClientVisibilityOptions");
            foreach (var light in new[] { false, true })
            foreach (var width in new[] { 420, 520 })
            {
                window.Width = width;
                window.SetThemeForCapture(light);
                vm.SelectTab(2);
                Layout(window);
                controls.BringIntoView();
                Layout(window);
                var toggles = Descendants<CheckBox>(controls).ToArray();
                Assert.Equal(9, toggles.Length);
                Assert.All(toggles, x => { Assert.IsType<string>(x.Content); Assert.True(x.ActualWidth > 100); });
                var toggle = toggles.Single(x => (string)x.Content == "WorkBuddy");
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Layout(window);
                Assert.DoesNotContain(vm.Platforms, x => x.Name == "WorkBuddy");
                Assert.DoesNotContain(vm.UsageSources, x => x.Name == "WorkBuddy");
                Assert.DoesNotContain(vm.Recent, x => x.Source == "WorkBuddy");
                Assert.Contains("WorkBuddy", new ClientVisibilitySettings(paths.LocalDataRoot).Load());
                Assert.Equal(Formatting.Tokens(2468000, vm.SelectedTokenUnit), vm.TotalTokensText);
                Assert.Equal("1 个活动", vm.ActiveProviderText);
                apply.Invoke(vm, [snapshot]);
                Assert.DoesNotContain(vm.Platforms, x => x.Name == "WorkBuddy");
                Capture(window, $"system-{(light ? "light" : "dark")}-{width}.png", light);
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.Equal(9, vm.Platforms.Count);
                Assert.Equal("2 个活动", vm.ActiveProviderText);
                vm.SelectTab(0);
                Layout(window);
                var borders = Descendants<Border>(window.PlansView).Where(x => x.DataContext is PlatformRow && x.ToolTip is string)
                    .ToDictionary(x => ((PlatformRow)x.DataContext).Name);
                Assert.Equal(borders["WorkBuddy"].TranslatePoint(new Point(), window.PlansView).Y,
                    borders["DSH Desktop"].TranslatePoint(new Point(), window.PlansView).Y);
                foreach (var name in new[] { "WorkBuddy", "DSH Desktop" })
                {
                    Assert.InRange(borders[name].ActualHeight, 40, 85);
                    Assert.Contains("精确总 Token", (string)borders[name].ToolTip);
                }
                Capture(window, $"plans-all-{(light ? "light" : "dark")}-{width}.png", light);
                foreach (var option in vm.ClientOptions) option.IsVisible = option.Name is "WorkBuddy" or "DSH Desktop" or "Codex";
                Layout(window);
                Assert.Equal(3, vm.Platforms.Count);
                var panel = Descendants<ProviderCardPanel>(window.PlansView).Single();
                Assert.InRange(panel.ActualHeight, 1, 220);
                Capture(window, $"plans-selected-{(light ? "light" : "dark")}-{width}.png", light);
                foreach (var option in vm.ClientOptions) option.IsVisible = false;
                Assert.True(vm.HasNoVisibleClients);
                Assert.Empty(vm.UsageSources);
                Assert.Empty(vm.Recent);
                var sessionSummary = new SessionSummary([new("s", "Codex", "C:\\Demo", "demo", 10, 100000, DateTimeOffset.Now, 0)],
                    [new("s", "attention", "Claude Code", "C:\\Demo", DateTimeOffset.Now)]);
                Assert.Empty(vm.VisibleSessions(sessionSummary)!.Active);
                Assert.Empty(vm.VisibleSessions(sessionSummary)!.Pending);
                Assert.Equal(Formatting.Tokens(2468000, vm.SelectedTokenUnit), vm.TotalTokensText);
                vm.UseInternationalTokens = !vm.UseInternationalTokens;
                Assert.Empty(vm.Platforms);
                foreach (var option in vm.ClientOptions) option.IsVisible = true;
                Assert.False(vm.HasNoVisibleClients);
                Assert.Equal(9, vm.Platforms.Count);
                Assert.Same(sessionSummary, vm.VisibleSessions(sessionSummary));
            }
            vm.ClientOptions.Single(x => x.Name == "DSH Desktop").IsVisible = false;
            using var restored = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
            restored.Dispose();
            apply.Invoke(restored, [snapshot]);
            Assert.DoesNotContain(restored.Platforms, x => x.Name == "DSH Desktop");
            Assert.False(restored.ClientOptions.Single(x => x.Name == "DSH Desktop").IsVisible);
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

    private static void Capture(FrameworkElement element, string name, bool light)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "client-visibility-ui"));
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(245, 245, 249) : Color.FromRgb(32, 33, 36)), null, new Rect(element.RenderSize));
        image.Render(backdrop);
        image.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
