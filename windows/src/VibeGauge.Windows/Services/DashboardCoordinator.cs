using VibeGauge.Core;

namespace VibeGauge.Windows.Services;

public sealed class DashboardCoordinator
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
    public AppPaths Paths { get; }

    public DashboardCoordinator(AppPaths? paths = null, ProxyManager? proxy = null)
    {
        paths ??= new AppPaths();
        Paths = paths;
        quotas = new(paths);
        usage = new(paths);
        sampler = new(paths);
        sessions = new(paths);
        local = new(paths);
        official = new(paths);
        this.proxy = proxy;
    }

    public async Task<DashboardSnapshot> ScanAsync()
    {
        var scanTask = Task.Run(() =>
        {
            var windows = system.Scan();
            var usageSnapshot = usage.Scan();
            var platforms = quotas.Scan(windows.Processes, usageSnapshot.Cli, usageSnapshot.PiDesktopTotal);
            var sessionSnapshot = sessions.Scan(DateTimeOffset.Now);
            var adapters = network.Scan();
            return (windows, usageSnapshot, platforms, sessionSnapshot, adapters);
        });
        var proxyTask = proxy?.GetStatusAsync() ?? Task.FromResult(ProxyRuntimeStatus.Stopped);
        var localTask = Task.Run(local.ScanAsync);
        var result = await scanTask;
        return new DashboardSnapshot(
            DateTimeOffset.Now,
            result.windows.Metrics,
            result.windows.Processes,
            sampler.Apply(result.platforms.Concat(await localTask).Concat(official.Scan()).ToArray(), DateTimeOffset.Now),
            result.usageSnapshot.Cli,
            result.usageSnapshot.Api,
            await proxyTask,
            result.usageSnapshot.Statistics,
            result.sessionSnapshot,
            result.adapters);
    }

    public Task<CleanupResult> CleanAsync(IReadOnlyList<OrphanProcess> targets) => Task.Run(() => system.Clean(targets));
    public void InvalidateOfficial() => official.Invalidate();
}
