using System.Text.Json;
using System.Text.Json.Nodes;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ReauditRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-reaudit-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private readonly DateTimeOffset now = DateTimeOffset.Now.AddSeconds(-1);
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    private string Quota(int value, DateTimeOffset? captured) => JsonSerializer.Serialize(new
    { _captured_at = captured?.ToUnixTimeSeconds(), five_hour = new { used_percentage = value, resets_at = now.AddHours(4).ToUnixTimeSeconds() } });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaudeChoosesNewestValidSourceEvenIfPreferredFileIsBroken(bool broken)
    {
        Write(Path.Combine(Paths.LocalDataRoot, "claude-usage.json"), broken ? "{broken" : Quota(20, now.AddMinutes(-30)));
        Write(Path.Combine(Paths.ClaudeRoot, "claude-usage.json"), Quota(80, now));
        using var scanner = new QuotaScanner(Paths);
        Assert.Equal(80, scanner.Scan(ProcessReport.Empty).Single(x => x.Name == "Claude").FiveHour!.UsedPercent);
    }
    [Fact]
    public void ClaudeUsesMtimeAndSkipsMalformedSecondaryPool()
    {
        var path = Path.Combine(Paths.ClaudeRoot, "claude-usage.json");
        var node = JsonNode.Parse(Quota(70, null))!;
        node["five_hour_a"] = JsonNode.Parse("{}");
        node["five_hour_b"] = JsonNode.Parse("{\"used_percentage\":30}");
        Write(path, node.ToJsonString()); File.SetLastWriteTimeUtc(path, now.AddDays(-2).UtcDateTime);
        using var scanner = new QuotaScanner(Paths);
        var claude = scanner.Scan(ProcessReport.Empty).Single(x => x.Name == "Claude");
        Assert.Equal("可能过期", claude.FiveHour!.Trust(now)); Assert.Equal("B", claude.SecondaryPoolName);
    }
    [Fact]
    public void EmptyModelPoolLeavesRequestInSharedPoolAndPartialResetDoesNotInherit()
    {
        using var doc = JsonDocument.Parse("""{"requests":{"5h":10},"reset":{"weekly":"monday"},"models":{"special":{},"valid":{"requests":{"weekly":10},"reset":{"5h":"first_use"}}}}""");
        var plan = CodingPlan.Parse(doc.RootElement, "fixture");
        Assert.False(plan.Models.ContainsKey("special")); Assert.False(plan.Models["valid"].Reset.ContainsKey("weekly"));
        var windows = plan.Estimate([new("1", "API", "special", now.AddMinutes(-1), 0, 0, 0, 0, 0, 502, ReachedUpstream: true)], now);
        Assert.Equal(10, windows[0].Window.UsedPercent); Assert.True(windows[1].Window.IsRolling);
    }
    [Fact]
    public void RollingDescriptionExplainsPartialReleaseAndRefresh()
    {
        var window = new QuotaWindow(50, now.AddMinutes(30), now, TimeSpan.FromHours(5), true, true, 2);
        Assert.Contains("后释放 2 次", window.ResetDescription(now)); Assert.DoesNotContain("重置", window.ResetDescription(now));
        Assert.Equal("待刷新", window.ResetDescription(now.AddHours(1)));
    }
    [Theory]
    [InlineData("6m0s", 360)]
    [InlineData("1500ms", 1.5)]
    [InlineData("30", 30)]
    [InlineData("1h2m3s", 3723)]
    public void RelativeRateLimitResetIsRelativeToResponse(string value, double seconds) =>
        Assert.Equal(now.AddSeconds(seconds), new ApiRateLimit("x-ratelimit-reset-tokens", value, now).ResetAt);
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("999999999999999999999d")]
    [InlineData("-30")]
    [InlineData("1m junk")]
    public void InvalidRateLimitResetRemainsUnknown(string value) => Assert.Null(new ApiRateLimit("retry-after", value, now).ResetAt);
    [Fact]
    public void RateLimitUsesOneRecentAccountSnapshotAndTightestWindow()
    {
        InteractionRecord Row(string id, DateTimeOffset at, Dictionary<string, string> headers, string host = "a.example") =>
            new(id, "API · test", "model", at, 1, 0, 0, 1, 0, 200, RateLimits: headers, ApiHost: host);
        var old = Row("1", now.AddDays(-3), new() { ["x-ratelimit-limit-tokens"] = "100" });
        var recent = Row("2", now, new() { ["x-ratelimit-remaining-tokens"] = "10" });
        var quality = ApiQuality.Build([old, recent], now);
        Assert.Single(quality.Limits); Assert.Null(quality.LimitingQuota);
        Assert.Empty(ApiQuality.Build([old], now).Limits);
        var current = Row("3", now, new() { ["x-ratelimit-limit-tokens"] = "100", ["x-ratelimit-remaining-tokens"] = "10",
            ["x-ratelimit-reset-tokens"] = "30", ["x-ratelimit-limit-requests"] = "20", ["x-ratelimit-remaining-requests"] = "10" });
        quality = ApiQuality.Build([current], now);
        Assert.Equal(90, quality.LimitingQuota!.Window.UsedPercent); Assert.Equal(now.AddSeconds(30), quality.LimitingQuota.Window.ResetsAt);
        Assert.Empty(ApiQuality.Build([current, current with { ApiHost = "b.example" }], now).Limits);
        Assert.Empty(ApiQuality.Build([current], now.AddHours(2)).Limits);
    }
    [Fact]
    public void QuotaNotificationsSeedThenWarnEscalateIgnoreResetDriftAndRearm()
    {
        var options = new FeaturePreferences { QuotaNotifications = true };
        var policy = new AttentionPolicy(Paths);
        DashboardSnapshot Snapshot(int pct, int seconds) => new(now.AddSeconds(seconds), new(50, 8, 16, 0, 1, 100, 500, 0), ProcessReport.Empty,
            [new("Gemini", "fixture", true, 1, ProviderDataState.Available, "", new(pct, now.AddHours(3).AddSeconds(seconds), now.AddSeconds(seconds), TimeSpan.FromHours(5)))], UsageSummary.Empty, ApiUsageSummary.Empty);
        Assert.Empty(policy.Evaluate(Snapshot(70, 0), options));
        Assert.Single(policy.Evaluate(Snapshot(90, 5), options));
        Assert.Single(policy.Evaluate(Snapshot(100, 10), options));
        Assert.Empty(policy.Evaluate(Snapshot(100, 15), options));
        Assert.Empty(policy.Evaluate(Snapshot(89, 20), options));
        Assert.Empty(policy.Evaluate(Snapshot(100, 25), options));
        Assert.Empty(policy.Evaluate(Snapshot(80, 30), options));
        Assert.Single(policy.Evaluate(Snapshot(90, 1900), options));
        Assert.Empty(new AttentionPolicy(Paths).Evaluate(Snapshot(100, 1905), options));
    }
    [Theory]
    [InlineData("open.bigmodel.cn", "{\"code\":500,\"data\":{\"limits\":[{\"percentage\":77}]}}")]
    [InlineData("open.bigmodel.cn", "{\"data\":{\"limits\":[{\"percentage\":77}]}}")]
    [InlineData("api.minimaxi.com", "{\"base_resp\":{\"status_code\":1004},\"data\":{}}")]
    [InlineData("api.deepseek.com", "{\"is_available\":true}")]
    [InlineData("openrouter.ai", "{\"data\":{}}")]
    public void BusinessErrorsAndMissingBalancesCannotBeReportedAvailable(string host, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = OfficialQuotaParser.Provider(host, "fixture", doc.RootElement, now);
        Assert.Equal(ProviderDataState.ReadFailed, result.DataState); Assert.Null(result.FiveHour); Assert.Null(result.Weekly);
    }
    [Theory]
    [InlineData("{\"code\":200,\"data\":{\"limits\":[{\"percentage\":77}]}}", ProviderDataState.Available)]
    [InlineData("{\"code\":200,\"data\":{\"limits\":[]}}", ProviderDataState.NoQuota)]
    [InlineData("{\"code\":404,\"msg\":\"不存在 Coding Plan\"}", ProviderDataState.NoQuota)]
    public void GlmDistinguishesSuccessAndExplicitNoPlan(string json, ProviderDataState expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, OfficialQuotaParser.Provider("open.bigmodel.cn", "fixture", doc.RootElement, now).DataState);
    }
    [Fact]
    public void ApiMigrationRestoresSentAndHostsWithoutAddingCallsThenRetainsDeletedHistory()
    {
        var file = Path.Combine(Paths.LocalDataRoot, "api-calls.jsonl");
        string Call(string host) => JsonSerializer.Serialize(new { ts = now, provider = "fixture", host, key = "abc", model = "m", status = 502, sent = true, ctx = 10, @out = 5 });
        Write(file, Call("a.example") + "\n" + Call("b.example") + "\n");
        Write(Path.Combine(Paths.LocalDataRoot, "plans.json"), """{"fixture":{"requests":{"5h":10}}}""");
        var before = new UsageScanner(Paths).Scan(); Assert.Equal(2, before.Api.Calls);
        var cacheFile = Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName);
        var cache = JsonNode.Parse(File.ReadAllText(cacheFile))!;
        foreach (var state in cache["Files"]!.AsObject().Select(x => x.Value!))
        {
            state["ApiMetadataVersion"] = 1;
            foreach (var record in state["Records"]!.AsObject().Select(x => x.Value!.AsObject())) { record.Remove("ApiHost"); record.Remove("ReachedUpstream"); }
        }
        File.WriteAllText(cacheFile, cache.ToJsonString());
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(2, result.Api.Calls); Assert.Equal(30, result.Api.Ranges![0].TotalTokens);
        Assert.Equal(2, result.Api.Providers.Count); Assert.Equal(20, result.Plans!.Single().ExtraQuotas!.Single().Window.UsedPercent);
        File.Delete(file); Assert.Equal(2, new UsageScanner(Paths).Scan().Api.Calls);
        Write(file, Call("a.example") + "\n" + Call("b.example") + "\n");
        Assert.Equal(2, new UsageScanner(Paths).Scan().Api.Calls);
    }
    [Fact]
    public void ClaudeCompactionUuidDeduplicatesWhileSameTimestampDifferentEventsRemain()
    {
        var transcript = Path.Combine(root, "fixture.jsonl");
        string Line(string uuid) => JsonSerializer.Serialize(new { type = "system", subtype = "compact_boundary", uuid, timestamp = now,
            compactMetadata = new { preTokens = 758290, postTokens = 21625 } }) + "\n";
        Write(transcript, Line("one") + Line("one") + Line("two"));
        Write(Path.Combine(Paths.LocalDataRoot, "claude-sessions.json"), JsonSerializer.Serialize(new { sessions = new Dictionary<string, object> {
            ["fixture"] = new { at = now.ToUnixTimeSeconds(), transcript, model = "fixture", cwd = root } } }));
        var monitor = new SessionMonitor(Paths); var result = monitor.Scan(now).Active.Single();
        Assert.Equal(2, result.Compactions); Assert.All(result.CompactionEvents!, x => { Assert.Equal(758290, x.PreTokens); Assert.Equal(21625, x.PostTokens); });
        File.AppendAllText(transcript, Line("one")); Assert.Equal(2, monitor.Scan(now).Active.Single().Compactions);
        Write(transcript, Line("three")); Assert.Equal(1, monitor.Scan(now).Active.Single().Compactions);
    }
    [Fact]
    public void RewrittenApiLogCannotReplaceKnownHostAndCopiesKeepTheirCounts()
    {
        var legacy = Path.Combine(Paths.LegacyDataRoot, "api-calls.jsonl");
        var current = Path.Combine(Paths.LocalDataRoot, "api-calls.jsonl");
        string Line(string host) => JsonSerializer.Serialize(new { ts = now, host, provider = "fixture", key = "abc", model = "m", ctx = 10, @out = 5, status = 200, sent = true }) + "\n";
        Write(legacy, Line("a.example")); Assert.Equal(1, new UsageScanner(Paths).Scan().Api.Calls);
        Write(current, Line("b.example")); Assert.Equal(2, new UsageScanner(Paths).Scan().Api.Calls);
        Write(current, Line("a.example") + Line("b.example"));
        var migrated = new UsageScanner(Paths).Scan(); Assert.Equal(2, migrated.Api.Calls); Assert.Equal(30, migrated.Api.Ranges![0].TotalTokens);
        Write(current, Line("c.example"));
        var replaced = new UsageScanner(Paths).Scan(); Assert.Equal(3, replaced.Api.Calls); Assert.Equal(3, replaced.Api.Providers.Count);
        File.Delete(current); File.Delete(legacy); Assert.Equal(3, new UsageScanner(Paths).Scan().Api.Calls);
        Write(current, Line("a.example") + Line("b.example") + Line("c.example"));
        Assert.Equal(3, new UsageScanner(Paths).Scan().Api.Calls);
    }
    [Theory]
    [InlineData("false", "true", 0)]
    [InlineData("true", "false", 10)]
    [InlineData("null", "true", 10)]
    public void ExplicitWindowsSentFlagTakesPrecedenceWithValidLegacyFallback(string reached, string sent, int used)
    {
        Write(Path.Combine(Paths.LocalDataRoot, "api-calls.jsonl"), "{\"ts\":\"" + now.ToString("O") + "\",\"provider\":\"fixture\",\"status\":502,\"reached_upstream\":" + reached + ",\"sent\":" + sent + "}\n");
        Write(Path.Combine(Paths.LocalDataRoot, "plans.json"), """{"fixture":{"requests":{"5h":10}}}""");
        Assert.Equal(used, new UsageScanner(Paths).Scan().Plans!.Single().ExtraQuotas!.Single().Window.UsedPercent);
    }
    [Fact]
    public void InvalidChildRequestLimitIsIgnoredAndOldEgressEventsAreNotRepeated()
    {
        using var doc = JsonDocument.Parse("""{"requests":{"5h":10},"models":{"special":{"requests":{"5h":"bad"}}}}""");
        Assert.Empty(CodingPlan.Parse(doc.RootElement, "fixture").Models);
        var report = new NetworkDiagnosticsReport(now, [], [], [], ["OpenAI API 出口变化：A → B"], "", "");
        var snapshot = new DashboardSnapshot(now, new(50, 8, 16, 0, 1, 100, 500, 0), ProcessReport.Empty, [], UsageSummary.Empty, ApiUsageSummary.Empty, Diagnostics: report);
        var policy = new AttentionPolicy(Paths); var options = new FeaturePreferences { EgressNotifications = true };
        Assert.Single(policy.Evaluate(snapshot, options));
        Assert.Empty(policy.Evaluate(snapshot with { CapturedAt = now.AddHours(1) }, options));
        Assert.Empty(new AttentionPolicy(Paths).Evaluate(snapshot with { CapturedAt = now.AddMinutes(2) }, options));
        Assert.Single(policy.Evaluate(snapshot with { CapturedAt = now.AddHours(1), Diagnostics = report with { CapturedAt = now.AddHours(1) } }, options));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
