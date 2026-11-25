using System.Text;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ReaderAllocationTests
{
    [Fact]
    public void UnchangedStreamDoesNotAllocateLargeBuffers()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        Action<string, long> consume = (_, _) => throw new InvalidOperationException();
        JsonLineReader.Read(stream, 3, 3, consume);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) Assert.Equal(3, JsonLineReader.Read(stream, 3, 3, consume));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 50_000);
    }

    [Fact]
    public void ByteFilterPreservesUtf8CrLfOffsetsAndIncompleteTail()
    {
        var ignored = new string('x', 180_000) + "\r\n";
        var accepted = "{\"usage\":\"中文\"}\r\n";
        var complete = Encoding.UTF8.GetBytes(ignored + accepted);
        using var stream = new MemoryStream(complete.Concat("partial"u8.ToArray()).ToArray());
        var lines = new List<(string Text, long Offset)>();
        var end = JsonLineReader.Read(stream, 0, stream.Length, (text, offset) => lines.Add((text, offset)),
            JsonLineReader.MaxLineBytes, bytes => bytes.Span.IndexOf("usage"u8) >= 0);
        Assert.Equal(complete.Length, end);
        Assert.Equal((accepted.TrimEnd('\r', '\n'), (long)Encoding.UTF8.GetByteCount(ignored)), Assert.Single(lines));
    }

    [Fact]
    public void UnchangedUsageScanReusesSummaryAndInvalidatesOnChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeGauge-memo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root, Path.Combine(root, "local"));
            var claudeDir = Path.Combine(paths.ClaudeRoot, "projects", "demo");
            Directory.CreateDirectory(claudeDir);
            var logPath = Path.Combine(claudeDir, "s1.jsonl");
            var ts = DateTimeOffset.Now.ToString("O");
            File.WriteAllText(logPath, $"{{\"type\":\"assistant\",\"requestId\":\"r1\",\"timestamp\":\"{ts}\",\"message\":{{\"model\":\"claude-sonnet-4\",\"usage\":{{\"input_tokens\":100,\"output_tokens\":20}}}}}}\n");
            var scanner = new UsageScanner(paths);
            var first = scanner.Scan();
            var second = scanner.Scan();
            Assert.Same(first, second);
            File.AppendAllText(logPath, $"{{\"type\":\"assistant\",\"requestId\":\"r2\",\"timestamp\":\"{ts}\",\"message\":{{\"model\":\"claude-sonnet-4\",\"usage\":{{\"input_tokens\":50,\"output_tokens\":10}}}}}}\n");
            var third = scanner.Scan();
            Assert.NotSame(second, third);
            Assert.Equal(2, third.Cli.Turns);
            var pricesPath = Path.Combine(paths.LocalDataRoot, "prices.json");
            File.WriteAllText(pricesPath, "{\"claude-sonnet-4\":{\"input\":3,\"output\":15}}");
            var fourth = scanner.Scan();
            Assert.NotSame(third, fourth);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
