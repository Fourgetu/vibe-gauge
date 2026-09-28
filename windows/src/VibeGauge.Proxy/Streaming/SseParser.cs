using System.Text;
using VibeGauge.Proxy.Usage;

namespace VibeGauge.Proxy.Streaming;

public sealed class SseParser(UsageAccumulator usage, bool ndjson = false)
{
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder pending = new();
    private readonly StringBuilder message = new();
    private bool discardLine;
    private bool discardMessage;

    public void Push(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        decoder.Convert(bytes, chars, flush: false, out _, out var charsUsed, out _);
        foreach (var c in chars.AsSpan(0, charsUsed))
        {
            if (c == '\n')
            {
                if (discardLine) discardMessage = true;
                else ParseLine(pending.ToString().TrimEnd('\r'));
                pending.Clear(); discardLine = false;
            }
            else if (!discardLine)
            {
                if (pending.Length >= 1024 * 1024) { pending.Clear(); discardLine = true; usage.Error ??= "usage_line_too_large"; }
                else pending.Append(c);
            }
        }
    }

    public void Complete()
    {
        Span<char> tail = stackalloc char[8];
        decoder.Convert([], tail, flush: true, out _, out var used, out _);
        if (used > 0) pending.Append(tail[..used]);
        if (pending.Length > 0 && !discardLine) ParseLine(pending.ToString());
        pending.Clear();
        FinishMessage();
    }

    private void FinishMessage()
    {
        var payload = message.ToString().Trim();
        if (!discardMessage && payload.Length > 0 && payload != "[DONE]") UsageParser.Apply(Encoding.UTF8.GetBytes(payload), usage);
        message.Clear(); discardMessage = false;
    }

    private void ParseLine(string line)
    {
        if (ndjson)
        {
            if (!string.IsNullOrWhiteSpace(line)) UsageParser.Apply(Encoding.UTF8.GetBytes(line), usage);
            return;
        }
        if (line.Length == 0) { FinishMessage(); return; }
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var payload = line[5..].TrimStart(' ');
        if (message.Length + payload.Length > 1024 * 1024) { discardMessage = true; usage.Error ??= "usage_event_too_large"; return; }
        if (message.Length > 0) message.Append('\n');
        message.Append(payload);
    }
}
