using System.Text;
using System.Buffers;

namespace VibeGauge.Core;

public static class JsonLineReader
{
    public const int MaxLineBytes = 32 * 1024 * 1024;

    // The cursor advances only past complete lines; partial writes are retried next scan.
    public static long Read(Stream stream, long offset, long length, Action<string, long> consume,
        int maxLineBytes = MaxLineBytes) => Read(stream, offset, length, consume, maxLineBytes, null);

    public static long Read(Stream stream, long offset, long length, Action<string, long> consume,
        int maxLineBytes, Func<ReadOnlyMemory<byte>, bool>? shouldConsume)
    {
        stream.Position = offset;
        if (offset >= length) return offset;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var line = new MemoryStream();
            var cursor = offset;
            var completed = offset;
            var skipping = false;
            while (cursor < length)
            {
                var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - cursor));
                if (count == 0) break;
                var start = 0;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != '\n') continue;
                    if (!skipping && line.Length + i - start <= maxLineBytes)
                    {
                        line.Write(buffer, start, i - start);
                        var bytes = line.GetBuffer().AsMemory(0, (int)line.Length);
                        if (shouldConsume is null || shouldConsume(bytes))
                        {
                            var text = Encoding.UTF8.GetString(bytes.Span).TrimEnd('\r');
                            if (text.Length > 0) consume(text, completed);
                        }
                    }
                    line.SetLength(0);
                    if (line.Capacity > 1024 * 1024) line.Capacity = 0;
                    skipping = false;
                    start = i + 1;
                    completed = cursor + start;
                }
                if (!skipping)
                {
                    if (line.Length + count - start > maxLineBytes) { line.SetLength(0); skipping = true; }
                    else line.Write(buffer, start, count - start);
                }
                cursor += count;
            }
            return completed;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
