using System.Text.Json;
using System.Text.Json.Nodes;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class UsageRetentionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VibeGauge-retention-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now = DateTimeOffset.Now;
    private AppPaths Paths => new(root, Path.Combine(root, "local"));
    private const string Session = "414443b3-64fb-496b-8af1-69520931d318";

    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("API")]
    public void DeletedLogsRetainAllViewsAcrossRestart(string source)
    {
        var path = Write(source, [Line(source, 0, now.AddDays(-2)), Line(source, 1), Line(source, 2), Line(source, 3)]);
        var scanner = new UsageScanner(Paths);
        Verify(scanner.Scan(), source, today: 3, all: 4);
        File.Delete(path);
        Verify(scanner.Scan(), source, today: 3, all: 4);
        Verify(new UsageScanner(Paths).Scan(), source, today: 3, all: 4);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("API")]
    public void TruncationReplayAndNewAppendsDoNotSubtractOrDuplicateUsage(string source)
    {
        var original = new[] { Line(source, 1), Line(source, 2), Line(source, 3) };
        var path = Write(source, original);
        var scanner = new UsageScanner(Paths);
        Verify(scanner.Scan(), source, 3, 3);
        File.WriteAllText(path, "");
        Verify(scanner.Scan(), source, 3, 3);
        Write(source, original);
        Verify(scanner.Scan(), source, 3, 3);
        File.AppendAllText(path, Line(source, 4) + "\n");
        Verify(scanner.Scan(), source, 4, 4);
        Write(source, [Line(source, 3), Line(source, 4)]);
        Verify(scanner.Scan(), source, 4, 4);
        File.AppendAllText(path, Line(source, 5) + "\n");
        Verify(scanner.Scan(), source, 5, 5);
        File.Delete(path);
        Verify(new UsageScanner(Paths).Scan(), source, 5, 5);
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    public void RemovingWholeSourceDirectoryDoesNotRemoveUsageOrRecreateIt(string source)
    {
        var path = Write(source, [Line(source, 1)]);
        new UsageScanner(Paths).Scan();
        var directory = source == "Claude" ? Paths.ClaudeRoot : Paths.CodexRoot;
        Directory.Delete(directory, true);
        Verify(new UsageScanner(Paths).Scan(), source, 1, 1);
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("API")]
    public void ReplayMatchingSurvivesRestartAndPartialRestore(string source)
    {
        var path = Write(source, [Line(source, 1), Line(source, 2)]);
        var scanner = new UsageScanner(Paths);
        Verify(scanner.Scan(), source, 2, 2);
        File.WriteAllText(path, "");
        Verify(scanner.Scan(), source, 2, 2);
        scanner = new UsageScanner(Paths);
        Write(source, [Line(source, 1)]);
        Verify(scanner.Scan(), source, 2, 2);
        scanner = new UsageScanner(Paths);
        File.AppendAllText(path, Line(source, 2) + "\n" + Line(source, 3) + "\n");
        Verify(scanner.Scan(), source, 3, 3);
        Verify(new UsageScanner(Paths).Scan(), source, 3, 3);
    }

    [Fact]
    public void ArchivedCodexSessionsAreReadEvenWhenNeverPreviouslyScanned()
    {
        var path = Write("Codex", [Line("Codex", 1), Line("Codex", 2)]);
        var archive = ArchivePath(path);
        File.Move(path, archive);
        Verify(new UsageScanner(Paths).Scan(), "Codex", 2, 2);
    }

    [Fact]
    public void MovingCopyingAndRestoringCodexSessionDoesNotDoubleCount()
    {
        var path = Write("Codex", [Line("Codex", 1), Line("Codex", 2)]);
        var scanner = new UsageScanner(Paths);
        Verify(scanner.Scan(), "Codex", 2, 2);
        var archive = ArchivePath(path);
        File.Move(path, archive);
        Verify(scanner.Scan(), "Codex", 2, 2);
        File.Copy(archive, path);
        var copy = Path.Combine(Path.GetDirectoryName(path)!, "renamed-copy.jsonl");
        File.Copy(path, copy);
        Verify(scanner.Scan(), "Codex", 2, 2);
        File.AppendAllText(path, Line("Codex", 3) + "\n");
        Verify(scanner.Scan(), "Codex", 3, 3);
        File.Delete(path);
        File.Delete(archive);
        File.Delete(copy);
        Verify(new UsageScanner(Paths).Scan(), "Codex", 3, 3);
    }

    [Fact]
    public void DifferentCodexSessionsWithSameTimestampsAndUsageRemainDistinct()
    {
        var line = Line("Codex", 1);
        Write("Codex", [line]);
        Write("Codex", [line], "other.jsonl", "different-session");
        var result = new UsageScanner(Paths).Scan();
        Verify(result, "Codex", 2, 2);
        Assert.Equal(2, result.Cli.Recent.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void IdenticalLegitimateEventsWithinOneFileSurviveReplayAndArchiveCopy()
    {
        var record = JsonNode.Parse(Line("Codex", 1))!;
        record["payload"]!["info"]!.AsObject().Remove("total_token_usage");
        var line = record.ToJsonString();
        var path = Write("Codex", [line, line]);
        var scanner = new UsageScanner(Paths);
        Verify(scanner.Scan(), "Codex", 2, 2);
        File.WriteAllText(path, "");
        Verify(scanner.Scan(), "Codex", 2, 2);
        Write("Codex", [line, line]);
        File.Copy(path, ArchivePath(path));
        Verify(scanner.Scan(), "Codex", 2, 2);
        Verify(new UsageScanner(Paths).Scan(), "Codex", 2, 2);
    }

    [Fact]
    public void CodexCumulativeOnlyDeltasKeepBaselineAfterRewriteAndAppend()
    {
        var second = JsonNode.Parse(Line("Codex", 2))!;
        second["payload"]!["info"]!.AsObject().Remove("last_token_usage");
        var third = JsonNode.Parse(Line("Codex", 3))!;
        third["payload"]!["info"]!.AsObject().Remove("last_token_usage");
        var path = Write("Codex", [Line("Codex", 1), second.ToJsonString()]);
        var scanner = new UsageScanner(Paths);
        Verify(scanner.Scan(), "Codex", 2, 2);
        File.WriteAllText(path, "");
        scanner.Scan();
        Write("Codex", [Line("Codex", 1), second.ToJsonString()]);
        Verify(scanner.Scan(), "Codex", 2, 2);
        File.AppendAllText(path, third.ToJsonString() + "\n");
        Verify(scanner.Scan(), "Codex", 3, 3);
    }

    [Fact]
    public void VersionOneCacheAndRestoredRolloutAreDeduplicatedWithoutLosingHistory()
    {
        var path = Write("Codex", [Line("Codex", 1), Line("Codex", 2)]);
        new UsageScanner(Paths).Scan();
        var currentCache = Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName);
        var cachePath = Path.Combine(Paths.LocalDataRoot, "usage-incremental.json");
        File.Move(currentCache, cachePath);
        var cache = JsonNode.Parse(File.ReadAllText(cachePath))!;
        cache["Version"] = 1;
        var file = cache["Files"]![path]!.AsObject();
        file.Remove("SessionId");
        file.Remove("SessionIdentityRead");
        File.WriteAllText(cachePath, cache.ToJsonString());
        File.Move(path, ArchivePath(path));
        Verify(new UsageScanner(Paths).Scan(), "Codex", 2, 2);
        Verify(new UsageScanner(Paths).Scan(), "Codex", 2, 2);
        Assert.True(File.Exists(currentCache));
        Assert.Equal(cache.ToJsonString(), File.ReadAllText(cachePath));
    }

    [Fact]
    public void ClaudeCorrectionAndRestoredCopiesRemainConsistentAcrossAllViews()
    {
        var path = Write("Claude", [Line("Claude", 1)]);
        var scanner = new UsageScanner(Paths);
        scanner.Scan();
        File.Delete(path);
        var corrected = JsonNode.Parse(Line("Claude", 1))!;
        corrected["message"]!["usage"]!["input_tokens"] = 130;
        var restored = Write("Claude", [corrected.ToJsonString()], "restored.jsonl");
        File.SetLastWriteTimeUtc(restored, DateTime.UtcNow.AddSeconds(1));
        var result = scanner.Scan();
        Assert.Equal(1, result.Cli.Turns);
        Assert.Equal(140, result.Cli.TotalTokens);
        Assert.Equal(140, result.Statistics!.Days.Sum(x => x.TotalTokens));
        File.Delete(restored);
        Assert.Equal(140, new UsageScanner(Paths).Scan().Cli.TotalTokens);
    }

    [Fact]
    public void CodexUnchangedAndDeletedScansDoNotRereadLogsOrRewriteCache()
    {
        var path = Write("Codex", [Line("Codex", 1)]);
        var scanner = new UsageScanner(Paths);
        scanner.Scan();
        var cache = Path.Combine(Paths.LocalDataRoot, UsageScanner.CacheFileName);
        var sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cache, sentinel);
        Verify(scanner.Scan(), "Codex", 1, 1);
        Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(cache));
        File.Delete(path);
        Verify(scanner.Scan(), "Codex", 1, 1);
        Assert.Equal(0, scanner.LastDiagnostics.BytesRead);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(cache));
    }

    [Fact]
    public void ApiLogMigrationBetweenLegacyAndCurrentLocationsDoesNotDuplicateUsage()
    {
        Directory.CreateDirectory(Paths.LegacyDataRoot);
        var legacy = Path.Combine(Paths.LegacyDataRoot, "api-calls.jsonl");
        File.WriteAllLines(legacy, [Line("API", 1), Line("API", 2)]);
        var scanner = new UsageScanner(Paths);
        Verify(scanner.Scan(), "API", 2, 2);
        var current = Path.Combine(Paths.LocalDataRoot, "api-calls.jsonl");
        File.Copy(legacy, current);
        Verify(scanner.Scan(), "API", 2, 2);
        File.AppendAllText(current, Line("API", 3) + "\n");
        Verify(scanner.Scan(), "API", 3, 3);
        File.Delete(current);
        Verify(new UsageScanner(Paths).Scan(), "API", 3, 3);
    }

    private string Write(string source, IEnumerable<string> lines, string? filename = null, string session = Session)
    {
        var directory = source switch
        {
            "Claude" => Path.Combine(Paths.ClaudeRoot, "projects", "fixture"),
            "Codex" => Path.Combine(Paths.CodexRoot, "sessions"),
            _ => Paths.LocalDataRoot
        };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, filename ?? (source == "Codex" ? "rollout-" + Session + ".jsonl" :
            source == "API" ? "api-calls.jsonl" : "fixture.jsonl"));
        if (source == "Codex") lines = new[] { JsonSerializer.Serialize(new
        {
            type = "session_meta", payload = new { id = session, model = "fixture" }
        }) }.Concat(lines);
        File.WriteAllLines(path, lines);
        return path;
    }

    private string ArchivePath(string path)
    {
        var archive = Path.Combine(Paths.CodexRoot, "archived_sessions");
        Directory.CreateDirectory(archive);
        return Path.Combine(archive, Path.GetFileName(path));
    }

    private string Line(string source, int index, DateTimeOffset? at = null)
    {
        var dayStart = new DateTimeOffset(now.Date, now.Offset);
        var timestamp = (at ?? dayStart.AddTicks((now - dayStart).Ticks / 2 + index)).ToString("O");
        return source switch
        {
            "Claude" => JsonSerializer.Serialize(new { type = "assistant", timestamp, requestId = "request-" + index,
                message = new { model = "fixture", usage = new { input_tokens = 100, output_tokens = 10 } } }),
            "Codex" => JsonSerializer.Serialize(new { type = "event_msg", timestamp,
                payload = new { type = "token_count", model = "fixture", info = new {
                    total_token_usage = new { input_tokens = (index + 1) * 100, output_tokens = (index + 1) * 10 },
                    last_token_usage = new { input_tokens = 100, output_tokens = 10 } } } }),
            _ => JsonSerializer.Serialize(new { ts = timestamp, provider = "fixture", model = "fixture", ctx = 100,
                @out = 10, status = 200, ms = 5 })
        };
    }

    private void Verify(UsageScanResult result, string source, int today, int all)
    {
        if (source == "API")
        {
            Assert.Equal(today, result.Api.Calls);
            Assert.Equal(today * 110, result.Api.ContextTokens + result.Api.OutputTokens);
            Assert.Equal(all, Assert.Single(result.Api.Ranges!, x => x.Key == "all").Calls);
            Assert.Equal(all * 110, Assert.Single(result.Api.Ranges!, x => x.Key == "all").TotalTokens);
            Assert.Equal(Math.Min(8, all), result.Api.Recent!.Count);
            Assert.Equal(0, result.Cli.Turns);
            Assert.Empty(result.Statistics!.Days);
            return;
        }
        Assert.Equal(today, result.Cli.Turns);
        Assert.Equal(today * 110, result.Cli.TotalTokens);
        var row = Assert.Single(result.Cli.Sources, x => x.Name == (source == "Claude" ? "Claude Code" : source));
        Assert.Equal(UsageDataState.Available, row.State);
        Assert.Equal(result.Cli.TotalTokens, row.TotalTokens);
        Assert.Equal(Math.Min(3, today), result.Cli.Recent.Count);
        var day = DateOnly.FromDateTime(now.LocalDateTime);
        var selected = result.Statistics!.ForPeriod(day, day);
        Assert.Equal(today * 110, selected.TotalTokens);
        Assert.Equal(today * 110, selected.Models.Sum(x => x.TotalTokens));
        Assert.Equal(all * 110, result.Statistics.Days.Sum(x => x.TotalTokens));
        Assert.Equal(0, result.Api.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
