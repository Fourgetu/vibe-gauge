using System.Text.Json;
using System.Text.Json.Nodes;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ClaudeDesktopUsageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-desktop-usage-" + Guid.NewGuid().ToString("N"));
    private const string Account = "11111111-2222-3333-4444-555555555555";
    private const string Org = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly DateTimeOffset At = DateTimeOffset.Now.AddSeconds(-20);
    private AppPaths Paths => new(root, Path.Combine(root, "vibegauge"));
    private string Profile => Path.Combine(Paths.WindowsLocalDataRoot, "Claude-Data", "claude_pzs8sxrjxfjjc");
    private string Ledger(string account = Account, string org = Org) => Path.Combine(Profile, "local-agent-mode-sessions", account, org, "usage-ledger");
    private void SignIn(string account = Account, bool signedIn = true)
    {
        Directory.CreateDirectory(Profile);
        File.WriteAllText(Path.Combine(Profile, "config.json"), JsonSerializer.Serialize(new { windowSizeWasSignedIn = signedIn, lastKnownAccountUuid = account }));
    }
    private static string Line(long input = 100, string surface = "chat", string session = "session-1", DateTimeOffset? at = null) => JsonSerializer.Serialize(new
    {
        v = 1, ts = (at ?? At).ToUnixTimeMilliseconds(), surface, origin = "turn", sessionId = session,
        models = new Dictionary<string, object> { ["claude-model"] = new { inputTokens = input, outputTokens = 20, cacheReadTokens = 70, cacheWriteTokens = 10, webSearchRequests = 0 } }
    }) + "\n";
    private string Write(string text, string file = "2026-11-15.ndjson", string account = Account, string org = Org)
    {
        var path = Path.Combine(Ledger(account, org), file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    private static InteractionRecord? Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return ClaudeDesktopUsage.Parse(doc.RootElement, "test-scope");
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("cowork")]
    [InlineData("code")]
    public void ReadsActualCountersIncludingCachesAndDoesNotPersistRawSession(string surface)
    {
        var row = Assert.IsType<InteractionRecord>(Parse(Line(surface: surface)));
        Assert.Equal(200, row.TotalTokens);
        Assert.Equal(180, row.ContextTokens);
        Assert.Equal(70, row.CacheReadTokens);
        Assert.Equal(10, row.CacheWriteTokens);
        Assert.Equal(20, row.OutputTokens);
        Assert.True(row.UsageKnown);
        Assert.Equal(surface, row.UsageSurface);
        Assert.DoesNotContain("session-1", JsonSerializer.Serialize(row));
    }

    [Theory]
    [InlineData("inputTokens", "-1")]
    [InlineData("outputTokens", "null")]
    [InlineData("cacheReadTokens", "\"100\"")]
    [InlineData("cacheWriteTokens", "1.5")]
    [InlineData("inputTokens", "9007199254740992")]
    public void InvalidOrMissingCountersAreNeverZeroUsage(string key, string value)
    {
        var doc = JsonNode.Parse(Line())!;
        doc["models"]!["claude-model"]![key] = JsonNode.Parse(value);
        Assert.Null(Parse(doc.ToJsonString()));
        doc["models"]!["claude-model"]!.AsObject().Remove(key);
        Assert.Null(Parse(doc.ToJsonString()));
    }

    [Fact]
    public void RejectsUnsupportedVersionSurfaceTimestampAndEmptyModels()
    {
        foreach (var (key, value) in new[] { ("v", "2"), ("surface", "\"other\""), ("ts", "-1"), ("ts", "253402300800000"), ("models", "{}") })
        {
            var doc = JsonNode.Parse(Line())!; doc[key] = JsonNode.Parse(value);
            Assert.Null(Parse(doc.ToJsonString()));
        }
    }

    [Fact]
    public void MultipleModelsAreOneLedgerEventAndCostIsNotATokenSource()
    {
        var doc = JsonNode.Parse(Line())!;
        doc["models"]!["other"] = doc["models"]!["claude-model"]!.DeepClone();
        doc["models"]!["other"]!["cost"] = JsonNode.Parse("{\"usd\":999,\"basis\":\"list\"}");
        var row = Assert.IsType<InteractionRecord>(Parse(doc.ToJsonString()));
        Assert.Equal(400, row.TotalTokens);
        Assert.Equal("claude-model / other", row.Model);
    }

    [Fact]
    public void UsageScannerOmitsClaudeDesktopSourceAndReadsClaudeCodeLogs()
    {
        SignIn(); Write(Line(surface: "code"));
        var cli = Path.Combine(Paths.ClaudeRoot, "projects", "sample.jsonl"); Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllText(cli, JsonSerializer.Serialize(new { type = "assistant", sessionId = "session-1", requestId = "request", timestamp = At,
            message = new { model = "claude-model", usage = new { input_tokens = 100, output_tokens = 20, cache_read_input_tokens = 70, cache_creation_input_tokens = 10 } } }) + "\n");
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(200, result.Cli.TotalTokens);
        Assert.DoesNotContain(result.Cli.Sources, x => x.Name == ClaudeDesktop.Name);
        Assert.Contains(result.Cli.Sources, x => x.Name == "Claude Code" && x.TotalTokens == 200);
        Assert.Empty(result.ClaudeDesktopRecords!);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
