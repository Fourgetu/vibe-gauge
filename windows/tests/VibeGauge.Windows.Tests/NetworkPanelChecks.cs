using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

internal static class NetworkPanelChecks
{
    internal static void Verify()
    {
        var at = DateTimeOffset.Now.AddMinutes(-2);
        var dns = NetworkHealth.Dns(["192.168.1.1"]);
        var ipv6 = NetworkHealth.Ipv6(false, true, true);
        var report = new NetworkDiagnosticsReport(at,
            NetworkDiagnostics.Targets.Select(x => new EgressInfo(x.Provider, x.Host, "203.0.113.24", "US", "SJC", "", 182, at))
                .Append(new("Gemini", "gemini.google.com", "", "", "", "", CapturedAt: at,
                    Chain: "gemini.google.com · AI → US-West-01\ngenerativelanguage.googleapis.com · Google → US-West-02")).ToArray(),
            ["代理组 AI → US-West-01", "代理组 Proxy → JP-Tokyo-02", "代理组 Google → US-West-02", "代理组 Archive → EU-Central-03",
                "gemini.google.com · AI → US-West-01", "generativelanguage.googleapis.com · Google → US-West-02"],
            ["SSID : Home-WiFi", "Tailscale：Running"], [], "api.openai.com：198.18.0.2", ipv6.Message,
            [new("Gemini", "gemini.google.com", "AI → US-West-01"), new("Gemini", "generativelanguage.googleapis.com", "Google → US-West-02")])
            { DnsSummary = dns.Message, DnsVerdict = dns.Verdict, Ipv6Verdict = ipv6.Verdict };
        NetworkAdapterInfo[] adapters = [new("Wi-Fi", "Wireless80211", "192.168.1.23 · fe80::1234", "192.168.1.1", "198.18.0.2", 2621440, 190464),
            new("Tailscale", "Tunnel", "100.64.0.8", "", "100.100.100.100", 1000, 2000)];
        var panel = new NetworkPanel(false);
        var window = new Window { Content = panel, Width = 420, Height = 850, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Padding = new Thickness(14) };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        try
        {
            UiLocalization.SetLanguage("zh"); panel.Update(adapters, report); window.Show(); Layout(window);
            var disclosures = Elements<Expander>(panel).ToArray();
            Assert.Equal(3, disclosures.Length);
            Assert.All(disclosures, x => Assert.False(x.IsExpanded));
            Assert.Equal(3, Elements<Border>(panel).Count(x => x.Name is "NetworkEgressCard" or "NetworkProxyCard" or "NetworkLocalCard"));
            Assert.Contains("203.0.113.24", VisibleText(panel));
            Assert.Contains("2 个活动连接", VisibleText(panel));
            Assert.Contains("192.168.1.1", VisibleText(panel));
            var localCard = Elements<Border>(panel).Single(x => x.Name == "NetworkLocalCard");
            var beforeRateUpdate = Elements<TextBlock>(localCard).ToArray();
            panel.Update([adapters[0] with { ReceiveBytesPerSecond = 5242880 }, adapters[1]], report); Layout(window);
            Assert.Equal(beforeRateUpdate, Elements<TextBlock>(localCard).ToArray());
            Assert.Contains("5.0 MB/s", VisibleText(localCard));
            Assert.DoesNotContain("EU-Central-03", VisibleText(panel));
            Assert.DoesNotContain("api.openai.com", VisibleText(panel));
            var scroll = (ScrollViewer)panel.Content;
            foreach (var width in new[] { 385, 520 })
            foreach (var language in new[] { "zh", "en" })
            foreach (var light in new[] { false, true })
            {
                window.Width = width; UiLocalization.SetLanguage(language);
                ((ThemePalette)Application.Current.FindResource("ThemePalette")).IsLight = light; Layout(window);
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                // Four cards now include the DNS/IPv6 summary; overflow remains vertical.
                Assert.InRange(scroll.ExtentHeight, 400, 1100 + Elements<OpenAiStatusPanel>(panel).Single().ActualHeight + 10);
                if (language == "en") Assert.DoesNotMatch(@"[\u4e00-\u9fff]", VisibleText(panel));
                Capture(window, $"network-{language}-{(light ? "light" : "dark")}-{width}.png");
                scroll.ScrollToEnd(); Layout(window);
                Capture(window, $"network-health-{language}-{(light ? "light" : "dark")}-{width}.png");
                scroll.ScrollToTop(); Layout(window);
            }
            UiLocalization.SetLanguage("zh"); window.Width = 420;
            foreach (var disclosure in disclosures) disclosure.IsExpanded = true;
            panel.Update(adapters, report with { CapturedAt = at.AddMinutes(1) }); Layout(window);
            Assert.All(disclosures, x => Assert.True(x.IsExpanded));
            Assert.Contains("EU-Central-03", VisibleText(panel));
            Assert.Contains("generativelanguage.googleapis.com", VisibleText(panel));
            Assert.Contains("100.64.0.8", VisibleText(panel));
            Assert.Contains("采集", VisibleText(panel));
            Assert.Contains("未发现 IPv6 直连", VisibleText(panel));
            var buttons = Elements<Button>(panel).ToArray();
            Assert.Contains(buttons, x => x.Name == "NetworkRefresh");
            Assert.Contains(buttons, x => Equals(x.Content, "检测出口"));
            Assert.Contains(buttons, x => Equals(x.Content, "检查 IPv6 直连"));
            UiLocalization.SetLanguage("en"); Layout(window);
            Assert.DoesNotMatch(@"[\u4e00-\u9fff]", VisibleText(panel));
            foreach (var check in new[] { NetworkHealth.Dns(["223.5.5.5"]), NetworkHealth.Dns(["198.18.0.2"]),
                NetworkHealth.Ipv6(true, false, false, "2001:db8::1"), NetworkHealth.Ipv6(true, false, false),
                NetworkHealth.Ipv6(false, false, true), NetworkHealth.Ipv6(false, true, false) })
                Assert.DoesNotMatch(@"[\u4e00-\u9fff]", UiLocalization.Text(check.Message));
        }
        finally { UiLocalization.SetLanguage("zh"); window.Close(); }
    }

    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject => Descendants(root).OfType<T>();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
    private static string VisibleText(DependencyObject root) => string.Join("\n", Elements<TextBlock>(root).Where(x => x.IsVisible).Select(x => x.Text));
    private static void Layout(Window window)
    {
        window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static void Capture(Window window, string name)
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/parity-20261001/screenshots"));
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
}
