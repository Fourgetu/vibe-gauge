using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class DashboardCoordinator : IDisposable
{
    private readonly WindowsSystemScanner system = new();
    private readonly QuotaScanner quotas;
    private readonly UsageScanner usage;
    private readonly ProxyManager? proxy;
    private readonly QuotaSampler sampler;
    private readonly SessionMonitor sessions;
    private readonly NetworkMonitor network = new();
    private readonly LocalServices local;
    private readonly OfficialSources official;
    private readonly NetworkDiagnostics diagnostics;
    private readonly AutoReapPolicy reaper = new();
    public CleanupResult? LastAutoCleanup { get; private set; }
    public AppPaths Paths { get; }

    public DashboardCoordinator(AppPaths? paths = null, ProxyManager? proxy = null, NetworkDiagnostics? networkDiagnostics = null)
    {
        paths ??= new AppPaths();
        Paths = paths;
        quotas = new(paths, new CodexAppServerQuotaClient(paths));
        usage = new(paths);
        sampler = new(paths);
        sessions = new(paths);
        local = new(paths);
        official = new(paths);
        diagnostics = networkDiagnostics ?? new(paths);
        this.proxy = proxy;
    }

    public async Task<DashboardSnapshot> ScanAsync()
    {
        var scanTask = Task.Run(() =>
        {
            var windows = system.Scan();
            var options = FeaturePreferences.Load(Paths);
            var targets = reaper.Evaluate(windows.Processes.Orphans, options.AutoReap,
                windows.Metrics.AvailableMemoryPercent <= Math.Clamp(options.MemoryFreeThreshold, 1, 50), DateTimeOffset.Now);
            if (targets.Count > 0) LastAutoCleanup = system.Clean(targets);
            var usageSnapshot = usage.Scan();
            var platforms = quotas.Scan(windows.Processes, usageSnapshot.Cli, usageSnapshot.PiDesktopTotal, usageSnapshot.ZCodeTotal,
                usageSnapshot.WorkBuddyTotal, usageSnapshot.DshTotal, usageSnapshot.CodexTotal);
            var sessionSnapshot = sessions.Scan(DateTimeOffset.Now);
            var adapters = network.Scan();
            return (windows, usageSnapshot, platforms, sessionSnapshot, adapters);
        });
        var proxyTask = proxy?.GetStatusAsync() ?? Task.FromResult(ProxyRuntimeStatus.Stopped);
        var result = await scanTask;
        var localTask = local.ScanAsync(result.windows.Processes.LocalRuntimes);
        return new DashboardSnapshot(
            DateTimeOffset.Now,
            result.windows.Metrics,
            result.windows.Processes,
            sampler.Apply(result.platforms.Concat(await localTask).Concat(official.Scan()).Concat(result.usageSnapshot.Plans ?? []).ToArray(), DateTimeOffset.Now),
            result.usageSnapshot.Cli,
            result.usageSnapshot.Api,
            await proxyTask,
            result.usageSnapshot.Statistics,
            result.sessionSnapshot,
            result.adapters, diagnostics.Scan());
    }

    public Task<NetworkDiagnosticsReport> RefreshDiagnosticsAsync() => diagnostics.RefreshAsync();
    internal Task<LightweightSnapshot> ScanLightweightAsync(bool activity, bool rates) => Task.Run(() =>
        new LightweightSnapshot(activity ? system.ScanActivity() : null, rates ? network.Scan() : null));

    public Task<CleanupResult> CleanAsync(IReadOnlyList<OrphanProcess> targets) => Task.Run(() => system.Clean(targets));
    public void InvalidateOfficial() { official.Invalidate(); quotas.InvalidateCodex(); }
    public void Dispose() => quotas.Dispose();
    public bool EnsureDurableHistory()
    {
        var result = usage.Scan();
        return usage.HistoryDurable && result.Cli.Error.Length == 0;
    }
}
