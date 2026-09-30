using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class ProviderDetailPanelChecks
{
    internal static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-details-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        vm.Dispose();
        MainWindow? window = null;
        try
        {
            var now = DateTimeOffset.Now;
            var five = new QuotaWindow(42, now.AddHours(2), now, TimeSpan.FromHours(5));
            var week = new QuotaWindow(63, now.AddDays(3), now, TimeSpan.FromDays(7));
            var names = new[] { "Claude", "Codex", "Gemini", "ZCode", "PI-Desktop", "WorkBuddy", "DSH Desktop" };
            var sources = names.Select(name => new UsageSourceSummary(name == "Claude" ? "Claude Code" : name,
                UsageDataState.Available, 12, 1200000, 900000, 10000, 34000, 2000, "")).ToArray();
            var records = sources.Select(x => new InteractionRecord(x.Name, x.Name, "test-model-long-display-name", now,
                x.ContextTokens, x.CacheReadTokens, x.CacheWriteTokens, x.OutputTokens, x.ThinkingTokens)).ToArray();
            var providers = names.Select((name, i) => new PlatformStatus(name, i < 3 ? "Pro" : "本地", true, 1,
                ProviderDataState.Available, "已检测到本地记录", i < 3 ? five : null, i < 3 ? week : null,
                DesktopTokens: i < 3 ? null : new(sources[i], sources[i] with { Turns = 120, ContextTokens = 12000000 }, true),
                Metadata: new([new("鉴权方式", "已登录")], ProviderDetails.Sources(paths, new(name, "", false, 0, ProviderDataState.Available, ""))),
                ExtraQuotas: name == "Codex" ? [new("Codex Spark · 5 小时", five with { UsedPercent = 15 })] : null)).ToList();
            providers.Add(new("Ollama", "本地", false, 0, ProviderDataState.NotRunning, "未运行"));
            providers.Add(new("我的自定义站点 · 12345678", "sub2api", false, 0, ProviderDataState.Available, "余额 123.45 USD",
                Monthly: new(18, now.AddDays(14), now, TimeSpan.FromDays(30)), AlwaysShowDetail: true, ReportedTokens: new(654321, 98765432)));
            var sessions = new SessionSummary(names.Select((name, i) => new SessionContext(name, sources[i].Name,
                @"C:\Projects\example-workspace", "test-model", 48, 200000, now, 2)).ToArray(), []);
            var processes = ProcessReport.Empty with { ProviderProcesses = names.Select((name, i) =>
                new ProviderProcess(name, 4200 + i, now.AddMinutes(-40), 120, Path.Combine(@"C:\Apps", name, "client.exe"))).ToArray() };
            var usage = new UsageSummary(84, 8400000, 6300000, 70000, 238000, 14000, sources, records);
            var snapshot = new DashboardSnapshot(now, new(75, 8, 32, 0, 5, 100, 500, 0), processes,
                providers, usage, ApiUsageSummary.Empty, Statistics: UsageStatistics.Build(records, now), Sessions: sessions);
            var apply = typeof(DashboardViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
            apply.Invoke(vm, [snapshot]);
            window = new MainWindow(vm, autoHide: false) { Width = 420, Height = 740, Left = -20000, Top = -20000,
                ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            Layout(window);
            var panel = (ProviderDetailPanel)window.FindName("ProviderDetailView");
            var scroller = (ScrollViewer)panel.Content;
            var overview = (ScrollViewer)window.PlansView.FindName("PlansScroll");
            Button Card(string name) => Descendants<Button>(window.PlansView).Single(x => x.DataContext is PlatformRow row && row.Name == name);
            void Click(string name) { Card(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window); }
            void Back() { ((Button)window.FindName("ProviderBackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window); }

            Assert.False(Card("Ollama").IsEnabled);
            Assert.False(vm.OpenProviderDetail("Ollama"));
            Assert.False(vm.OpenProviderDetail("unknown"));
            foreach (var light in new[] { false, true })
            foreach (var width in new[] { 420, 520 })
            {
                window.Width = width;
                window.SetThemeForCapture(light);
                foreach (var name in new[] { "Codex", "WorkBuddy", "我的自定义站点 · 12345678" })
                {
                    Click(name);
                    Assert.True(vm.IsProviderDetailOpen);
                    Assert.Equal(name, vm.SelectedProviderName);
                    Assert.True(panel.IsVisible);
                    Assert.False(window.PlansView.IsVisible);
                    Assert.Equal(0, scroller.HorizontalOffset);
                    Assert.Equal(0, scroller.VerticalOffset);
                    Assert.InRange(panel.ActualWidth, 330, width);
                    Assert.Contains("订阅与状态", Text(panel));
                    if (name == "Codex")
                    {
                        Assert.Contains("Codex Spark · 5 小时", Text(panel));
                        Assert.Contains(Text(panel), x => x.StartsWith("额度回报 "));
                        Assert.DoesNotContain(Text(panel), x => x.StartsWith("采集于 "));
                        var ringColors = Descendants<System.Windows.Shapes.Path>(panel).Where(x => x.Data is PathGeometry)
                            .Select(x => ((SolidColorBrush)x.Stroke).Color).ToArray();
                        Assert.Contains(QuotaVisuals.ColorForPercent(42), ringColors);
                        Assert.Contains(QuotaVisuals.ColorForPercent(63), ringColors);
                    }
                    if (name == "WorkBuddy") Assert.Contains("今日用量 · 本地记录", Text(panel));
                    Capture(window, $"{(name == "Codex" ? "codex" : name == "WorkBuddy" ? "workbuddy" : "custom")}-{(light ? "light" : "dark")}-{width}.png", light);
                    Back();
                    Assert.True(vm.IsOverview);
                    Assert.True(window.PlansView.IsVisible);
                }
            }
            foreach (var name in new[] { "Claude", "Gemini", "PI-Desktop", "ZCode", "DSH Desktop" })
            {
                Click(name);
                Assert.Contains("数据来源", Text(panel));
                Assert.Contains("今日用量 · 本地记录", Text(panel));
                Back();
            }
            overview.ScrollToVerticalOffset(90);
            Layout(window);
            var overviewOffset = overview.VerticalOffset;
            Click("Codex");
            scroller.ScrollToVerticalOffset(200);
            Layout(window);
            var detailOffset = scroller.VerticalOffset;
            Assert.True(detailOffset > 0);
            apply.Invoke(vm, [snapshot with { CapturedAt = now.AddSeconds(1) }]);
            // Runtime dispatches this event after Apply; refresh via theme uses the same path in this fixture.
            window.SetThemeForCapture(false);
            Layout(window);
            Assert.Equal("Codex", vm.SelectedProviderName);
            Assert.Equal(detailOffset, scroller.VerticalOffset, 1);
            vm.UseInternationalTokens = true;
            Layout(window);
            Assert.Contains(Formatting.Tokens(1234000, TokenUnit.International), Text(panel));
            vm.UseInternationalTokens = false;
            Layout(window);
            Assert.Contains("123.4 万", Text(panel));
            Back();
            Assert.Equal(overviewOffset, overview.VerticalOffset, 1);
            Click("WorkBuddy");
            Assert.Equal(0, scroller.VerticalOffset);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Layout(window);
            Assert.False(vm.IsProviderDetailOpen);
            Assert.True(window.IsVisible);
            Click("Codex");
            vm.ClientOptions.Single(x => x.Name == "Codex").IsVisible = false;
            Layout(window);
            Assert.False(vm.IsProviderDetailOpen);
            vm.ClientOptions.Single(x => x.Name == "Codex").IsVisible = true;
            Click("Codex");
            vm.SelectTab(2);
            Assert.False(vm.IsProviderDetailOpen);
            vm.SelectTab(0);
            Click("Codex");
            apply.Invoke(vm, [snapshot with { Platforms = providers.Where(x => x.Name != "Codex").ToArray() }]);
            Assert.False(vm.IsProviderDetailOpen);

            var expired = providers[1] with { FiveHour = five with { ResetsAt = now.AddHours(-1) }, Weekly = null, ExtraQuotas = null };
            panel.Update(expired, snapshot, paths, TokenUnit.International);
            Assert.Contains("等待新回报", Text(panel));
            Assert.DoesNotContain("42%", Text(panel));
            Assert.DoesNotContain("消耗速率与预测", Text(panel));
            var historicalOnly = snapshot with { Usage = UsageSummary.Empty, Processes = ProcessReport.Empty,
                Platforms = [new("Codex", "未登录", false, 0, ProviderDataState.NotSignedIn, "暂无额度")], Sessions = null };
            apply.Invoke(vm, [historicalOnly]);
            Assert.True(vm.OpenProviderDetail("Codex"));
            Layout(window);
            Assert.Contains("累计用量 · 本地留存", Text(panel));
            Assert.DoesNotContain("暂无该客户端的用量记录；未记录不等于实际用量为零。", Text(panel));
            vm.CloseProviderDetail(); apply.Invoke(vm, [snapshot]); vm.SelectTab(0); overview.ScrollToTop();
            window.Width = 420; UiLocalization.SetLanguage("en");
            foreach (var light in new[] { false, true })
            {
                window.SetThemeForCapture(light); Layout(window);
                Capture(window, light ? "parity-overview-en-light-420.png" : "parity-overview-en-dark-420.png", light);
                var untranslated = Text(window.PlansView).Where(x => !x.Contains("我的自定义站点") &&
                    System.Text.RegularExpressions.Regex.IsMatch(x, @"[\u4e00-\u9fff]")).Distinct().ToArray();
                Assert.True(untranslated.Length == 0, string.Join(" | ", untranslated));
            }
            UiLocalization.SetLanguage("zh");
        }
        finally
        {
            UiLocalization.SetLanguage("zh");
            if (window is not null)
            {
                typeof(MainWindow).GetField("allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string[] Text(DependencyObject element) => Descendants<TextBlock>(element).Select(x => x.Text).ToArray();
    private static void Layout(FrameworkElement element)
    {
        element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
    }
    private static void Capture(FrameworkElement element, string name, bool light)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "provider-details-ui"));
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
