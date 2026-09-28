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
}
