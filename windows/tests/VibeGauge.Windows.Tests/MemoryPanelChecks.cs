using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class MemoryPanelChecks
{
    internal static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-memory-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose(); // Supply deterministic snapshots without a background scan.
        var apply = typeof(DashboardViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = DateTimeOffset.Now;
        NetworkDiagnosticsReport Report(string ip) => new(now,
            [new("OpenAI API", "api.openai.com", ip, "US", "TEST", "", CapturedAt: now)], [], [], [], "fixture", "fixture");
        var snapshot = new DashboardSnapshot(now, new(50, 8, 16, 0, 1, 100, 500, 0),
            ProcessReport.Empty, [], UsageSummary.Empty, ApiUsageSummary.Empty, Diagnostics: Report("192.0.2.1"));
        apply.Invoke(vm, [snapshot]); vm.SelectTab(4);
        var window = new MainWindow(vm, autoHide: false)
        { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Layout(window);
            var panel = (NetworkPanel)window.FindName("NetworkView");
            Assert.Contains("192.0.2.1", Text(panel));
            window.Hide();
            var next = snapshot with { Diagnostics = Report("192.0.2.2") };
            apply.Invoke(vm, [next]); Layout(window);
            Assert.Same(next, vm.CurrentSnapshot);
            Assert.Contains("192.0.2.1", Text(panel));
            Assert.DoesNotContain("192.0.2.2", Text(panel));
            window.Show(); Layout(window);
            Assert.Contains("192.0.2.2", Text(panel));
            Assert.DoesNotContain("192.0.2.1", Text(panel));
            UiLocalization.SetLanguage("en"); Layout(window);
            Assert.Contains("OpenAI API", Text(panel));
            Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
        }
        finally
        {
            UiLocalization.SetLanguage("zh");
            typeof(MainWindow).GetField("allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static string Text(DependencyObject root) => string.Join("\n", Descendants(root).OfType<TextBlock>().Select(x => x.Text));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
    private static void Layout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
