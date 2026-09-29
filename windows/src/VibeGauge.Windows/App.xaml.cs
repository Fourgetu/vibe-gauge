using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;

namespace VibeGauge.Windows;

public partial class App : System.Windows.Application
{
    private readonly bool startRuntime;
    public App() : this(startRuntime: true) { }
    public App(bool startRuntime) { this.startRuntime = startRuntime; UiLocalization.Initialize(); }
    private TrayIconManager? tray;
    private Mutex? singleInstance;
    private ProxyManager? proxy;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startRuntime) return;
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole();
            try { SelfTest.Run(); Console.WriteLine("VibeGauge Windows self-test passed"); Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine(error); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--scan-json", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole();
            try
            {
                var snapshot = await new DashboardCoordinator().ScanAsync();
                Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); Shutdown(1); }
            return;
        }

        var captureUi = e.Args.Contains("--capture-ui", StringComparer.OrdinalIgnoreCase);
        singleInstance = new Mutex(initiallyOwned: true, captureUi ? @"Local\VibeGauge.Windows.Capture." + Guid.NewGuid().ToString("N") :
            @"Local\VibeGauge.Windows.Singleton", out var created);
        if (!created)
        {
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown(0);
            return;
        }

        var captureHome = e.Args.FirstOrDefault(x => x.StartsWith("--capture-home=", StringComparison.OrdinalIgnoreCase));
        var paths = captureUi && captureHome is not null
            ? new AppPaths(captureHome["--capture-home=".Length..], Path.Combine(captureHome["--capture-home=".Length..], "local"))
            : null;
        proxy = new ProxyManager(paths);
        UiLocalization.SetLanguage(FeaturePreferences.Load(paths ?? new AppPaths()).Language);
        var proxySettings = new ProxySettings();
        if (proxySettings.AutoStart && !captureUi) _ = await proxy.StartAsync();
        var viewModel = new DashboardViewModel(
            new DashboardCoordinator(paths, proxy),
            new StartupRegistrar(),
            proxy,
            proxySettings);
        var window = new MainWindow(viewModel, autoHide: !captureUi);
        if (captureUi) window.SetThemeForCapture(e.Args.Contains("--capture-light", StringComparer.OrdinalIgnoreCase));
        tray = new TrayIconManager(window, viewModel);
        viewModel.SnapshotChanged += (_, snapshot) => tray.Update(snapshot);
        await viewModel.RefreshAsync();
        if (e.Args.Contains("--tray-smoketest", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole();
            window.Hide();
            tray.ShowWindow();
            var passed = tray.IsVisible && window.IsVisible;
            Console.WriteLine(passed ? "VibeGauge tray smoke test passed" : "VibeGauge tray smoke test failed");
            Shutdown(passed ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--capture-system", StringComparer.OrdinalIgnoreCase)) viewModel.SelectTab(2);
        else if (e.Args.Contains("--capture-api", StringComparer.OrdinalIgnoreCase)) viewModel.SelectTab(1);
        else if (e.Args.Contains("--capture-stats", StringComparer.OrdinalIgnoreCase)) viewModel.SelectTab(3);
        else if (e.Args.Contains("--capture-network", StringComparer.OrdinalIgnoreCase)) viewModel.SelectTab(4);
        if (captureUi)
        {
            var rangeArgument = e.Args.FirstOrDefault(x => x.StartsWith("--capture-range=", StringComparison.OrdinalIgnoreCase));
            if (rangeArgument is not null) window.StatisticsView.SelectRange(rangeArgument["--capture-range=".Length..]);
            var dateArgument = e.Args.FirstOrDefault(x => x.StartsWith("--capture-date=", StringComparison.OrdinalIgnoreCase));
            if (dateArgument is not null && DateOnly.TryParseExact(dateArgument["--capture-date=".Length..], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                window.StatisticsView.SelectDate(date);
            if (e.Args.Contains("--capture-hourly", StringComparer.OrdinalIgnoreCase)) window.StatisticsView.SetHourly(true);
        }
        if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase)) tray.ShowWindow();
        if (captureUi && e.Args.Contains("--capture-compact", StringComparer.OrdinalIgnoreCase))
        {
            window.Width = 420; window.Height = 560;
            window.UpdateLayout();
        }
        var captureOutput = e.Args.FirstOrDefault(x => x.StartsWith("--capture-output=", StringComparison.OrdinalIgnoreCase));
        if (captureUi && e.Args.Contains("--capture-usage", StringComparer.OrdinalIgnoreCase))
        {
            viewModel.SelectTab(0);
            window.UpdateLayout();
            window.PlansView.ScrollToUsageForCapture();
        }
        if (captureUi && captureOutput is not null)
            _ = CaptureWindowAsync(window, captureOutput["--capture-output=".Length..],
                e.Args.Contains("--capture-composited", StringComparer.OrdinalIgnoreCase),
                e.Args.Contains("--verify-window", StringComparer.OrdinalIgnoreCase) ? () => WindowDiagnostics.Verify(window, tray, viewModel.Paths.LocalDataRoot) : null,
                e.Args.FirstOrDefault(x => x.StartsWith("--capture-tooltip=", StringComparison.OrdinalIgnoreCase))?["--capture-tooltip=".Length..],
                e.Args.Contains("--verify-edge-animation", StringComparer.OrdinalIgnoreCase));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        if (proxy is not null)
        {
            try { Task.Run(proxy.StopOwnedAsync).GetAwaiter().GetResult(); } catch { }
            proxy.Dispose();
        }
        if (singleInstance is not null)
        {
            try { singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    private static void AttachConsole()
    {
        if (NativeMethods.AttachConsole(-1))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    private static async Task CaptureWindowAsync(MainWindow window, string outputPath, bool composited, Func<object>? verify, string? tooltip, bool verifyEdgeAnimation)
    {
        try
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(500);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            if (verifyEdgeAnimation)
            {
                var animation = await EdgeAnimationDiagnostics.VerifyAsync(window);
                File.WriteAllText(outputPath + ".animation.json", JsonSerializer.Serialize(animation, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (tooltip is not null)
            {
                var checks = await TooltipDiagnostics.CaptureAsync(window, tooltip, outputPath);
                File.WriteAllText(outputPath + ".checks.json", JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            if (verify is not null)
                File.WriteAllText(outputPath + ".checks.json", JsonSerializer.Serialize(verify(), new JsonSerializerOptions { WriteIndented = true }));
            if (composited)
            {
                window.Activate();
                await Task.Delay(400);
                WindowDiagnostics.CaptureCompositedWindow(window, outputPath);
                return;
            }
            var dpi = VisualTreeHelper.GetDpi(window);
            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var stream = File.Create(outputPath);
            encoder.Save(stream);
        }
        catch (Exception error)
        {
            File.WriteAllText(outputPath + ".error.txt", error.ToString());
        }
        finally { window.ExitApplication(); }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AttachConsole(int processId);
    }
}
