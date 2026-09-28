using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class UpstreamParityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-parity-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private static DateTimeOffset Now => DateTimeOffset.Parse("2026-09-28T12:00:00+08:00");

    [Fact]
    public void ReaderRetainsIncompleteUtf8Line()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("第一行\r\n未完成"));
        var lines = new List<string>();
        var cursor = JsonLineReader.Read(stream, 0, stream.Length, (s, _) => lines.Add(s));
        Assert.Equal(new[] { "第一行" }, lines);
        Assert.Equal(Encoding.UTF8.GetByteCount("第一行\r\n"), cursor);
    }

    [Fact]
    public void ReaderSkipsOversizedLineAndContinues()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1048580) + "\n{}\n"));
        var lines = new List<string>();
        Assert.Equal(stream.Length, JsonLineReader.Read(stream, 0, stream.Length, (s, _) => lines.Add(s), 128));
        Assert.Equal(new[] { "{}" }, lines);
    }

    [Fact]
    public void ExpiredQuotaIsUnknownAndNeverForecast()
    {
        var window = new QuotaWindow(98, Now.AddSeconds(-1), Now, TimeSpan.FromHours(5));
        Assert.Equal("等待新回报", window.Trust(Now));
        Assert.Null(QuotaForecast.Calculate(window, Now));
    }

    [Fact]
    public void RollingQuotaDoesNotResetToZeroAtFirstRelease()
    {
        var window = new QuotaWindow(75, Now.AddSeconds(-1), Now, TimeSpan.FromHours(5), IsRolling: true);
        Assert.Equal(75, window.EffectivePercent(Now));
        Assert.Equal("本机估算", window.Trust(Now));
        Assert.Null(QuotaForecast.Calculate(window, Now));
    }

    [Fact]
    public void ForecastIgnoresStaleOrEstimatedData()
    {
        var window = new QuotaWindow(60, Now.AddHours(3), Now.AddHours(-2), TimeSpan.FromHours(5));
        Assert.Equal("可能过期", window.Trust(Now));
        Assert.Null(QuotaForecast.Calculate(window, Now));
        Assert.Null(QuotaForecast.Calculate(window with { CapturedAt = Now, IsEstimate = true }, Now));
    }

    [Fact]
    public void ForecastUsesObservedZeroRecentRate()
    {
        var window = new QuotaWindow(60, Now.AddHours(3), Now, TimeSpan.FromHours(5), RecentSpanMinutes: 20);
        var forecast = QuotaForecast.Calculate(window, Now)!;
        Assert.Equal(60, forecast.ProjectedPercent);
        Assert.Null(forecast.ExhaustAt);
    }

    [Fact]
    public void ForecastWindowAverageProjectsExhaustion()
    {
        var forecast = QuotaForecast.Calculate(new(60, Now.AddHours(3), Now, TimeSpan.FromHours(5)), Now)!;
        Assert.Equal(150, forecast.ProjectedPercent);
        Assert.InRange(Math.Abs((Now.AddMinutes(80) - forecast.ExhaustAt!.Value).TotalMilliseconds), 0, 1);
    }

    [Fact]
    public void ActivityProfileRequiresEnoughDiverseSamples()
    {
        Assert.Null(ActivityProfile.From(new double[24]));
        var narrow = new double[24]; narrow[9] = 100;
        Assert.Null(ActivityProfile.From(narrow));
        var profile = ActivityProfile.From(Enumerable.Repeat(2d, 24).ToArray())!;
        Assert.Equal(1, profile.Share.Sum(), 10);
        Assert.Equal(1, profile.ActiveDays(Now, Now.AddDays(1), TimeZoneInfo.Utc), 9);
    }

    [Fact]
    public void StatisticsUseLocalDatesAndExcludeFutureRecords()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test", "test");
        var rows = new[] { Record(Now), Record(Now.AddDays(-1)), Record(Now.AddDays(1)) };
        var stats = UsageStatistics.Build(rows, Now, zone);
        Assert.Equal(2, stats.Days.Count);
        Assert.Single(stats.Models);
        Assert.Equal(2, stats.ActivityHours.Sum());
    }

    [Fact]
    public void SamplerUsesCaptureTimeInsteadOfRepeatedScanTime()
    {
        var sampler = new QuotaSampler(Paths);
        var platform = new PlatformStatus("Claude", "Pro", true, 1, ProviderDataState.Available, "",
            new(30, Now.AddHours(4), Now, TimeSpan.FromHours(5)));
        sampler.Apply([platform], Now);
        var result = sampler.Apply([platform], Now.AddMinutes(20));
        Assert.Equal(0, result[0].FiveHour!.RecentSpanMinutes);
    }

    [Fact]
    public void KimiMissingWeeklyPoolIsNotFabricated()
    {
        using var doc = JsonDocument.Parse("""{"code":0,"data":{"kind":"ok","quota":{"usages":{"limit5h":{"usedRatio":0.42,"resetAt":"2026-09-29T12:00:00Z"},"monthTotal":{"usedRatio":0.7}}}}}""");
        var status = OfficialQuotaParser.Kimi(doc.RootElement, Now);
        Assert.Equal(42, status.FiveHour!.UsedPercent);
        Assert.Equal(70, status.Monthly!.UsedPercent);
        Assert.Null(status.Weekly);
    }

    [Fact]
    public void HooksDoNotConfirmUnapprovedRequestsOrLogInput()
    {
        var payload = JsonNode.Parse("""{"session_id":"s1","hook_event_name":"PermissionRequest","tool_use_id":"t1","tool_name":"Bash","tool_input":{"command":"private-command"}}""")!.AsObject();
        CliBridge.RecordHook(Paths, payload, Now);
        Assert.Empty(ReadPending());
        CliBridge.RecordHook(Paths, JsonNode.Parse("""{"session_id":"s1","hook_event_name":"Notification","notification_type":"permission_prompt","message":"secret-message"}""")!.AsObject(), Now);
        Assert.Single(ReadPending());
        var stored = File.ReadAllText(Path.Combine(Paths.LocalDataRoot, "claude-waiting.json"));
        Assert.DoesNotContain("private-command", stored);
        Assert.DoesNotContain("secret-message", stored);
        CliBridge.RecordHook(Paths, JsonNode.Parse("""{"session_id":"s1","hook_event_name":"PostToolUse","tool_use_id":"t1"}""")!.AsObject(), Now);
        Assert.Empty(ReadPending());
        CliBridge.RecordHook(Paths, payload, Now);
        Assert.Empty(ReadPending());
    }

    [Fact]
    public void BridgePreservesOtherHooksAndOriginalStatusLine()
    {
        Directory.CreateDirectory(Paths.ClaudeRoot);
        var path = Path.Combine(Paths.ClaudeRoot, "settings.json");
        File.WriteAllText(path, """{"theme":"dark","statusLine":{"type":"command","command":"echo original","padding":1},"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo user-hook"}]}]}}""");
        CliBridge.Configure(Paths, "C:\\fixture\\VibeGauge.Proxy.exe", true, false);
        CliBridge.Configure(Paths, "C:\\fixture\\VibeGauge.Proxy.exe", true, true);
        Assert.True(CliBridge.HooksInstalled(Paths));
        CliBridge.Configure(Paths, "C:\\fixture\\VibeGauge.Proxy.exe", false, true);
        CliBridge.Configure(Paths, "C:\\fixture\\VibeGauge.Proxy.exe", false, false);
        var value = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("echo original", value["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(1, value["statusLine"]!["padding"]!.GetValue<int>());
        Assert.Contains("echo user-hook", value.ToJsonString());
        Assert.Equal("dark", value["theme"]!.GetValue<string>());
        Assert.False(CliBridge.HooksInstalled(Paths));
    }

    [Fact]
    public void StatusBridgeDoesNotPersistPromptOrCredentials()
    {
        var payload = JsonNode.Parse("""{"session_id":"s1","api_key":"sk-secret","prompt":"private-prompt","rate_limits":{"five_hour":{"used_percentage":42}},"context_window":{"used_percentage":35,"context_window_size":200000},"model":{"id":"fixture"}}""")!.AsObject();
        Assert.Equal("5h 42%", CliBridge.RecordStatus(Paths, payload, Now));
        var text = string.Join("", Directory.EnumerateFiles(Paths.LocalDataRoot).Select(File.ReadAllText));
        Assert.DoesNotContain("sk-secret", text);
        Assert.DoesNotContain("private-prompt", text);
    }

    [Fact]
    public void ProxyConfigurationRejectsMalformedRouteWithoutSilentlyUsingSystem()
    {
        Directory.CreateDirectory(Paths.LocalDataRoot);
        File.WriteAllText(Path.Combine(Paths.LocalDataRoot, "proxy.json"), """{"upstream":7}""");
        var config = ProxyConfiguration.Load(Paths.LocalDataRoot);
        Assert.Equal("direct", config.Upstream);
        Assert.NotEmpty(config.Error);
        Assert.False(ProxyConfiguration.ValidRoute("file:///secret"));
        Assert.False(ProxyConfiguration.ValidRoute("http://127.0.0.1:7890/path"));
    }

    [Fact]
    public void ProxyConfigurationPreservesUnrelatedFields()
    {
        Directory.CreateDirectory(Paths.LocalDataRoot);
        File.WriteAllText(Path.Combine(Paths.LocalDataRoot, "proxy.json"), """{"no_proxy":["example.com"],"custom":42}""");
        new ProxyConfiguration(18999, "direct").Save(Paths.LocalDataRoot);
        Assert.Equal(18999, ProxyConfiguration.Load(Paths.LocalDataRoot).Port);
        Assert.Equal("example.com", Assert.Single(ProxyConfiguration.Load(Paths.LocalDataRoot).NoProxy!));
        Assert.Contains("42", File.ReadAllText(Path.Combine(Paths.LocalDataRoot, "proxy.json")));
    }

    private IReadOnlyList<PendingSession> ReadPending()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.LocalDataRoot, "claude-waiting.json")));
        return SessionMonitor.ParsePending(doc.RootElement, Now);
    }

    [Fact]
    public void DeletedLogHistorySurvivesScannerRestart()
    {
        var directory = Path.Combine(Paths.ClaudeRoot, "projects", "fixture");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "usage.jsonl");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            type = "assistant",
            requestId = "history",
            timestamp = DateTimeOffset.Now.AddDays(-2).ToString("O"),
            message = new { model = "fixture", usage = new { input_tokens = 20, output_tokens = 5 } }
        }) + "\n");
        var scanner = new UsageScanner(Paths);
        Assert.Equal(20, Assert.Single(scanner.Scan().Statistics!.Days).Context);
        File.Delete(file);
        Assert.Equal(20, Assert.Single(new UsageScanner(Paths).Scan().Statistics!.Days).Context);
    }

    [Fact]
    public void ForecastAlertDoesNotRepeatWhenResetDrifts()
    {
        var alerts = new QuotaAlerts(Paths);
        var p = new PlatformStatus("fixture", "Pro", true, 1, ProviderDataState.Available, "",
            new(60, Now.AddHours(3), Now, TimeSpan.FromHours(5)));
        Assert.Empty(alerts.Evaluate([p], null, Now));
        Assert.Single(alerts.Evaluate([p with { FiveHour = p.FiveHour! with { CapturedAt = Now.AddMinutes(16) } }], null, Now.AddMinutes(16)));
        var drifted = p with { FiveHour = p.FiveHour! with { ResetsAt = Now.AddHours(3).AddMinutes(2), CapturedAt = Now.AddMinutes(17) } };
        Assert.Empty(alerts.Evaluate([drifted], null, Now.AddMinutes(17)));
    }

    [Fact]
    public void OfficialCliMonthlyQuotaAndMillisecondResetAreParsed()
    {
        using var doc = JsonDocument.Parse("""{"per5Hour":{"usedQuota":3,"totalQuota":10,"resetTime":1790600000000},"perBillMonth":{"percentage":0.42,"resetTime":1792600000}}""");
        var result = OfficialQuotaParser.Cli("百炼", doc.RootElement, Now);
        Assert.Equal(30, result.FiveHour!.UsedPercent);
        Assert.Equal(42, result.Monthly!.UsedPercent);
        Assert.NotNull(result.FiveHour.ResetsAt);
    }
    private static InteractionRecord Record(DateTimeOffset at) => new("id", "Claude", "fixture", at, 10, 2, 0, 3, 0);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
