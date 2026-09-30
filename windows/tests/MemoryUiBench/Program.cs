using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using VibeGauge.Core;
using VibeGauge.Windows;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Usage: MemoryUiBench <synthetic-home> <label> <report.jsonl>");
        var root = Path.GetFullPath(args[0]);
        if (!File.Exists(Path.Combine(root, "fixture-ready")) || root == new AppPaths().Home) throw new ArgumentException("A synthetic fixture is required.");
        var paths = new AppPaths(root, Path.Combine(root, "ui-" + args[1]));
        new FeaturePreferences { Language = "zh" }.Save(paths);
        using var output = new StreamWriter(args[2]) { AutoFlush = true };
        using var process = Process.GetCurrentProcess();
        var app = new App(startRuntime: false); app.InitializeComponent();
        using var proxy = new ProxyManager(paths);
        using var vm = new DashboardViewModel(new DashboardCoordinator(paths, proxy), new StartupRegistrar(), proxy, new ProxySettings());
        var window = new MainWindow(vm, autoHide: false) { Left = -20000, Top = -20000, Width = 420, Height = 860, ShowActivated = false, ShowInTaskbar = false };
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var tick = 0;
        long previousAllocations = 0;
        timer.Tick += (_, _) =>
        {
            tick++;
            if (tick == 7) vm.SelectTab(2);
            if (tick == 13) { vm.SelectTab(3); UiLocalization.SetLanguage("en"); }
            if (tick == 19) { vm.SelectTab(4); UiLocalization.SetLanguage("zh"); }
            if (tick == 25) window.Hide();
            if (tick == 31) { vm.SelectTab(0); window.Show(); }
            process.Refresh();
            var info = GC.GetGCMemoryInfo(); var allocated = GC.GetTotalAllocatedBytes();
            output.WriteLine(JsonSerializer.Serialize(new { Label = args[1], Pid = process.Id, Tick = tick, Seconds = watch.Elapsed.TotalSeconds,
                Visible = window.IsVisible, Committed = process.PrivateMemorySize64, WorkingSet = process.WorkingSet64,
                Heap = GC.GetTotalMemory(false), GcCommitted = info.TotalCommittedBytes, Allocated = allocated, AllocationDelta = allocated - previousAllocations,
                Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2), Cpu = process.TotalProcessorTime.TotalSeconds,
                vm.CallsText, vm.TotalTokensText, vm.IsRefreshing }));
            previousAllocations = allocated;
            if (tick >= 36)
            {
                timer.Stop(); vm.Dispose();
                typeof(MainWindow).GetField("allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close(); app.Shutdown();
            }
        };
        window.Show(); timer.Start();
        _ = vm.RefreshAsync(); app.Run();
        return 0;
    }
}
