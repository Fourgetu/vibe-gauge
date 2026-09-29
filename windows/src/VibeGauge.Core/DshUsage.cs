using System.Buffers;
using System.Text.Json;
using ZstdSharp;

namespace VibeGauge.Core;

public static class DshUsage
{
    public const string SourceName = "DSH Desktop";

    public static IReadOnlyList<InteractionRecord> Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase)) return Read(file);
        // DSH appends independent Zstandard frames. Stream through every frame,
        // retaining only usage metadata, never a decompressed conversation copy.
        using var stream = new DecompressionStream(file);
        return Read(stream);
    }

    public static IReadOnlyList<InteractionRecord> Read(Stream stream)
    {
        var records = new Dictionary<string, InteractionRecord>(StringComparer.Ordinal);
        string session = "", model = SourceName, lastKey = "";
        long lastTurn = -1, lastStep = -1;
        var inherited = false;
        ReadLines(stream, line =>
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.StringOrEmpty("type");
            if (type == "session")
            {
                if (!ReadNumber(root, "version", out var version) || version != 4)
                    throw new InvalidDataException("Unsupported DSH session format");
                session = root.StringOrEmpty("id");
                inherited = root.TryGetProperty("isSeeded", out var seeded) && seeded.ValueKind == JsonValueKind.True;
                return;
            }
            if (session.Length == 0) throw new InvalidDataException("Missing DSH session header");
            if (type == "session/end-seed") { inherited = false; lastKey = ""; return; }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
            if (type == "model/selection")
            {
                var selected = data.StringOrEmpty("model");
                if (selected.Length > 0) model = selected;
            }
            if (type == "request/header" && Object(data, "header", out var header) && Object(header, "config", out var config))
            {
                var selected = config.StringOrEmpty("model");
                if (selected.Length > 0) model = selected;
            }
            if (inherited || !ReadNumber(data, "turn", out var turn) || !ReadNumber(data, "step", out var step)) return;
            if (type == "llm/retry-started")
            {
                if (lastTurn == turn && lastStep == step) lastKey = "";
                return;
            }
            if (type is not ("assistant/message" or "assistant/attempt") ||
                !ReadNumber(root, "seq", out var sequence) || !ReadNumber(root, "time", out var time) ||
                time > 253402300799999 || !Usage(data, out var usage) ||
                !ReadNumber(usage, "inputTokens", out var input) || !ReadNumber(usage, "outputTokens", out var output)) return;
            if (!OptionalNumber(usage, "cacheReadTokens", out var read) ||
                !OptionalNumber(usage, "cacheWriteTokens", out var write) ||
                !OptionalNumber(usage, "reasoningTokens", out var thinking)) return;
            try
            {
                // DSH's token-meter explicitly treats inputTokens as uncached.
                var context = checked(input + read + write);
                if (checked(context + output) == 0) return;
                // Match token-meter replacement slots; retries open a new slot.
                if (lastKey.Length == 0 || lastTurn != turn || lastStep != step)
                    lastKey = session + ":" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                lastTurn = turn;
                lastStep = step;
                records[lastKey] = new(lastKey, SourceName, model, DateTimeOffset.FromUnixTimeMilliseconds(time),
                    context, read, write, output, thinking);
            }
            catch (OverflowException) { }
        });
        return records.Values.ToArray();
    }

    private static bool Usage(JsonElement data, out JsonElement usage)
    {
        if (Object(data, "usage", out usage)) return true;
        if (data.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Array)
            foreach (var entry in stream.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && entry.StringOrEmpty("type") == "chunk" &&
                    Object(entry, "chunk", out var chunk) && chunk.StringOrEmpty("type") == "usage" &&
                    Object(chunk, "usage", out var sample)) usage = sample;
        return usage.ValueKind == JsonValueKind.Object;
    }

    private static bool Object(JsonElement root, string key, out JsonElement value) =>
        root.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;
    private static bool OptionalNumber(JsonElement root, string key, out long value)
    {
        value = 0;
        return !root.TryGetProperty(key, out _) || ReadNumber(root, key, out value);
    }
    private static bool ReadNumber(JsonElement root, string key, out long value)
    {
        value = 0;
        return root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out value) && value >= 0;
    }

    private static bool IsRelevant(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var isType = reader.ValueTextEquals("type"u8);
            if (!reader.Read()) throw new JsonException();
            if (isType)
                return reader.TokenType == JsonTokenType.String && reader.GetString() is
                    "session" or "session/end-seed" or "model/selection" or "request/header" or
                    "llm/retry-started" or "assistant/message" or "assistant/attempt";
            reader.Skip();
        }
        return false;
    }

    private static void ReadLines(Stream stream, Action<ReadOnlyMemory<byte>> consume)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var line = new MemoryStream();
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                var start = 0;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != '\n') continue;
                    if (line.Length + i - start > JsonLineReader.MaxLineBytes) throw new InvalidDataException("Oversized DSH event");
                    line.Write(buffer, start, i - start);
                    var bytes = line.GetBuffer().AsMemory(0, (int)line.Length);
                    if (line.Length > 0 && IsRelevant(bytes.Span)) consume(bytes);
                    line.SetLength(0);
                    if (line.Capacity > 1024 * 1024) line.Capacity = 0;
                    start = i + 1;
                }
                if (line.Length + count - start > JsonLineReader.MaxLineBytes) throw new InvalidDataException("Oversized DSH event");
                line.Write(buffer, start, count - start);
            }
            // An unfinished JSONL tail may contain usage: retry it on the next scan.
            if (line.Length > 0) throw new EndOfStreamException("Incomplete DSH event");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
