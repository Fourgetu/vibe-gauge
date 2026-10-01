using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class ProjectPanelChecks
{
    internal static void Verify()
    {
        var panel = new ProjectUsagePanel();
        ProjectUsage[] projects = [new("C:/Work/VibeGauge", 12, 2000000, 50000, 0, ["Claude", "Codex"]),
            new("D:/Archive/VibeGauge", 4, 1000000, 10000, 0, ["Codex"]), new("C:/Apps/网站界面调整", 3, 500000, 10000, 0, ["Claude"]),
            new("C:/Apps/Fourth", 1, 100000, 1000, 0, ["Codex"]), new("", 1, 50000, 1000, 0, ["Claude"])];
        var window = new Window { Content = panel, Width = 385, Height = 600, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, Padding = new Thickness(14), WindowStyle = WindowStyle.None };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        try
        {
            panel.Update(projects, TokenUnit.International); window.Show(); Layout(window);
            Assert.Equal(3, Elements<ProgressBar>(panel).Count());
            Assert.All(Elements<ProgressBar>(panel), x => Assert.Equal(2000000, x.Maximum));
            Assert.DoesNotContain(Elements<TextBlock>(panel), x => x.Text == "Fourth");
            var toggle = Elements<Button>(panel).Single(x => x.Name == "ProjectUsageToggle");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
            Assert.Equal(5, Elements<ProgressBar>(panel).Count());
            panel.Update(projects, TokenUnit.Chinese); Layout(window);
            Assert.Equal(5, Elements<ProgressBar>(panel).Count());
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
            Assert.Equal(3, Elements<ProgressBar>(panel).Count());
            foreach (var width in new[] { 385, 520 })
            foreach (var language in new[] { "zh", "en" })
            foreach (var light in new[] { false, true })
            {
                window.Width = width; UiLocalization.SetLanguage(language);
                ((ThemePalette)Application.Current.FindResource("ThemePalette")).IsLight = light; Layout(window);
                if (language == "en") Assert.Contains(Elements<TextBlock>(panel), x => x.Text.Contains("By project"));
                Capture(window, $"projects-{language}-{(light ? "light" : "dark")}-{width}.png");
            }
            panel.Update([], TokenUnit.Chinese); Layout(window); Assert.Equal(Visibility.Collapsed, toggle.Visibility);
        }
        finally { UiLocalization.SetLanguage("zh"); window.Close(); }
        VerifyAlongsideAgentUsage(projects);
    }

    private static void VerifyAlongsideAgentUsage(ProjectUsage[] projects)
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-project-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose();
        var usage = new UsageSummary(21, 3650000, 3000000, 0, 72000, 0,
            [new("Claude Code", UsageDataState.Available, 10, 1800000, 1500000, 0, 36000, 0, ""),
                new("Codex", UsageDataState.Available, 11, 1850000, 1500000, 0, 36000, 0, "")], [], Projects: projects);
        var snapshot = new DashboardSnapshot(DateTimeOffset.Now, new(50, 8, 16, 0, 1, 100, 500, 0),
            ProcessReport.Empty, [], usage, ApiUsageSummary.Empty);
        typeof(DashboardViewModel).GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [snapshot]);
        var window = new MainWindow(vm, autoHide: false) { Width = 420, Height = 740, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Layout(window); window.PlansView.ScrollToUsageForCapture(); Layout(window);
            Assert.Equal(3, Elements<ProgressBar>(window.PlansView.ProjectsView).Count());
            Assert.Contains(vm.UsageSources, x => x.Name == "Claude Code"); Assert.Contains(vm.UsageSources, x => x.Name == "Codex");
            Assert.Contains(Elements<TextBlock>(window.PlansView), x => x.Text == "Claude Code");
            Capture(window, "plans-projects-and-agents.png");
        }
        finally
        {
            typeof(MainWindow).GetField("allowClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static IEnumerable<T> Elements<T>(DependencyObject value) where T : DependencyObject => Descendants(value).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
        {
            var child = VisualTreeHelper.GetChild(value, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Layout(Window window)
    { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static void Capture(Window window, string name)
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/parity-20261001/screenshots"));
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
}
