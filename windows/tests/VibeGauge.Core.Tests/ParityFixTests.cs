using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ParityFixTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-parity-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    private readonly DateTimeOffset now = DateTimeOffset.Now;

    [Fact]
    public void CodexExplicitLimitOverridesOlderQuotaButNotNewerOrExpired()
    {
        var file = Path.Combine(Paths.CodexRoot, "sessions", "fixture.jsonl");
        string Quota(int minute) => JsonSerializer.Serialize(new { timestamp = now.AddMinutes(minute), rate_limits = new {
            secondary = new { used_percent = 42, resets_at = now.AddDays(1).ToUnixTimeSeconds(), window_minutes = 10080 } } });
        string Hit(int minute, string message) => JsonSerializer.Serialize(new { timestamp = now.AddMinutes(minute), payload = new {
            error = new { codex_error_info = "usage_limit_exceeded", message } } });
        Write(file, Quota(-3) + "\n" + Hit(-2, "limit reached"));
        var scanner = new QuotaScanner(Paths);
        PlatformStatus Read() => scanner.Scan(ProcessReport.Empty).Single(x => x.Name == "Codex");
        Assert.Equal(100, Read().Weekly?.UsedPercent);
        Assert.Equal(now.AddDays(1).ToUnixTimeSeconds(), Read().Weekly?.ResetsAt?.ToUnixTimeSeconds());
        File.AppendAllText(file, "\n" + Quota(-1));
        Assert.Equal(42, Read().Weekly?.UsedPercent);
        File.AppendAllText(file, "\n" + Hit(0, "try again on September 1st, 2020 1:30 PM"));
        Assert.Equal(42, Read().Weekly?.UsedPercent);
        Write(file, Hit(0, "try again at September 29, 2099 1:30 PM"));
        Assert.Equal(2099, Read().Weekly?.ResetsAt?.Year);
        Assert.Equal(100, Read().Weekly?.UsedPercent);
    }

    [Fact]
    public void GeminiMergesEachWindowByItsTimestampAndHandlesMalformedBridge()
    {
        Write(Paths.ResolveOwnDataFile("agy-quota.json"), """{"updated_at":100,"pools":{"gemini":{"5h":{"remaining_fraction":0.8}},"3p":{"5h":{"remaining_fraction":0.7,"recorded_at":400}}}}""");
        Write(Paths.AgyQuota, """{"updated_at":200,"pools":{"gemini":{"5h":{"remaining_fraction":0.2},"weekly":{"remaining_fraction":0.4}},"3p":{"5h":{"remaining_fraction":0.1}}}}""");
        PlatformStatus Read() => new QuotaScanner(Paths).Scan(ProcessReport.Empty).Single(x => x.Name == "Gemini");
        var p = Read();
        Assert.Equal(80, p.FiveHour?.UsedPercent);
        Assert.Equal(60, p.Weekly?.UsedPercent);
        Assert.Equal(30, p.SecondaryFiveHour?.UsedPercent);
        Write(Paths.ResolveOwnDataFile("agy-quota.json"), "broken");
        Assert.Equal(80, Read().FiveHour?.UsedPercent);
    }

    [Fact]
    public void ProfilePrefersOwnThenApiThenGlobalWithoutCountingApiTwice()
    {
        InteractionRecord Row(string source, int hour) => new(Guid.NewGuid().ToString(), source, "m", now.Date.AddDays(-1).AddHours(hour), 10, 0, 0, 5, 0);
        var cli = Enumerable.Range(0, 30).Select(i => Row("Claude", 8 + i % 3)).Concat(Enumerable.Range(0, 90).Select(i => Row("Codex", 16 + i % 3))).ToArray();
        var api = Enumerable.Range(0, 30).Select(i => Row("API · Gemini · key", 3 + i % 3));
        var stats = UsageStatistics.Build(cli, now, apiRecords: api);
        Assert.Equal(120, stats.Days.Sum(x => x.Calls));
        Assert.True(stats.ProfileFor("Claude")!.Share[8] > stats.ProfileFor("Claude")!.Share[16]);
        Assert.True(stats.ProfileFor("Gemini")!.Share[3] > stats.ProfileFor("Gemini")!.Share[16]);
        Assert.Equal(ActivityProfile.From(stats.ActivityHours)!.Share, stats.ProfileFor("unknown")!.Share);
    }

    [Fact]
    public void UnknownApiUsageSurvivesRetentionAndDoesNotInventTokens()
    {
        var file = Paths.ResolveOwnDataFile("api-calls.jsonl");
        var lines = new[] { false, true, false }.Select((parsed, i) => JsonSerializer.Serialize(new {
            ts = now.AddSeconds(-i - 1), provider = "fixture", key = "fingerprint", model = "m", status = 200,
            parsed, ctx = parsed ? 10 : 0, @out = parsed ? 5 : 0 }));
        Write(file, string.Join("\n", lines) + "\n");
        var result = new UsageScanner(Paths).Scan().Api;
        Assert.Equal(2, result.UnknownUsage);
        Assert.Equal(2, result.Ranges![0].UnknownUsage);
        Assert.Equal(2, result.Providers[0].UnknownUsage);
        Assert.Equal(2, result.Recent!.Count(x => x.UnknownUsage));
        Assert.Equal(10, result.ContextTokens);
        File.Delete(file);
        Assert.Equal(2, new UsageScanner(Paths).Scan().Api.UnknownUsage);
        Assert.False(new InteractionRecord("", "API", "", now, 10, 0, 0, 0, 0, 200).UnknownUsage);
        Assert.True(new InteractionRecord("", "API", "", now, 0, 0, 0, 0, 0, 200).UnknownUsage);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("restart")]
    public void AlertRequiresContinuousObservation(string interruption)
    {
        var alerts = new QuotaAlerts(Paths);
        PlatformStatus P(DateTimeOffset at) => new("fixture", "Pro", true, 1, ProviderDataState.Available, "", new(60, now.AddHours(3), at, TimeSpan.FromHours(5)));
        for (var minute = 0; minute < 10; minute++) Assert.Empty(alerts.Evaluate([P(now.AddMinutes(minute))], null, now.AddMinutes(minute)));
        if (interruption == "disabled") alerts.Evaluate([P(now.AddMinutes(10))], null, now.AddMinutes(10), false);
        if (interruption == "missing") alerts.Evaluate([], null, now.AddMinutes(10));
        if (interruption == "restart") alerts = new QuotaAlerts(Paths);
        var resume = now.AddMinutes(interruption == "gap" ? 30 : 11);
        for (var minute = 0; minute < 15; minute++) Assert.Empty(alerts.Evaluate([P(resume.AddMinutes(minute))], null, resume.AddMinutes(minute)));
        Assert.Single(alerts.Evaluate([P(resume.AddMinutes(15))], null, resume.AddMinutes(15)));
        alerts = new QuotaAlerts(Paths);
        Assert.Empty(alerts.Evaluate([P(resume.AddMinutes(16))], null, resume.AddMinutes(16)));
    }

    [Theory]
    [InlineData(false, "{\"models\":[{\"key\":\"disk\",\"loaded_instances\":[]},{\"key\":\"live\",\"loaded_instances\":[{}]}]}")]
    [InlineData(true, "{\"data\":[{\"id\":\"disk\",\"state\":\"not-loaded\"},{\"id\":\"live\",\"state\":\"loaded\"}]}")]
    public void LmStudioOnlyCountsLoadedModels(bool legacy, string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("live", Assert.Single(LocalModelParser.LmStudio(doc.RootElement, legacy)!));
        using var availableOnly = JsonDocument.Parse("""{"data":[{"id":"available"}]}""");
        Assert.Null(LocalModelParser.LmStudio(availableOnly.RootElement));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void ExistingButInsufficientOwnProfileFallsBackToCliRatherThanApi()
    {
        var own = new double[24]; own[8] = 1;
        var api = new double[24]; api[1] = api[2] = api[3] = 20;
        var all = new double[24]; all[12] = all[13] = all[14] = 20;
        var stats = new UsageStatistics([], [], [], all, SourceActivity: new Dictionary<string, double[]> { ["Claude"] = own, ["API · Claude"] = api });
        Assert.Equal(ActivityProfile.From(all)!.Share, stats.ProfileFor("Claude")!.Share);
    }

    [Fact]
    public void LegacyApiCacheRegainsUsageMetadataWithoutAddingCalls()
    {
        var file = Paths.ResolveOwnDataFile("api-calls.jsonl");
        Write(file, JsonSerializer.Serialize(new { ts = now.AddSeconds(-1), provider = "fixture", model = "m", status = 200, parsed = false, ctx = 10, @out = 5, complete = false }) + "\n");
        var scan = new UsageScanner(Paths).Scan(); Assert.Equal(1, scan.Api.Calls);
        var cache = Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cache))!;
        foreach (var row in node["Files"]!.AsObject())
        {
            row.Value!["ApiMetadataVersion"] = 0;
            foreach (var record in row.Value["Records"]!.AsObject())
            { record.Value!.AsObject().Remove("UsageKnown"); record.Value.AsObject().Remove("Completed"); }
        }
        File.WriteAllText(cache, node.ToJsonString());
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(1, result.Api.Calls); Assert.Equal(1, result.Api.UnknownUsage);
        Assert.Equal(15, result.Api.Ranges![0].TotalTokens); Assert.Equal(1, result.Api.Ranges[0].Errors);
        File.Delete(file);
        Assert.Equal(1, new UsageScanner(Paths).Scan().Api.Calls);
    }
}
