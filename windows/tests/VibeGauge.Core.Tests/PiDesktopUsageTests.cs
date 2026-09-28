using System.Text.Json;
using System.Text.Json.Nodes;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class PiDesktopUsageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-pi-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));

    [Fact]
    public void ReadsRealPiSchemaWithoutDoubleCountingCacheOrReasoning()
    {
        using var doc = JsonDocument.Parse(Message("m1"));
        var record = Assert.IsType<InteractionRecord>(PiDesktopUsage.Parse(doc.RootElement));
        Assert.Equal("PI-Desktop", record.Source);
        Assert.Equal("pi-test-model", record.Model);
        Assert.Equal(130, record.ContextTokens);
        Assert.Equal(100, record.CacheReadTokens);
        Assert.Equal(10, record.CacheWriteTokens);
        Assert.Equal(8, record.OutputTokens);
        Assert.Equal(3, record.ThinkingTokens);
    }

    [Fact]
    public void CompactionUsageUsesItsSeparateSchema()
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "compaction",
            id = "compact-1",
            createdAt = DateTimeOffset.Now,
            modelId = "pi-compact-model",
            usage = new { input = 10, output = 5, cacheRead = 20, cacheWrite = 2, reasoning = 1 }
        });
        using var doc = JsonDocument.Parse(line);
        var record = Assert.IsType<InteractionRecord>(PiDesktopUsage.Parse(doc.RootElement));
        Assert.Equal(32, record.ContextTokens);
        Assert.Equal(5, record.OutputTokens);
        Assert.Equal(1, record.ThinkingTokens);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"message\",\"role\":\"user\"}")]
    [InlineData("{\"type\":\"message\",\"role\":\"assistant\",\"meta\":null}")]
    public void IrrelevantOrMalformedRecordsAreIgnored(string line)
    {
        using var doc = JsonDocument.Parse(line);
        Assert.Null(PiDesktopUsage.Parse(doc.RootElement));
    }

    [Theory]
    [InlineData("streaming")]
    [InlineData("pending")]
    public void IncompleteMessagesDoNotCount(string status)
    {
        using var doc = JsonDocument.Parse(Message("unfinished", status: status));
        Assert.Null(PiDesktopUsage.Parse(doc.RootElement));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("\"123\"")]
    [InlineData("9223372036854775807")]
    public void InvalidOrOverflowingUsageIsIgnored(string input)
    {
        var message = JsonNode.Parse(Message("bad-input"))!;
        message["meta"]!["usage"]!["inputTokens"] = JsonNode.Parse(input);
        using var doc = JsonDocument.Parse(message.ToJsonString());
        Assert.Null(PiDesktopUsage.Parse(doc.RootElement));
    }

    [Fact]
    public void NullableCacheAndReasoningFieldsAreOptional()
    {
        var message = JsonNode.Parse(Message("null-cache"))!;
        foreach (var key in new[] { "cacheReadTokens", "cacheWriteTokens", "reasoningTokens" })
            message["meta"]!["usage"]![key] = null;
        using var doc = JsonDocument.Parse(message.ToJsonString());
        var record = Assert.IsType<InteractionRecord>(PiDesktopUsage.Parse(doc.RootElement));
        Assert.Equal(20, record.ContextTokens);
        Assert.Equal(0, record.CacheReadTokens);
        Assert.Equal(0, record.ThinkingTokens);
    }

    [Fact]
    public void UsageAppearsInTodayRecentHistoryAndCumulativeButNotApi()
    {
        Write("a", Message("today"), Message("yesterday", DateTimeOffset.Now.AddDays(-1)), Message("future", DateTimeOffset.Now.AddDays(1)));
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(1, result.Cli.Turns);
        Assert.Equal(130, result.Cli.ContextTokens);
        Assert.Equal("PI-Desktop", Assert.Single(result.Cli.Recent).Source);
        Assert.Equal(2, result.PiDesktopTotal!.Turns);
        Assert.Equal(260, result.PiDesktopTotal.ContextTokens);
        Assert.Equal(2, result.Statistics!.Days.Sum(x => x.Calls));
        Assert.Contains(result.Statistics.Models, x => x.Source == "PI-Desktop");
        Assert.DoesNotContain(result.Cli.Sources, x => x.Name == "Grok");
        Assert.Equal(0, result.Api.Calls);
    }

    [Fact]
    public void DuplicateMessageIdsAcrossLogsCountOnceEvenAfterRestart()
    {
        Write("a", Message("same"));
        Write("copy", Message("same"));
        Assert.Equal(1, new UsageScanner(Paths).ScanToday().Turns);
        Assert.Equal(1, new UsageScanner(Paths).ScanToday().Turns);
    }

    [Fact]
    public void IncrementalScanWaitsForCompleteLinesAndDoesNotRereadUnchangedFiles()
    {
        var path = Write("a", Message("first"));
        var scanner = new UsageScanner(Paths);
        Assert.Equal(1, scanner.ScanToday().Turns);
        scanner.Scan();
        Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
        var next = Message("second");
        File.AppendAllText(path, next[..(next.Length / 2)]);
        Assert.Equal(1, scanner.ScanToday().Turns);
        File.AppendAllText(path, next[(next.Length / 2)..] + "\n");
        Assert.Equal(2, scanner.ScanToday().Turns);
    }

    [Fact]
    public void RewrittenLogsReplacePreviousContributionAndBadLinesDoNotBlockGoodOnes()
    {
        var path = Write("a", Message("one"), Message("two"));
        var scanner = new UsageScanner(Paths);
        Assert.Equal(2, scanner.ScanToday().Turns);
        File.WriteAllText(path, "not json\n" + Message("replacement") + "\n");
        Assert.Equal(1, scanner.ScanToday().Turns);
        Assert.Equal(1, scanner.Scan().PiDesktopTotal!.Turns);
    }

    [Fact]
    public void PiCardReplacesGrokAndShowsHistoricalUsageEvenWithoutCallsToday()
    {
        Write("a", Message("past", DateTimeOffset.Now.AddDays(-1)));
        var usage = new UsageScanner(Paths).Scan();
        var cards = new QuotaScanner(Paths).Scan(ProcessReport.Empty with { PiDesktopProcesses = 1 }, usage.Cli, usage.PiDesktopTotal);
        var pi = Assert.Single(cards, x => x.Name == "PI-Desktop");
        Assert.True(pi.IsRunning);
        Assert.Equal(ProviderDataState.Available, pi.DataState);
        Assert.Contains("今日 0 次", pi.Detail);
        Assert.Contains("本地累计 1 次", pi.Detail);
        Assert.Null(pi.FiveHour);
        Assert.Null(pi.Weekly);
        Assert.DoesNotContain(cards, x => x.Name == "Grok");
    }

    [Fact]
    public void MissingDataDoesNotPretendToKnowQuotaOrLoginState()
    {
        var usage = new UsageScanner(Paths).Scan();
        var source = Assert.Single(usage.Cli.Sources, x => x.Name == "PI-Desktop");
        Assert.Equal(UsageDataState.NotDetected, source.State);
        var pi = new QuotaScanner(Paths).Scan(ProcessReport.Empty, usage.Cli, usage.PiDesktopTotal).Single(x => x.Name == "PI-Desktop");
        Assert.Equal("未检测到本地会话日志", pi.Detail);
        Assert.Equal(ProviderDataState.NoQuota, pi.DataState);
        Assert.False(pi.IsRunning);
    }

    private string Write(string name, params string[] lines)
    {
        Directory.CreateDirectory(Paths.PiDesktopSessions);
        var path = Path.Combine(Paths.PiDesktopSessions, name + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Message(string id, DateTimeOffset? at = null, string status = "complete") =>
        JsonSerializer.Serialize(new
        {
            type = "message",
            role = "assistant",
            id,
            createdAt = at ?? DateTimeOffset.Now,
            meta = new
            {
                status,
                modelId = "pi-test-model",
                usage = new
                {
                    inputTokens = 20,
                    outputTokens = 8,
                    cacheReadTokens = 100,
                    cacheWriteTokens = 10,
                    reasoningTokens = 3,
                    totalTokens = 138
                }
            }
        });

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
