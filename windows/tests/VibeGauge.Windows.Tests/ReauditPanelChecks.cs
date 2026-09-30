using System.IO;
using System.Reflection;
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

internal static class ReauditPanelChecks
{
    internal static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-reaudit-panels-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        var now = DateTimeOffset.Now;
        var report = new NetworkDiagnosticsReport(now, NetworkDiagnostics.Targets.Select(x =>
            new EgressInfo(x.Provider, x.Host, "", "", "", "检测失败 / 站点不支持 trace", 52, now)).Append(
            new("Gemini", "gemini.google.com", "", "", "", "未发现 Gemini 活动连接", CapturedAt: now)).ToArray(), [], [], [], "fixture", "fixture");
        var service = new NetworkDiagnostics(paths, () => Task.FromResult(report));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy, service), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose();
        var snapshot = new DashboardSnapshot(now, new(50, 8, 16, 0, 1, 100, 500, 0), ProcessReport.Empty, [], UsageSummary.Empty, ApiUsageSummary.Empty);
        typeof(DashboardViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [snapshot]);
        DashboardSnapshot? notified = null;
        vm.SnapshotChanged += (_, value) => notified = value;
        vm.RefreshDiagnosticsAsync().GetAwaiter().GetResult();
        Assert.Equal(report.Exits, notified!.Diagnostics!.Exits);
        Assert.Equal(notified, vm.CurrentSnapshot);
        var panel = new NetworkPanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 420, Height = 900, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        try
        {
            UiLocalization.SetLanguage("zh"); window.Show(); Layout(window);
            Assert.Contains("OpenAI API", Text(panel)); Assert.Contains("尚未探测", Text(panel));
            foreach (var light in new[] { false, true })
            {
                ((ThemePalette)Application.Current.FindResource("ThemePalette")).IsLight = light;
                panel.Update([], report); Layout(window);
                Assert.Contains("无有效出口结果", Text(panel)); Assert.DoesNotContain("出口一致", Text(panel));
                Assert.Contains("成功 0/4", Text(panel));
                Capture(window, light ? "network-failed-light.png" : "network-failed-dark.png");
            }
            panel.Update([], report with { Exits = report.Exits.Select((x, i) => i < 2
                ? x with { Ip = "192.0.2." + (i + 1), Error = "", Region = i == 0 ? "US" : "JP", Colo = "TEST" } : x).ToArray() });
            Layout(window); Assert.Contains("各站点出口不同", Text(panel));
            Capture(window, "network-divergent-light.png");
            UiLocalization.SetLanguage("en"); Layout(window);
            Assert.Contains("Site egress addresses differ", Text(panel));
            Assert.DoesNotMatch(@"[\u4e00-\u9fff]", Text(panel));
            Capture(window, "network-divergent-en.png");
            foreach (var text in new[] { "系统代理路径出口：192.0.2.1 · US", "IPv6 可直连：2001:db8::1（并不单独证明 DNS 泄漏）",
                "Claude 5H 已用 95% · 额度危急", "可用内存剩余 8%", "系统盘可用空间 2.0 GB",
                "Claude W 预计 30m 后打满；离重置还有 2h。这是预测，不是已耗尽。", "30m 后释放 2 次" })
                Assert.DoesNotMatch(@"[\u4e00-\u9fff]", UiLocalization.Text(text));
            UiLocalization.SetLanguage("zh");
            var rolling = new QuotaWindow(20, now.AddMinutes(30), now, TimeSpan.FromHours(5), true, true, 2);
            var provider = new PlatformStatus("fixture · Plan", "fixture", false, 0, ProviderDataState.Available, "fixture", ExtraQuotas: [new("共享池 · 5h", rolling)]);
            var detail = new ProviderDetailPanel(); window.Content = detail;
            detail.Update(provider, snapshot with { Platforms = [provider] }, paths, TokenUnit.Chinese); Layout(window);
            Assert.Contains("后释放 2 次", Text(detail)); Assert.DoesNotContain("重置 29m", Text(detail));
            Capture(window, "rolling-plan-light.png");
        }
        finally { UiLocalization.SetLanguage("zh"); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
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
    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static void Capture(Window window, string name)
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/reaudit-fixes-20260930/ui"));
        Directory.CreateDirectory(output);
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
}
