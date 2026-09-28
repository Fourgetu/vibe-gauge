using System.Text;

namespace VibeGauge.Core;

public static class JsonLineReader
{
    public const int MaxLineBytes = 32 * 1024 * 1024;

    // The cursor advances only past complete lines; partial writes are retried next scan.
    public static long Read(Stream stream, long offset, long length, Action<string, long> consume,
        int maxLineBytes = MaxLineBytes)
    {
        stream.Position = offset;
        var buffer = new byte[1024 * 1024];
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
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                    if (text.Length > 0) consume(text, completed);
                }
                line.SetLength(0);
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
}
