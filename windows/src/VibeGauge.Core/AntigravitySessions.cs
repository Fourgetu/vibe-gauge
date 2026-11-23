using Microsoft.Data.Sqlite;

namespace VibeGauge.Core;

internal sealed class AntigravitySessions
{
    private IReadOnlyList<InteractionRecord>? previous;
    private IReadOnlyList<SessionContext> cached = [];
    private string signature = "";
    private DateTimeOffset scanned;

    internal IReadOnlyList<SessionContext> Read(AppPaths paths, DateTimeOffset now, IReadOnlyList<InteractionRecord> usage)
    {
        var index = Path.Combine(paths.GeminiRoot, "antigravity", "conversation_summaries.db");
        var cutoff = now.AddHours(-2);
        var stamp = ZCodeUsage.Signature(index);
        if (ReferenceEquals(previous, usage) && stamp == signature && now >= scanned && now < scanned.AddMinutes(1))
            return cached.Where(s => s.UpdatedAt > cutoff).ToArray();
        var sessions = new Dictionary<string, (string Title, DateTimeOffset At)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(index))
            try
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index,
                    Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
                db.Open();
                using var cmd = db.CreateCommand();
                // Select display metadata, never previews, transcripts or the search index.
                cmd.CommandText = """
                    SELECT conversation_id, substr(title,1,240), last_modified_time FROM conversation_summaries
                    WHERE julianday(last_modified_time) BETWEEN julianday($from) AND julianday($to)
                    """;
                cmd.Parameters.AddWithValue("$from", cutoff.UtcDateTime.ToString("O"));
                cmd.Parameters.AddWithValue("$to", now.UtcDateTime.ToString("O"));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetValue(0) is not string id || id.Length == 0 || Formatting.ParseDate(r.GetString(2)) is not { } at ||
                        at <= cutoff || at > now) continue;
                    sessions[id] = (r.GetValue(1) as string ?? "", at);
                }
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { }

        foreach (var r in usage)
        {
            if (r.Timestamp <= cutoff || r.Timestamp > now || r.SessionId.Length == 0) continue;
            if (sessions.TryGetValue(r.SessionId, out var entry))
            {
                if (r.Timestamp > entry.At) sessions[r.SessionId] = (entry.Title, r.Timestamp);
            }
            else if (Path.GetFileName(r.SessionId) == r.SessionId && File.Exists(Path.Combine(paths.AntigravityConversations, r.SessionId + ".db")))
                sessions[r.SessionId] = ("", r.Timestamp);
        }
        previous = usage;
        signature = stamp;
        scanned = now;
        if (sessions.Count == 0) return cached = [];
        var groups = usage.Where(r => r.Timestamp <= now && sessions.ContainsKey(r.SessionId)).ToLookup(r => r.SessionId, StringComparer.OrdinalIgnoreCase);
        return cached = sessions.Select(pair =>
        {
            var records = groups[pair.Key];
            var title = string.IsNullOrWhiteSpace(pair.Value.Title) ? "桌面会话 · " + pair.Key[..Math.Min(8, pair.Key.Length)] : pair.Value.Title;
            return new SessionContext(pair.Key, AntigravityUsage.SourceName, title, records.MaxBy(r => r.Timestamp)?.Model ?? "",
                null, null, pair.Value.At, 0, RecordedTokens: records.Sum(r => r.TotalTokens), RecordedEntries: records.Count());
        }).ToArray();
    }
}
