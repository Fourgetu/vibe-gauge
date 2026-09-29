using System.Text;
using System.Text.Json;
using VibeGauge.Core;
using Xunit;
using ZstdSharp;

namespace VibeGauge.Core.Tests;

public sealed class DesktopClientUsageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-clients-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private static readonly DateTimeOffset At = DateTimeOffset.Now.AddMinutes(-1);
    private static string Buddy(string id = "call", long input = 100, long output = 20, long think = 5, long total = 120,
        string type = "function_call", string status = "completed", DateTimeOffset? at = null) => JsonSerializer.Serialize(new
        {
            id = "tool-" + id, sessionId = "session", type, role = "assistant", status,
            timestamp = (at ?? At).ToUnixTimeMilliseconds(),
            providerData = new { messageId = id, model = "buddy-model", rawUsage = new { completion_tokens_details = new { reasoning_tokens = think } } },
            message = new { usage = new { input_tokens = input, output_tokens = output, cache_read_input_tokens = 80, total_tokens = total } }
        }) + "\n";
    private static string Header(string id = "dsh-session", bool seeded = false) => JsonSerializer.Serialize(new { type = "session", version = 4, id, isSeeded = seeded }) + "\n";
    private static string Event(string type, long seq, object data, DateTimeOffset? at = null) => JsonSerializer.Serialize(new
        { type, seq, time = (at ?? At).ToUnixTimeMilliseconds(), data }) + "\n";
    private static object Usage(long input = 100, long output = 20) => new { inputTokens = input, outputTokens = output, cacheReadTokens = 80, cacheWriteTokens = 10 };
    private static string DshCall(long seq = 2, long step = 1, long input = 100, DateTimeOffset? at = null) =>
        Event("assistant/message", seq, new { turn = 1, step, usage = Usage(input) }, at);
    private string Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    private static IReadOnlyList<InteractionRecord> ParseDsh(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return DshUsage.Read(stream);
    }

    [Theory]
    [InlineData("function_call", 120, 20)]
    [InlineData("message", 120, 20)]
    [InlineData("message", 125, 25)]
    public void WorkBuddyUsesReportedTotalToNormalizeReasoningWithoutDoublingCache(string type, long total, long output)
    {
        using var doc = JsonDocument.Parse(Buddy(type: type, total: total));
        var r = Assert.IsType<InteractionRecord>(WorkBuddyUsage.Parse(doc.RootElement));
        Assert.Equal(100, r.ContextTokens);
        Assert.Equal(output, r.OutputTokens);
        Assert.Equal(total, r.TotalTokens);
        Assert.Equal(80, r.CacheReadTokens);
        Assert.Equal(5, r.ThinkingTokens);
        Assert.Equal("session:call", r.Id);
        Assert.Equal("buddy-model", r.Model);
    }

    [Theory]
    [InlineData(-1, 20, "completed")]
    [InlineData(100, -1, "completed")]
    [InlineData(100, 20, "pending")]
    [InlineData(100, 20, "streaming")]
    [InlineData(long.MaxValue, 20, "completed")]
    public void WorkBuddyRejectsInvalidOrUnfinishedUsage(long input, long output, string status)
    {
        using var doc = JsonDocument.Parse(Buddy(input: input, output: output, status: status));
        Assert.Null(WorkBuddyUsage.Parse(doc.RootElement));
    }

    [Fact]
    public void WorkBuddyDeduplicatesToolsCopiesAndSurvivesDeletionRewriteAndRestart()
    {
        var path = Write(".workbuddy/projects/p/session.jsonl", Buddy() + Buddy(type: "message"));
        var copy = Write(".workbuddy-ai/projects/p/session.jsonl", Buddy());
        var scanner = new UsageScanner(Paths);
        Assert.Equal(120, scanner.Scan().WorkBuddyTotal!.TotalTokens);
        scanner.Scan();
        Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
        File.AppendAllText(path, Buddy("second", at: At.AddDays(-2)));
        var both = scanner.Scan();
        Assert.Equal(240, both.WorkBuddyTotal!.TotalTokens);
        Assert.Equal(120, both.Cli.TotalTokens);
        Assert.Equal(2, both.Statistics!.Days.Sum(x => x.Calls));
        File.WriteAllText(path, Buddy("second", at: At.AddDays(-2)));
        File.Delete(copy);
        Assert.Equal(240, scanner.Scan().WorkBuddyTotal!.TotalTokens);
        File.Delete(path);
        Assert.Equal(240, new UsageScanner(Paths).Scan().WorkBuddyTotal!.TotalTokens);
        Write(".workbuddy/projects/p/session.jsonl", Buddy());
        Assert.Equal(240, new UsageScanner(Paths).Scan().WorkBuddyTotal!.TotalTokens);
    }

    [Fact]
    public void DshReadsEveryCompressedFrameAndIncludesCacheOnce()
    {
        var path = Write(".dsh/sessions/p/s/session.v4.jsonl.zstd", "");
        using (var compressor = new Compressor())
        using (var stream = File.Create(path))
            foreach (var line in new[] { Header(), Event("request/header", 1, new { header = new { config = new { model = "dsh-model" } } }), DshCall(), DshCall(3, 2) })
                stream.Write(compressor.Wrap(Encoding.UTF8.GetBytes(line)));
        var records = DshUsage.Read(path);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => { Assert.Equal(190, r.ContextTokens); Assert.Equal(20, r.OutputTokens); Assert.Equal(210, r.TotalTokens); Assert.Equal("dsh-model", r.Model); });
    }

    [Fact]
    public void DshReplacesSameAttemptAndCountsRetriedRequestsSeparately()
    {
        var content = Header() + Event("model/selection", 1, new { model = "dsh-model" }) +
            Event("assistant/attempt", 2, new { turn = 1, step = 1, stream = new[] {
                new { type = "chunk", chunk = new { type = "usage", usage = Usage(10) } },
                new { type = "chunk", chunk = new { type = "usage", usage = Usage(50) } } } }) +
            DshCall(3, 1, 100) + Event("llm/retry-started", 4, new { turn = 1, step = 1 }) + DshCall(5, 1, 200);
        var records = ParseDsh(content);
        Assert.Equal(2, records.Count);
        Assert.Equal(520, records.Sum(x => x.TotalTokens));
        Assert.Equal(new[] { "dsh-session:2", "dsh-session:5" }, records.Select(x => x.Id));
    }

    [Fact]
    public void DshForkDoesNotCountInheritedContextAgain()
    {
        var records = ParseDsh(Header(seeded: true) + DshCall() + Event("session/end-seed", 3, new { }) + DshCall(4, 2));
        Assert.Equal("dsh-session:4", Assert.Single(records).Id);
    }

    [Fact]
    public void DshPartialOrCorruptFilesKeepPriorHistoryThenRecover()
    {
        var content = Header() + DshCall();
        var path = Write(".dsh/sessions/p/s/session.v4.jsonl", content);
        var scanner = new UsageScanner(Paths);
        Assert.Equal(210, scanner.Scan().DshTotal!.TotalTokens);
        scanner.Scan();
        Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
        File.WriteAllText(path, content + DshCall(3, 2).TrimEnd());
        var failed = scanner.Scan();
        Assert.Equal(210, failed.DshTotal!.TotalTokens);
        Assert.Equal(UsageDataState.ReadFailed, failed.DshTotal.State);
        File.AppendAllText(path, "\n");
        Assert.Equal(420, scanner.Scan().DshTotal!.TotalTokens);
        File.WriteAllText(path, "corrupt\n");
        Assert.Equal(420, scanner.Scan().DshTotal!.TotalTokens);
        File.Delete(path);
        Assert.Equal(420, new UsageScanner(Paths).Scan().DshTotal!.TotalTokens);
        Write(".dsh/sessions/p/s/session.v4.jsonl", content);
        Assert.Equal(420, new UsageScanner(Paths).Scan().DshTotal!.TotalTokens);
    }

    [Fact]
    public void DshCopiedFilesDoNotDoubleCountAndDatesRemainLinked()
    {
        var content = Header() + DshCall() + DshCall(3, 2, at: At.AddDays(-2)) + DshCall(4, 3, at: At.AddDays(2));
        Write(".dsh/sessions/p/s/session.v4.jsonl", content);
        Write(".dsh/sessions/copy/s/session.v4.jsonl", content);
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(210, result.Cli.TotalTokens);
        Assert.Equal(420, result.DshTotal!.TotalTokens);
        Assert.Equal(2, result.Statistics!.Days.Sum(x => x.Calls));
        Assert.Equal(0, result.Api.Calls);
    }

    [Fact]
    public void DshTornCompressedFrameRetriesWithoutErasingTheLastSnapshot()
    {
        var path = Write(".dsh/sessions/p/s/session.v4.jsonl.zstd", "");
        using var compressor = new Compressor();
        var complete = compressor.Wrap(Encoding.UTF8.GetBytes(Header() + DshCall())).ToArray();
        var next = compressor.Wrap(Encoding.UTF8.GetBytes(DshCall(3, 2))).ToArray();
        File.WriteAllBytes(path, complete);
        var scanner = new UsageScanner(Paths);
        Assert.Equal(210, scanner.Scan().DshTotal!.TotalTokens);
        File.WriteAllBytes(path, complete.Concat(next.Take(next.Length / 2)).ToArray());
        var failed = scanner.Scan();
        Assert.Equal(210, failed.DshTotal!.TotalTokens);
        Assert.Equal(UsageDataState.ReadFailed, failed.DshTotal.State);
        Assert.Equal(UsageDataState.NotDetected, failed.Api.State);
        File.WriteAllBytes(path, complete.Concat(next).ToArray());
        Assert.Equal(420, scanner.Scan().DshTotal!.TotalTokens);
    }

    [Fact]
    public void DshInvalidNegativeAndOverflowCountsAreExcluded()
    {
        var content = Header() + DshCall(input: -1) + DshCall(3, 2, long.MaxValue) +
            Event("assistant/message", 4, new { turn = 1, step = 3, usage = new { inputTokens = 100, outputTokens = 20, cacheReadTokens = -1 } });
        Assert.Empty(ParseDsh(content));
    }

    [Fact]
    public void BothClientsHaveNumericCardsAndApiFailuresStaySeparate()
    {
        Write(".workbuddy/projects/p/s.jsonl", Buddy());
        Write(".dsh/sessions/p/s/session.v4.jsonl", Header() + DshCall());
        var scan = new UsageScanner(Paths).Scan();
        var cards = new QuotaScanner(Paths).Scan(ProcessReport.Empty with { WorkBuddyProcesses = 1, DshProcesses = 1 },
            scan.Cli, scan.PiDesktopTotal, scan.ZCodeTotal, scan.WorkBuddyTotal, scan.DshTotal);
        foreach (var name in new[] { WorkBuddyUsage.SourceName, DshUsage.SourceName })
        {
            var card = cards.Single(x => x.Name == name);
            Assert.True(card.IsRunning);
            Assert.NotNull(card.DesktopTokens);
            Assert.Contains("精确总 Token", card.Detail);
        }
        Assert.Equal(330, scan.Cli.TotalTokens);
        Assert.Equal(UsageDataState.NotDetected, scan.Api.State);
    }

    [Theory]
    [InlineData("{\"type\":\"session\",\"version\":5,\"id\":\"s\"}\n")]
    [InlineData("{\"type\":\"assistant/message\",\"data\":{}}\n")]
    public void DshUnknownFormatsAreNotPresentedAsValidZeroUsage(string content) => Assert.Throws<InvalidDataException>(() => ParseDsh(content));

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
