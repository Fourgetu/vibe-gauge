using Microsoft.Win32;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class WindowsScannerTests
{
    [Fact]
    public void OwnedQuotaHelperDoesNotIncreaseCodexSessionCount()
    {
        var processes = new Dictionary<int, ProcessSnapshot>
        {
            [701101] = new(701101, Environment.ProcessId, "codex.exe", "codex.exe app-server", @"C:\Codex\codex.exe", 100, null, 1),
            [701102] = new(701102, 9999, "codex.exe", "codex.exe app-server", @"C:\Codex\codex.exe", 100, null, 1)
        };
        var method = typeof(WindowsSystemScanner).GetMethod("BuildProcessReport", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var report = (ProcessReport)method.Invoke(null, [processes, new HashSet<int>(), new HashSet<int>(), Array.Empty<string>()])!;
        Assert.Equal(1, report.CodexSessions);
        Assert.DoesNotContain(report.ProviderProcesses!, x => x.ProcessId == 701101);
    }

    [Theory]
    [InlineData("PI-Desktop.exe", "C:\\Program Files\\PI-Desktop\\PI-Desktop.exe", true)]
    [InlineData("pi-desktop.exe", "", true)]
    [InlineData("powershell.exe", "C:\\Windows\\powershell.exe", false)]
    [InlineData("fake-PI-Desktop.exe", "", false)]
    public void PiDetectionMatchesExecutableNotUnrelatedCommandText(string name, string path, bool expected)
    {
        var process = new ProcessSnapshot(1, 0, name, "echo PI-Desktop.exe", path, 0, null, 1);
        Assert.Equal(expected, WindowsSystemScanner.IsPiDesktop(process));
    }

    [Fact]
    public void NativeScanReturnsPlausibleMetrics()
    {
        var snapshot = new WindowsSystemScanner().Scan();
        Assert.InRange(snapshot.Metrics.AvailableMemoryPercent, 0, 100);
        Assert.True(snapshot.Metrics.TotalMemoryGb > 0);
        Assert.True(snapshot.Metrics.DiskTotalGb > 0);
        Assert.True(snapshot.Processes.CodexSessions >= 0);
    }

    [Fact]
    public void StartupRegistrationRoundTrips()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "VibeGauge";
        using var beforeKey = Registry.CurrentUser.OpenSubKey(keyPath);
        var before = beforeKey?.GetValue(valueName);
        var beforeKind = before is null ? RegistryValueKind.None : beforeKey!.GetValueKind(valueName);
        var registrar = new StartupRegistrar();
        try
        {
            registrar.SetEnabled(true);
            Assert.True(registrar.IsEnabled);
            registrar.SetEnabled(false);
            Assert.False(registrar.IsEnabled);
        }
        finally
        {
            using var restore = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (before is null) restore.DeleteValue(valueName, throwOnMissingValue: false);
            else restore.SetValue(valueName, before, beforeKind);
        }
    }

    [Fact]
    public void ProxyAutoStartSettingRoundTripsWithoutLosingPreviousValue()
    {
        const string keyPath = @"Software\VibeGauge";
        const string valueName = "AutoStartProxy";
        using var beforeKey = Registry.CurrentUser.OpenSubKey(keyPath);
        var before = beforeKey?.GetValue(valueName);
        var beforeKind = before is null ? RegistryValueKind.None : beforeKey!.GetValueKind(valueName);
        var settings = new ProxySettings();
        try
        {
            settings.AutoStart = true;
            Assert.True(settings.AutoStart);
            settings.AutoStart = false;
            Assert.False(settings.AutoStart);
        }
        finally
        {
            using var restore = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (before is null) restore.DeleteValue(valueName, throwOnMissingValue: false);
            else restore.SetValue(valueName, before, beforeKind);
        }
    }
}
