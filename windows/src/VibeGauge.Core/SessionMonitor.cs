using System.Security.Cryptography;
using System.Text.Json;

namespace VibeGauge.Core;

public sealed record SessionContext(string Id, string Tool, string Directory, string Model,
    double? UsedPercent, long? Window, DateTimeOffset UpdatedAt, int Compactions);
public sealed record PendingSession(string Id, string Kind, string Tool, string Directory, DateTimeOffset Since);
public sealed record SessionSummary(IReadOnlyList<SessionContext> Active, IReadOnlyList<PendingSession> Pending);

public sealed class SessionMonitor(AppPaths paths)
{
    private readonly Dictionary<string, Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);

    public SessionSummary Scan(DateTimeOffset now)
    {
        var active = new List<SessionContext>();
        using var claude = JsonSupport.ReadDocument(paths.ResolveOwnDataFile("claude-sessions.json"));
        if (claude?.RootElement.TryGetProperty("sessions", out var rows) == true && rows.ValueKind == JsonValueKind.Object)
            foreach (var entry in rows.EnumerateObject())
            {
                var row = entry.Value;
                if (row.ValueKind != JsonValueKind.Object || Epoch(row.DoubleOrNull("at")) is not { } at ||
                    at > now || now - at >= TimeSpan.FromHours(2)) continue;
                var transcript = row.StringOrEmpty("transcript");
                var compactions = 0;
                if (File.Exists(transcript))
                    try { compactions = Refresh(transcript).Compactions.Count(x => x.LocalDateTime.Date == now.LocalDateTime.Date); } catch (IOException) { }
                active.Add(new(entry.Name, "Claude", row.StringOrEmpty("cwd"), row.StringOrEmpty("model"),
                    row.DoubleOrNull("used_pct"), row.Long("window") is > 0 and var w ? w : null, at, compactions));
            }

        var root = Path.Combine(paths.CodexRoot, "sessions");
        if (Directory.Exists(root))
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    var changed = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
                    if (now - changed >= TimeSpan.FromHours(2)) continue;
                    var state = Refresh(file);
                    if (state.Used is null && state.Compactions.Count == 0) continue;
                    active.Add(new(file, "Codex", state.Directory, state.Model,
                        state.Used is { } used && state.Window > 0 ? used * 100d / state.Window : null,
                        state.Window, state.At ?? changed,
                        state.Compactions.Count(x => x.LocalDateTime.Date == now.LocalDateTime.Date)));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        foreach (var key in cursors.Keys.Where(x => !File.Exists(x) || now.UtcDateTime - File.GetLastWriteTimeUtc(x) > TimeSpan.FromHours(2)).ToArray())
            cursors.Remove(key);
        using var pending = JsonSupport.ReadDocument(paths.ResolveOwnDataFile("claude-waiting.json"));
        var waiting = CliBridge.HooksInstalled(paths) && pending is not null
            ? ParsePending(pending.RootElement, now) : [];
        return new(active.OrderByDescending(x => x.UpdatedAt).ToArray(), waiting);
    }

    private Cursor Refresh(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var modified = File.GetLastWriteTimeUtc(path).Ticks;
        if (!cursors.TryGetValue(path, out var state)) state = new();
        var changed = stream.Length < state.Size || stream.Length == state.Size && state.Modified != modified;
        if (!changed && state.Offset > 0)
        {
            var tail = new byte[(int)Math.Min(64, state.Offset)];
            stream.Position = state.Offset - tail.Length;
            stream.ReadExactly(tail);
            changed = !SHA256.HashData(tail).SequenceEqual(state.Tail);
        }
        if (changed) state = new();
        state.Offset = JsonLineReader.Read(stream, state.Offset, stream.Length, (line, _) => Consume(state, line));
        var end = new byte[(int)Math.Min(64, state.Offset)];
        stream.Position = state.Offset - end.Length;
        stream.ReadExactly(end);
        state.Tail = SHA256.HashData(end);
        state.Size = stream.Length;
        state.Modified = modified;
        cursors[path] = state;
        return state;
    }

    private static void Consume(Cursor state, string line)
    {
        if (!line.Contains("token_count") && !line.Contains("compacted") && !line.Contains("turn_context") &&
            !line.Contains("session_meta") && !line.Contains("compact_boundary")) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var at = Formatting.ParseDate(root.StringOrEmpty("timestamp"));
            var type = root.StringOrEmpty("type");
            if (type == "compacted" || root.StringOrEmpty("subtype") == "compact_boundary")
            {
                if (at is { } compacted) state.Compactions.Add(compacted);
                state.Used = null;
                return;
            }
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            if (type is "turn_context" or "session_meta")
            {
                if (payload.StringOrEmpty("model") is { Length: > 0 } model) state.Model = model;
                if (payload.StringOrEmpty("cwd") is { Length: > 0 } cwd) state.Directory = cwd;
            }
            else if (type == "event_msg" && payload.StringOrEmpty("type") == "token_count" &&
                payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                var window = info.Long("model_context_window");
                long? used = info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object &&
                    last.DoubleOrNull("total_tokens") is >= 0 ? last.Long("total_tokens") : null;
                if (window > 0 && state.Window != window) { state.Window = window; state.Used = null; }
                if (used is not null) { state.Used = used; state.At = at; }
            }
        }
        catch (JsonException) { }
    }

    public static IReadOnlyList<PendingSession> ParsePending(JsonElement root, DateTimeOffset now)
    {
        var result = new List<PendingSession>();
        if (!root.TryGetProperty("sessions", out var rows) || rows.ValueKind != JsonValueKind.Object) return result;
        foreach (var row in rows.EnumerateObject())
        {
            var item = row.Value;
            if (item.ValueKind != JsonValueKind.Object || Epoch(item.DoubleOrNull("at")) is not { } at ||
                at > now || now - at >= TimeSpan.FromHours(12)) continue;
            var path = item.StringOrEmpty("transcript_path");
            if (File.Exists(path) && new DateTimeOffset(File.GetLastWriteTimeUtc(path)) > at.AddSeconds(30)) continue;
            var calls = item.TryGetProperty("calls", out var callRows) && callRows.ValueKind == JsonValueKind.Object
                ? callRows.EnumerateObject().Select(x => x.Value)
                    .Where(x => x.ValueKind == JsonValueKind.Object && x.StringOrEmpty("state") == "permission" &&
                        Epoch(x.DoubleOrNull("since")) is { } since && since <= now)
                    .OrderBy(x => x.DoubleOrNull("since")).ToArray() : [];
            if (calls.Length > 0)
                result.Add(new(row.Name, "等待批准", calls[0].StringOrEmpty("tool"), item.StringOrEmpty("cwd"),
                    Epoch(calls[0].DoubleOrNull("since"))!.Value));
            else if (Epoch(item.DoubleOrNull("input")) is { } input && input <= now)
                result.Add(new(row.Name, "等待输入", "", item.StringOrEmpty("cwd"), input));
        }
        return result.OrderBy(x => x.Since).ToArray();
    }

    private static DateTimeOffset? Epoch(double? value) => value is >= 0 and < 253402300800
        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(value.Value * 1000)) : null;
    private sealed class Cursor
    {
        public long Offset, Size, Modified;
        public byte[] Tail = [];
        public string Model = "", Directory = "";
        public long? Used, Window;
        public DateTimeOffset? At;
        public List<DateTimeOffset> Compactions = [];
    }
}
