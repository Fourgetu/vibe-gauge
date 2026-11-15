using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ClaudeDesktopTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-desktop-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private string Profile => Path.Combine(Paths.WindowsLocalDataRoot, "Claude-Data", "claude_pzs8sxrjxfjjc");
    private static void Config(string directory, string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.json"), json);
    }

    [Theory]
    [InlineData(true, "已登录", ProviderDataState.NoQuota)]
    [InlineData(false, "未登录", ProviderDataState.NotSignedIn)]
    public void DesktopUsesExplicitLocalFlagEvenWhenAnOldAccountRemains(bool signedIn, string tier, ProviderDataState state)
    {
        Config(Profile, JsonSerializer.Serialize(new { windowSizeWasSignedIn = signedIn, lastKnownAccountUuid = "PRIVATE", accessToken = "SECRET" }));
        using var scanner = new QuotaScanner(Paths);
        var status = scanner.Scan(ProcessReport.Empty with { ClaudeDesktopProcesses = 1 }).Single(p => p.Name == ClaudeDesktop.Name);
        Assert.Equal(tier, status.Tier);
        Assert.Equal(state, status.DataState);
        Assert.True(status.IsRunning);
        Assert.Contains("本地状态", status.Detail);
        Assert.Contains("本地状态", status.CompactDetail);
        Assert.Null(status.FiveHour);
        Assert.Null(status.Weekly);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(status));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(status));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"lastKnownAccountUuid\":\"OLD-ACCOUNT\"}")]
    [InlineData("{\"windowSizeWasSignedIn\":\"true\"}")]
    [InlineData("{\"windowSizeWasSignedIn\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("broken json")]
    public void MissingUnsupportedOrCorruptStateIsUnknown(string json)
    {
        Config(Profile, json);
        var status = ClaudeDesktop.Read(Paths, ProcessReport.Empty with { ClaudeDesktopProcesses = 1 })!;
        Assert.Equal(ProviderDataState.AuthenticationUnknown, status.DataState);
        Assert.Contains("登录状态未知", status.Detail);
        Assert.DoesNotContain("未登录", status.Detail);
    }

    [Fact]
    public void RunningDesktopWithoutConfigIsUnknownAndAbsentDesktopHasNoCard()
    {
        Assert.Null(ClaudeDesktop.Read(Paths, ProcessReport.Empty));
        var status = ClaudeDesktop.Read(Paths, ProcessReport.Empty with { ClaudeDesktopProcesses = 1 })!;
        Assert.Equal(ProviderDataState.AuthenticationUnknown, status.DataState);
    }

    [Fact]
    public void ActiveProfileWinsOverOldSignedInProfileAndMissingActiveFileDoesNotFallBack()
    {
        Config(Profile, "{\"windowSizeWasSignedIn\":true}");
        var active = Path.Combine(root, "active profile 中文");
        var report = ProcessReport.Empty with { ClaudeDesktopProcesses = 1,
            ProviderProcesses = [new(ClaudeDesktop.Name, 42, null, 1, "claude.exe", active)] };
        Assert.Equal(ProviderDataState.AuthenticationUnknown, ClaudeDesktop.Read(Paths, report)!.DataState);
        Config(active, "{\"windowSizeWasSignedIn\":false}");
        Assert.Equal(ProviderDataState.NotSignedIn, ClaudeDesktop.Read(Paths, report)!.DataState);
    }

    [Fact]
    public void MultipleUnresolvedProfilesDoNotGuessTheActiveAccount()
    {
        Config(Profile, "{\"windowSizeWasSignedIn\":true}");
        Config(Path.Combine(Paths.WindowsRoamingDataRoot, "Claude"), "{\"windowSizeWasSignedIn\":false}");
        Assert.Equal(ProviderDataState.AuthenticationUnknown,
            ClaudeDesktop.Read(Paths, ProcessReport.Empty with { ClaudeDesktopProcesses = 1 })!.DataState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassicAndLegacyStoreProfilesAreSupported(bool store)
    {
        var profile = store ? Path.Combine(Paths.WindowsLocalDataRoot, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude")
            : Path.Combine(Paths.WindowsRoamingDataRoot, "Claude");
        Config(profile, "{\"windowSizeWasSignedIn\":true}");
        var status = ClaudeDesktop.Read(Paths, ProcessReport.Empty)!;
        Assert.Equal("已登录", status.Tier);
        Assert.False(status.IsRunning);
        Assert.Contains("本地状态", status.Detail);
    }

    [Fact]
    public void CodeAndDesktopAccountsAndQuotasStaySeparate()
    {
        Config(Profile, "{\"windowSizeWasSignedIn\":false}");
        File.WriteAllText(Paths.ClaudeSettings, "{\"oauthAccount\":{\"organizationRateLimitTier\":\"max_5x\"}}");
        Directory.CreateDirectory(Paths.ClaudeRoot);
        File.WriteAllText(Path.Combine(Paths.ClaudeRoot, "claude-usage.json"), "{\"five_hour\":{\"used_percentage\":42}}");
        using var scanner = new QuotaScanner(Paths);
        var result = scanner.Scan(ProcessReport.Empty with { ClaudeDesktopProcesses = 1, ClaudeSessions = 2 });
        var desktop = result.Single(p => p.Name == ClaudeDesktop.Name);
        var code = result.Single(p => p.Name == "Claude");
        Assert.Equal(ProviderDataState.NotSignedIn, desktop.DataState);
        Assert.Null(desktop.FiveHour);
        Assert.Equal("Max 5x", code.Tier);
        Assert.Equal(42, code.FiveHour!.UsedPercent);
        Assert.Equal(2, code.Sessions);
    }

    [Fact]
    public void LoginAndLogoutAreRefreshedWithoutRestart()
    {
        using var scanner = new QuotaScanner(Paths);
        foreach (var signedIn in new[] { true, false, true })
        {
            Config(Profile, JsonSerializer.Serialize(new { windowSizeWasSignedIn = signedIn }));
            var status = scanner.Scan(ProcessReport.Empty).Single(p => p.Name == ClaudeDesktop.Name);
            Assert.Equal(signedIn ? "已登录" : "未登录", status.Tier);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
