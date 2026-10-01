using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class LightweightRefreshTests
{
    [Fact]
    public async Task HiddenAndOverlappingTicksNeverStartExtraWork()
    {
        var source = new TaskCompletionSource<int>(); var scans = 0; var applied = new List<int>();
        using var loop = new VisibleRefreshLoop<int>(() => { scans++; return source.Task; }, applied.Add);
        await loop.TickAsync(); Assert.Equal(0, scans);
        loop.SetEnabled(true); var running = loop.TickAsync();
        await loop.TickAsync(); Assert.Equal(1, scans);
        loop.SetEnabled(false); source.SetResult(1); await running;
        Assert.Empty(applied); await loop.TickAsync(); Assert.Equal(1, scans);
        source = new(); loop.SetEnabled(true); running = loop.TickAsync(); source.SetResult(2); await running;
        Assert.Equal(new[] { 2 }, applied);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FullScanTabSwitchAndDisposalDiscardLateResults(bool dispose)
    {
        var source = new TaskCompletionSource<int>(); var applied = false;
        using var loop = new VisibleRefreshLoop<int>(() => source.Task, _ => applied = true);
        loop.SetEnabled(true); var pending = loop.TickAsync();
        if (dispose) loop.Dispose(); else loop.Invalidate();
        source.SetResult(1); await pending; Assert.False(applied);
    }

    [Fact]
    public void ActivityChangesOnlyRuntimeFieldsAndRejectsReusedProcessIdentity()
    {
        var quota = new QuotaWindow(42, DateTimeOffset.Now.AddHours(1), DateTimeOffset.Now, TimeSpan.FromHours(5));
        var source = new PlatformStatus("Codex", "Pro", false, 0, ProviderDataState.Available, "quota", quota);
        var updated = DashboardViewModel.WithActivity(source, ProcessReport.Empty with { CodexSessions = 2 });
        Assert.True(updated.IsRunning); Assert.Equal(2, updated.Sessions); Assert.Same(quota, updated.FiveHour);
        Assert.Equal(source, updated with { IsRunning = false, Sessions = 0 });
        var process = new ProcessSnapshot(42, 1, "node.exe", "node codex", "", 10, DateTimeOffset.Now, 1);
        Assert.True(WindowsSystemScanner.SameActivityIdentity(process, process, process.StartedAt!.Value));
        Assert.False(WindowsSystemScanner.SameActivityIdentity(process, process, process.StartedAt!.Value.AddSeconds(10)));
    }

    [Fact]
    public void NativeActivityScanReturnsWithoutCleanupOrQuotaDiscovery()
    {
        var result = new WindowsSystemScanner().ScanActivity();
        Assert.Empty(result.Orphans); Assert.Empty(result.ProtectedReasons);
        Assert.NotNull(result.ProviderProcesses);
    }
}
