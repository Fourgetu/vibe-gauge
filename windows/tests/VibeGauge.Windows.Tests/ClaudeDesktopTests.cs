using System.Reflection;
using VibeGauge.Core;
using VibeGauge.Windows.Services;
using VibeGauge.Windows.ViewModels;
using Xunit;

namespace VibeGauge.Windows.Tests;

public sealed class ClaudeDesktopTests
{
    private const string StoreExe = @"C:\Program Files\WindowsApps\Claude_5.1_x64__pzs8sxrjxfjjc\app\Claude.exe";

    [Fact]
    public void LedgerDisplayPreservesQuotaAndTwoColumnsWithoutInventingThinkingTokens()
    {
        var usage = new UsageSourceSummary(ClaudeDesktop.Name, UsageDataState.Available, 2, 180, 70, 10, 20, 0,
            "来自桌面真实用量账本；仅覆盖已记录活动");
        var status = new PlatformStatus(ClaudeDesktop.Name, "Pro", true, 1, ProviderDataState.Available,
            "官方额度状态", FiveHour: new(25, DateTimeOffset.Now.AddHours(1), DateTimeOffset.Now, TimeSpan.FromHours(5)),
            DesktopTokens: new(usage, usage, true));
        var row = PlatformRow.From(status);
        Assert.False(row.IsWide);
        Assert.True(row.HasQuota);
        Assert.Contains("官方额度状态", row.Detail);
        Assert.Contains("今日已记录", row.DisplayDetail);
        Assert.DoesNotContain("思考 0", row.Detail);
        Assert.Contains("2 条记录", UsageSourceRow.From(usage).TotalText);
        var missing = usage with { State = UsageDataState.NoLocalStats, Turns = 0, Note = ClaudeDesktopUsage.MissingUsage };
        var empty = PlatformRow.From(status with { DesktopTokens = new(missing, missing, true) });
        Assert.Equal("真实 Token 暂不可用", empty.DisplayDetail);
        Assert.Contains("尚未写入真实", empty.FullTooltip);
    }

    [Theory]
    [InlineData(StoreExe, true)]
    [InlineData(@"C:\Users\Name 中文\AppData\Local\Claude\app-1.2.3\claude.exe", true)]
    [InlineData(@"C:\Users\Name 中文\.local\bin\claude.exe", false)]
    [InlineData(@"C:\Tools\claude.exe", false)]
    [InlineData(@"C:\Windows\pwsh.exe", false)]
    [InlineData("", false)]
    public void DesktopDetectionUsesExecutableNotMentionedCommandText(string path, bool expected)
    {
        var process = new ProcessSnapshot(1, 0, "claude.exe", "echo " + StoreExe, path, 0, null, 1);
        Assert.Equal(expected, WindowsSystemScanner.IsClaudeDesktop(process));
    }

    [Fact]
    public void DesktopChildrenAreNotCodeSessionsButEmbeddedCodeIsCounted()
    {
        var processes = new Dictionary<int, ProcessSnapshot>
        {
            [101] = new(101, 0, "claude.exe", $"\"{StoreExe}\"", StoreExe, 50, null, 1),
            [102] = new(102, 101, "claude.exe", $"\"{StoreExe}\" --type=renderer --user-data-dir=\"C:\\Profile 中文\\Claude\"", StoreExe, 50, null, 1),
            [103] = new(103, 101, "claude.exe", "claude.exe", @"C:\Native Code\claude.exe", 50, null, 1),
            [104] = new(104, 103, "claude.exe", "claude.exe", @"C:\Native Code\claude.exe", 50, null, 1),
            [105] = new(105, 0, "claude.exe", "claude.exe", @"C:\Tools\claude.exe", 50, null, 1),
            [106] = new(106, 0, "claude.exe", "claude.exe mcp-server", @"C:\Tools\claude.exe", 50, null, 1)
        };
        var method = typeof(WindowsSystemScanner).GetMethod("BuildProcessReport", BindingFlags.Static | BindingFlags.NonPublic)!;
        var report = (ProcessReport)method.Invoke(null, [processes, new HashSet<int>(), new HashSet<int>(), Array.Empty<string>()])!;
        Assert.Equal(1, report.ClaudeDesktopProcesses);
        Assert.Equal(2, report.ClaudeSessions);
        Assert.Equal(@"C:\Profile 中文\Claude", report.ProviderProcesses!.Single(p => p.Provider == ClaudeDesktop.Name).DataDirectory);
        Assert.DoesNotContain(report.ProviderProcesses!, p => p.ProcessId is 102 or 104 or 106);
    }

    [Fact]
    public void DesktopCardShowsLocalLoginAndRuntimeWithoutClaimingChatCount()
    {
        var status = new PlatformStatus(ClaudeDesktop.Name, "已登录", true, 1, ProviderDataState.NoQuota,
            "已登录（本地状态）\n订阅额度尚未接入", CompactDetail: "已登录（本地状态）\n额度尚未接入");
        var row = PlatformRow.From(status);
        Assert.Equal("运行中", row.SessionText);
        Assert.Equal(status.Detail, row.Detail);
        Assert.Contains("已登录", row.DisplayDetail);
        Assert.DoesNotContain("会话", row.FullTooltip);
        Assert.False(row.ShowTier);
        Assert.Empty(row.Quotas);
        var stopped = DashboardViewModel.WithActivity(status, ProcessReport.Empty with { ClaudeSessions = 3 });
        Assert.False(stopped.IsRunning);
        Assert.Equal("未运行", PlatformRow.From(stopped).SessionText);
        Assert.Equal(status.Detail, stopped.Detail);
        var resumed = DashboardViewModel.WithActivity(stopped, ProcessReport.Empty with { ClaudeDesktopProcesses = 1 });
        Assert.True(resumed.IsRunning);
        Assert.Equal(1, resumed.Sessions);
    }
}
