using System.Text.Json;
using System.Text.Json.Nodes;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class FeaturePortTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-ports-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private readonly DateTimeOffset now = DateTimeOffset.Now;
    private static void Write(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); }
    [Fact]
    public void AgyBridgeMergesPoolsAndRestoresOriginalSettings()
    {
        var settings = Path.Combine(Paths.GeminiRoot, "antigravity-cli", "settings.json");
        Write(settings, """{"theme":"mine","statusLine":{"type":"command","command":"echo original","padding":3}}""");
        CliBridge.Configure(Paths, "C:\\fixture\\proxy.exe", true, false, "agy");
        Assert.True(CliBridge.StatusInstalled(Paths, "agy"));
        CliBridge.RecordAgyStatus(Paths, JsonNode.Parse("""{"model":{"id":"claude-fixture"},"quota":{"5h":{"remaining_fraction":0.7,"reset_in_seconds":100}},"api_key":"secret","prompt":"private"}""")!.AsObject(), now);
        CliBridge.RecordAgyStatus(Paths, JsonNode.Parse("""{"model":"gemini-fixture","quota":{"weekly":{"remaining_fraction":0.4}}}""")!.AsObject(), now);
        var quota = File.ReadAllText(Paths.ResolveOwnDataFile("agy-quota.json"));
        Assert.Contains("3p", quota); Assert.Contains("gemini", quota); Assert.DoesNotContain("secret", quota); Assert.DoesNotContain("private", quota);
        CliBridge.Configure(Paths, "C:\\fixture\\proxy.exe", false, false, "agy");
        Assert.False(CliBridge.StatusInstalled(Paths, "agy"));
        var restored = JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal("echo original", restored["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(3, restored["statusLine"]!["padding"]!.GetValue<int>());
        Assert.Equal("mine", restored["theme"]!.GetValue<string>());
    }
    [Fact]
    public void LocalDiscoveryOnlyUsesRuntimeProcessesAndValidPorts()
    {
        ProcessSnapshot P(int id, string name, string command) => new(id, 0, name, command, "", 10, now, 1);
        var result = LocalRuntimeDiscovery.From([P(1, "llama-server.exe", "llama-server --port 9001"), P(2, "llama-server.exe", "llama-server -p=9002"),
            P(3, "llmster.exe", ""), P(4, "cmd.exe", "llama-server --port 9999"), P(5, "llama-server.exe", "--port 99999")]);
        Assert.Equal(new[] { 1234, 9001, 9002 }, result.Select(x => x.Port));
    }
    [Fact]
    public void AutoReaperRequiresStableTwoScansAndResetsAfterSleep()
    {
        var p = new OrphanProcess(42, now.AddHours(-1), "fingerprint", "node", "mcp", 25, "fixture", "orphan");
        var policy = new AutoReapPolicy();
        Assert.Empty(policy.Evaluate([p], false, true, now));
        Assert.Empty(policy.Evaluate([p], true, true, now));
        Assert.Empty(policy.Evaluate([p], true, true, now.AddSeconds(119)));
        Assert.Single(policy.Evaluate([p], true, true, now.AddSeconds(120)));
        Assert.Empty(policy.Evaluate([p], true, true, now.AddSeconds(121)));
        Assert.Empty(policy.Evaluate([p], true, true, now.AddMinutes(20)));
        Assert.Empty(policy.Evaluate([p with { StartedAt = now }], true, true, now.AddMinutes(21)));
    }
    [Fact]
    public void NotificationTogglesThresholdsAndDedupSurviveRestart()
    {
        var snapshot = new DashboardSnapshot(now, new(5, 15, 16, 0, 1, 2, 100, 0), ProcessReport.Empty, [], UsageSummary.Empty, ApiUsageSummary.Empty,
            Sessions: new([], [new("s", "permission", "Claude", "", now.AddMinutes(-2))]));
        var policy = new AttentionPolicy(Paths);
        Assert.Empty(policy.Evaluate(snapshot, new()));
        var options = new FeaturePreferences { MemoryNotifications = true, DiskNotifications = true, PendingNotifications = true };
        Assert.Equal(3, policy.Evaluate(snapshot, options).Count);
        Assert.Empty(new AttentionPolicy(Paths).Evaluate(snapshot with { CapturedAt = now.AddMinutes(1) }, options));
    }
    [Fact]
    public void CleanupRequiresDurableLedgerSkipsChangedFilesAndRefusesNonAllowlistedPaths()
    {
        var inventory = new DiskInventory(Paths);
        var area = inventory.Areas.First(x => x.Name == "Codex");
        var file = Path.Combine(area.Root, "old.jsonl"); Write(file, "{}\n"); File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddDays(-100));
        var plan = inventory.Preview(area, 90, now);
        Assert.Single(plan.Files);
        Assert.Throws<IOException>(() => inventory.Delete(plan, () => false)); Assert.True(File.Exists(file));
        File.AppendAllText(file, "new");
        Assert.Equal(1, inventory.Delete(plan, () => true).Skipped);
        Assert.Throws<IOException>(() => inventory.Preview(new("any", root, true), 90, now));
        Assert.False(DiskInventory.SafePath(area.Root, Path.Combine(area.Root, "..", "secret")));
        File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddDays(-100));
        Assert.Equal(1, inventory.Delete(inventory.Preview(area, 90, now), () => true).Deleted);
    }
    [Theory]
    [InlineData("v2.0.0", false, "VibeGauge-win-x64.exe", true)]
    [InlineData("v2.0.0", true, "VibeGauge-win-x64.exe", false)]
    [InlineData("v2.0.0", false, "VibeGauge.dmg", false)]
    [InlineData("v1.0.0", false, "VibeGauge-win-x64.exe", false)]
    [InlineData("garbage", false, "VibeGauge-win-x64.exe", false)]
    public void UpdatesRequireNewStableWindowsAsset(string tag, bool prerelease, string asset, bool expected)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = tag, prerelease, html_url = "https://github.com/Fourgetu/vibe-gauge/releases/tag/" + tag,
            assets = new[] { new { name = asset } } }));
        Assert.Equal(expected, UpdateRelease.Parse(doc.RootElement, "1.6.1") is not null);
    }
    [Fact]
    public void OldCodexHistoryCompactsWithoutChangingTotalsAfterDeleteRestoreAndCopy()
    {
        var file = Path.Combine(Paths.CodexRoot, "sessions", "old.jsonl");
        var id = Guid.NewGuid().ToString();
        var header = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } });
        var row = JsonSerializer.Serialize(new { timestamp = now.AddDays(-120), type = "event_msg", payload = new { type = "token_count", info = new {
            last_token_usage = new { input_tokens = 100, cached_input_tokens = 20, output_tokens = 10, reasoning_output_tokens = 2 } } } });
        var text = header + "\n" + row + "\n";
        Write(file, text);
        var scanner = new UsageScanner(Paths);
        void Verify(UsageScanResult scan) { Assert.Equal(110, scan.Statistics!.Days.Sum(x => x.TotalTokens)); Assert.Single(scan.Statistics.DailyModels!); }
        Verify(scanner.Scan()); Assert.Equal(1, scanner.CompactedCalls); Assert.Equal(0, scanner.HotRecordCount);
        Assert.Contains("PackedRecords", File.ReadAllText(Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName)));
        File.Delete(file); Verify(scanner.Scan());
        scanner = new(Paths); Verify(scanner.Scan());
        Write(file, header + "\n"); Verify(scanner.Scan());
        File.AppendAllText(file, row + "\n"); Verify(scanner.Scan());
        var archive = Path.Combine(Paths.CodexRoot, "archived_sessions", "copy.jsonl"); Write(archive, text); Verify(scanner.Scan());
        Verify(new UsageScanner(Paths).Scan());
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void CompactedDesktopRecordCanBeCorrected(bool sameFile)
    {
        string Row(DateTimeOffset at, int tokens) => JsonSerializer.Serialize(new { type = "message", role = "assistant", id = "same", timestamp = at,
            message = new { id = "same", model = "fixture", usage = new { input_tokens = tokens, output_tokens = 5 } } });
        var file = Path.Combine(Paths.WorkBuddyRoot, "projects", "old.jsonl");
        Write(file, Row(now.AddDays(-120), 20) + "\n");
        var scanner = new UsageScanner(Paths);
        Assert.Equal(25, scanner.Scan().Statistics!.Days.Sum(x => x.TotalTokens));
        Write(sameFile ? file : Path.Combine(Paths.WorkBuddyRoot, "projects", "new.jsonl"), Row(now.AddMinutes(-1), 30) + "\n");
        Assert.Equal(35, scanner.Scan().Statistics!.Days.Sum(x => x.TotalTokens));
        Assert.Equal(35, new UsageScanner(Paths).Scan().Statistics!.Days.Sum(x => x.TotalTokens));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void CorruptLedgerIsNotSilentlyReplacedWithEmptyHistory()
    {
        var file = Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName);
        Write(file, "damaged-retained-ledger");
        Assert.Throws<IOException>(() => new UsageScanner(Paths).Scan());
        Assert.Equal("damaged-retained-ledger", File.ReadAllText(file));
    }

    [Fact]
    public async Task AgyBridgeForwardsOriginalStatuslineWithoutReplacingItsOutput()
    {
        var settings = Path.Combine(Paths.GeminiRoot, "antigravity-cli", "settings.json");
        Write(settings, """{"statusLine":{"type":"command","command":"echo original-fixture"}}""");
        CliBridge.Configure(Paths, "C:\\fixture\\proxy.exe", true, false, "agy");
        Assert.Equal("original-fixture", (await CliBridge.ForwardOriginalAgyAsync(Paths, "{}"))?.Trim());
    }

    [Fact]
    public void LargeColdHistoryDoesNotInflateOnUnchangedRefresh()
    {
        var file = Path.Combine(Paths.WorkBuddyRoot, "projects", "old.jsonl");
        var rows = Enumerable.Range(0, 5000).Select(i => JsonSerializer.Serialize(new { type = "message", role = "assistant", id = i.ToString(),
            timestamp = now.AddDays(-120).AddSeconds(i), message = new { usage = new { input_tokens = 100, output_tokens = 10 } } }));
        Write(file, string.Join("\n", rows) + "\n");
        var scanner = new UsageScanner(Paths);
        Assert.Equal(550000, scanner.Scan().Statistics!.Days.Sum(x => x.TotalTokens));
        Assert.Equal(5000, scanner.CompactedCalls); Assert.Equal(0, scanner.HotRecordCount);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(550000, scanner.Scan().Statistics!.Days.Sum(x => x.TotalTokens));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 500000);
        Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
        Assert.Equal(5000, new UsageScanner(Paths).Scan().WorkBuddyTotal!.Turns);
    }
}
