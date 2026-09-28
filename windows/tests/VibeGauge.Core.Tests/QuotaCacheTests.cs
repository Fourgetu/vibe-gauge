using System.Text;
using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class QuotaCacheTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "vibegauge-quota-" + Guid.NewGuid().ToString("N"));
    private string Sessions => Path.Combine(home, ".codex", "sessions");
    private string Log => Path.Combine(Sessions, "session.jsonl");
    private readonly DateTimeOffset now = DateTimeOffset.Now;

    public QuotaCacheTests() => Directory.CreateDirectory(Sessions);
    private QuotaScanner Scanner() => new(new AppPaths(home, Path.Combine(home, "local")));
    private static PlatformStatus Codex(QuotaScanner scanner) => scanner.Scan(ProcessReport.Empty).Single(x => x.Name == "Codex");
    private string Line(int percent, int seconds = 0, string limit = "codex") => JsonSerializer.Serialize(new
    {
        timestamp = now.AddSeconds(seconds).ToString("O"),
        rate_limits = new
        {
            limit_id = limit, plan_type = "pro",
            primary = new { used_percent = percent, window_minutes = 300, resets_at = now.AddHours(4).ToUnixTimeSeconds() },
            secondary = new { used_percent = 72, window_minutes = 10080, resets_at = now.AddDays(3).ToUnixTimeSeconds() }
        }
    });

    [Fact]
    public void UnchangedLargeTailReusesQuotaWithoutLargeAllocations()
    {
        File.WriteAllText(Log, new string('x', 1_500_000) + "\n" + Line(23));
        var scanner = Scanner();
        var first = Codex(scanner);
        Assert.Equal(23, first.FiveHour?.UsedPercent);
        Codex(scanner);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var again = Codex(scanner);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(first, again);
        Assert.InRange(allocated, 0, 200_000);
    }

    [Fact]
    public void ChangedFilesRefreshForAppendRewriteTruncateAndDeletion()
    {
        File.WriteAllText(Log, Line(10));
        var scanner = Scanner();
        Assert.Equal(10, Codex(scanner).FiveHour?.UsedPercent);
        File.AppendAllText(Log, "\n" + Line(20, 1));
        Assert.Equal(20, Codex(scanner).FiveHour?.UsedPercent);
        File.WriteAllText(Log, Line(30, 2));
        Assert.Equal(30, Codex(scanner).FiveHour?.UsedPercent);
        File.WriteAllText(Log, Line(40, 2));
        File.SetLastWriteTimeUtc(Log, DateTime.UtcNow.AddSeconds(3));
        Assert.Equal(40, Codex(scanner).FiveHour?.UsedPercent);
        File.WriteAllText(Log, "{}");
        Assert.Null(Codex(scanner).FiveHour);
        File.WriteAllText(Log, Line(50, 3));
        Assert.Equal(50, Codex(scanner).FiveHour?.UsedPercent);
        File.Delete(Log);
        Assert.Null(Codex(scanner).FiveHour);
    }

    [Fact]
    public void ChoosesNewestAcrossCachedFilesAndExpiresOldFiles()
    {
        File.WriteAllText(Log, Line(10));
        var other = Path.Combine(Sessions, "other.jsonl");
        File.WriteAllText(other, Line(20, 2));
        var scanner = Scanner();
        Assert.Equal(20, Codex(scanner).FiveHour?.UsedPercent);
        File.AppendAllText(Log, "\n" + Line(30, 3));
        Assert.Equal(30, Codex(scanner).FiveHour?.UsedPercent);
        File.SetLastWriteTimeUtc(Log, DateTime.UtcNow.AddDays(-3));
        Assert.Equal(20, Codex(scanner).FiveHour?.UsedPercent);
        Directory.Delete(Sessions, true);
        Assert.Null(Codex(scanner).FiveHour);
    }

    [Fact]
    public void IgnoresMalformedOtherLimitsAndIncompleteJsonUntilCompleted()
    {
        var next = Line(80, 2);
        File.WriteAllText(Log, Line(12) + "\n{\"used_percent\":\n" + Line(90, 1, "other") + "\n" + next[..^1]);
        var scanner = Scanner();
        Assert.Equal(12, Codex(scanner).FiveHour?.UsedPercent);
        File.AppendAllText(Log, "}");
        Assert.Equal(80, Codex(scanner).FiveHour?.UsedPercent);
    }

    [Fact]
    public void BoundedTailSkipsPartialFirstLineAndKeepsFinalLineWithoutNewline()
    {
        File.WriteAllText(Log, Line(99, 9) + "\n" + new string('x', 2_200_000) + "\r\n" + Line(17));
        Assert.Equal(17, Codex(Scanner()).FiveHour?.UsedPercent);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void PreservesLineEndings(string newline)
    {
        File.WriteAllText(Log, Line(61) + newline + Line(62, 1) + newline);
        Assert.Equal(62, Codex(Scanner()).FiveHour?.UsedPercent);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf32")]
    public void PreservesBomEncodedLogs(string format)
    {
        var encoding = format switch { "utf16" => Encoding.Unicode, "utf32" => Encoding.UTF32, _ => new UTF8Encoding(true) };
        File.WriteAllText(Log, Line(61) + "\r\n", encoding);
        Assert.Equal(61, Codex(Scanner()).FiveHour?.UsedPercent);
    }

    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }
}
