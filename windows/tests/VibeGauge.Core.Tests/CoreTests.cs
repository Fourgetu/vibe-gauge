using System.Text.Json;
using System.Text.Json.Nodes;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingSelfTestPasses() => SelfTest.Run();

    [Fact]
    public void ExpiredQuotaReadsAsZero()
    {
        var now = DateTimeOffset.Now;
        var window = new QuotaWindow(91, now.AddSeconds(-1), now.AddMinutes(-5), TimeSpan.FromHours(5));
        Assert.Equal(0, window.EffectivePercent(now));
    }

    [Fact]
    public void ClaudeQuotaAndTierAreReadFromFixtures()
    {
        Directory.CreateDirectory(Path.Combine(root, ".claude"));
        File.WriteAllText(Path.Combine(root, ".claude.json"), "{\"oauthAccount\":{\"organizationRateLimitTier\":\"default_claude_max_20x\"}}");
        var reset = DateTimeOffset.Now.AddHours(2).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(root, ".claude", "claude-usage.json"), JsonSerializer.Serialize(new
        {
            _captured_at = DateTimeOffset.Now.ToUnixTimeSeconds(),
            five_hour = new { used_percentage = 42, resets_at = reset },
            seven_day = new { used_percentage = 17, resets_at = reset + 86400 }
        }));
        var scanner = new QuotaScanner(new AppPaths(root, Path.Combine(root, "local")));
        var report = ProcessReport.Empty with { ClaudeSessions = 1 };
        var claude = scanner.Scan(report).Single(x => x.Name == "Claude");
        Assert.Equal("Max 20x", claude.Tier);
        Assert.True(claude.IsRunning);
        Assert.Equal(42, claude.FiveHour?.UsedPercent);
        Assert.Equal(17, claude.Weekly?.UsedPercent);
    }

    [Fact]
    public void UsageDeduplicatesForkedClaudeRequests()
    {
        var project = Path.Combine(root, ".claude", "projects", "fixture");
        Directory.CreateDirectory(project);
        var timestamp = DateTimeOffset.Now.ToString("O");
        var line = $"{{\"type\":\"assistant\",\"timestamp\":\"{timestamp}\",\"requestId\":\"same\",\"message\":{{\"model\":\"fixture\",\"usage\":{{\"input_tokens\":10,\"cache_read_input_tokens\":20,\"output_tokens\":4}}}}}}";
        File.WriteAllText(Path.Combine(project, "a.jsonl"), line + Environment.NewLine);
        File.WriteAllText(Path.Combine(project, "b.jsonl"), line + Environment.NewLine);
        var usage = new UsageScanner(new AppPaths(root, Path.Combine(root, "local"))).ScanToday();
        Assert.Equal(1, usage.Turns);
        Assert.Equal(30, usage.ContextTokens);
        Assert.Equal(20, usage.CacheReadTokens);
    }

    [Fact]
    public void CacheHitMatchesSwiftFixtureDefinition()
    {
        var project = Path.Combine(root, ".claude", "projects", "fixture");
        Directory.CreateDirectory(project);
        var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "UsageParity"));
        var timestamp = DateTimeOffset.Now.ToString("O");
        File.WriteAllLines(Path.Combine(project, "usage.jsonl"),
            File.ReadLines(Path.Combine(fixtureRoot, "claude.jsonl")).Select(line =>
            {
                var record = JsonNode.Parse(line)!.AsObject();
                record["timestamp"] = timestamp;
                return record.ToJsonString();
            }));
        using var expectedDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "expected.json")));
        var expected = expectedDocument.RootElement;

        var usage = new UsageScanner(new AppPaths(root, Path.Combine(root, "local"))).ScanToday();

        Assert.Equal(expected.GetProperty("contextTokens").GetInt64(), usage.ContextTokens);
        Assert.Equal(expected.GetProperty("cacheReadTokens").GetInt64(), usage.CacheReadTokens);
        Assert.Equal(expected.GetProperty("cacheWriteTokens").GetInt64(), usage.CacheWriteTokens);
        Assert.Equal(expected.GetProperty("outputTokens").GetInt64(), usage.OutputTokens);
        Assert.Equal(expected.GetProperty("thinkingTokens").GetInt64(), usage.ThinkingTokens);
        Assert.Equal(expected.GetProperty("cacheHitRate").GetDouble(), usage.CacheHitRate!.Value, 8);
    }

    [Fact]
    public void IncrementalReaderConsumesOnlyAppendsAndWaitsForCompleteLines()
    {
        var project = Path.Combine(root, ".claude", "projects", "fixture");
        Directory.CreateDirectory(project);
        var path = Path.Combine(project, "usage.jsonl");
        File.WriteAllText(path, ClaudeLine("first", 10, 2, 3, 4, 0) + Environment.NewLine);
        var scanner = new UsageScanner(new AppPaths(root, Path.Combine(root, "local")));
        Assert.Equal(1, scanner.ScanToday().Turns);
        var firstBytes = scanner.LastDiagnostics.BytesRead;

        var second = ClaudeLine("second", 7, 1, 2, 3, 0);
        var split = second.Length / 2;
        File.AppendAllText(path, second[..split]);
        Assert.Equal(1, scanner.ScanToday().Turns);
        Assert.True(scanner.LastDiagnostics.BytesRead < firstBytes);

        File.AppendAllText(path, second[split..] + Environment.NewLine);
        var usage = scanner.ScanToday();
        Assert.Equal(2, usage.Turns);
        Assert.Equal("second", usage.Recent[0].Id);
    }

    [Fact]
    public void CodexCumulativeSnapshotsUseNonNegativeDelta()
    {
        var sessions = Path.Combine(root, ".codex", "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "fixture.jsonl");
        var timestamp = DateTimeOffset.Now.ToString("O");
        var context = JsonSerializer.Serialize(new { type = "turn_context", timestamp, payload = new { model = "gpt-fixture" } });
        var first = CodexLine(timestamp, totalInput: 100, totalOutput: 20, lastInput: 100, lastOutput: 20);
        var second = CodexLine(timestamp, totalInput: 150, totalOutput: 30);
        var duplicate = CodexLine(timestamp, totalInput: 150, totalOutput: 30);
        File.WriteAllLines(path, [context, first, second, duplicate]);

        var usage = new UsageScanner(new AppPaths(root, Path.Combine(root, "local"))).ScanToday();

        Assert.Equal(2, usage.Turns);
        Assert.Equal(150, usage.ContextTokens);
        Assert.Equal(30, usage.OutputTokens);
    }

    [Fact]
    public void RewrittenLogRevokesOldContribution()
    {
        var project = Path.Combine(root, ".claude", "projects", "fixture");
        Directory.CreateDirectory(project);
        var path = Path.Combine(project, "usage.jsonl");
        var scanner = new UsageScanner(new AppPaths(root, Path.Combine(root, "local")));
        File.WriteAllText(path, ClaudeLine("old", 90, 0, 0, 1, 0) + Environment.NewLine);
        Assert.Equal(90, scanner.ScanToday().ContextTokens);

        File.WriteAllText(path, ClaudeLine("new", 8, 0, 0, 1, 0) + Environment.NewLine);
        var usage = scanner.ScanToday();

        Assert.Equal(1, usage.Turns);
        Assert.Equal(8, usage.ContextTokens);
        Assert.Equal("new", usage.Recent[0].Id);
    }

    [Fact]
    public void ApiUsageAggregatesRangesProvidersModelsErrorsAndLatency()
    {
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var now = DateTimeOffset.Now;
        var records = new[]
        {
            ApiLine(now, "DeepSeek", "deepseek-chat", 100, 60, 0, 20, 4, 200, 100),
            ApiLine(now.AddMinutes(-1), "DeepSeek", "deepseek-chat", 50, 10, 2, 8, 1, 429, 300),
            ApiLine(now.AddDays(-10), "OpenRouter", "open/model", 30, 0, 0, 5, 0, 200, 50)
        };
        File.WriteAllLines(Path.Combine(local, "api-calls.jsonl"), records);

        var api = new UsageScanner(new AppPaths(root, local)).Scan().Api;

        var today = Assert.Single(api.Ranges!, x => x.Key == "today");
        Assert.Equal(2, today.Calls);
        Assert.Equal(150, today.ContextTokens);
        Assert.Equal(70, today.CacheReadTokens);
        Assert.Equal(2, today.CacheWriteTokens);
        Assert.Equal(28, today.OutputTokens);
        Assert.Equal(5, today.ThinkingTokens);
        Assert.Equal(1, today.Errors);
        Assert.Equal(200, today.AverageLatencyMs);
        var provider = Assert.Single(today.Providers);
        Assert.Equal("DeepSeek", provider.Name);
        Assert.Equal(2, provider.Calls);
        var model = Assert.Single(today.Models);
        Assert.Equal("deepseek-chat", model.Model);
        Assert.Equal(1, model.Errors);
        Assert.Equal(3, Assert.Single(api.Ranges!, x => x.Key == "all").Calls);
        Assert.Equal(3, api.Recent!.Count);
        Assert.Equal(429, api.Recent[1].Status);
    }

    private static string ClaudeLine(string id, long input, long cacheRead, long cacheWrite, long output, long thinking) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = DateTimeOffset.Now.ToString("O"),
            requestId = id,
            message = new
            {
                model = "claude-opus-4-1",
                usage = new
                {
                    input_tokens = input,
                    cache_read_input_tokens = cacheRead,
                    cache_creation_input_tokens = cacheWrite,
                    output_tokens = output,
                    output_tokens_details = new { thinking_tokens = thinking }
                }
            }
        });

    private static string CodexLine(string timestamp, long totalInput, long totalOutput, long? lastInput = null, long? lastOutput = null)
    {
        object? last = lastInput is null ? null : new
        {
            input_tokens = lastInput.Value,
            cached_input_tokens = 0,
            cache_write_input_tokens = 0,
            output_tokens = lastOutput ?? 0,
            reasoning_output_tokens = 0
        };
        return JsonSerializer.Serialize(new
        {
            type = "event_msg",
            timestamp,
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new
                    {
                        input_tokens = totalInput,
                        cached_input_tokens = 0,
                        cache_write_input_tokens = 0,
                        output_tokens = totalOutput,
                        reasoning_output_tokens = 0
                    },
                    last_token_usage = last
                }
            }
        });
    }

    private static string ApiLine(
        DateTimeOffset timestamp,
        string provider,
        string model,
        long context,
        long cacheRead,
        long cacheWrite,
        long output,
        long thinking,
        int status,
        int latency) =>
        JsonSerializer.Serialize(new
        {
            ts = timestamp.ToString("O"),
            host = provider.ToLowerInvariant() + ".example",
            provider,
            model,
            ctx = context,
            cache_read = cacheRead,
            cache_write = cacheWrite,
            @out = output,
            think = thinking,
            status,
            ms = latency
        });

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }
}
