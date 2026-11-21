using Microsoft.Data.Sqlite;

namespace VibeGauge.Core;

public static class DevinUsage
{
    public const string SourceName = "Devin";

    public static IReadOnlyList<InteractionRecord> Read(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
        }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        // Select scalar metadata only. Copied tree nodes can represent the same billed reply.
        query.CommandText = """
            WITH replies AS (
              SELECT n.session_id AS sid, n.created_at AS node_time, n.row_id,
                     json_extract(n.chat_message, '$.message_id') AS mid,
                     json_extract(n.chat_message, '$.metadata.created_at') AS reply_time,
                     coalesce(nullif(json_extract(n.chat_message, '$.metadata.generation_model'), ''), s.model) AS model,
                     json_extract(n.chat_message, '$.metadata.metrics') AS metrics
              FROM message_nodes n LEFT JOIN sessions s ON s.id = n.session_id
              WHERE CASE WHEN json_valid(n.chat_message) THEN json_extract(n.chat_message, '$.role') END = 'assistant'
            )
            SELECT sid, mid, model, reply_time, node_time,
                   metrics ->> '$.context_input_tokens', metrics ->> '$.input_tokens',
                   metrics ->> '$.cache_read_tokens', metrics ->> '$.cache_creation_tokens', metrics ->> '$.output_tokens'
            FROM replies WHERE typeof(mid) = 'text' AND mid <> '' AND typeof(sid) = 'text' AND sid <> ''
              AND json_type(metrics, '$.output_tokens') = 'integer'
            ORDER BY row_id
            """;
        using var reader = query.ExecuteReader();
        var records = new Dictionary<string, InteractionRecord>(StringComparer.Ordinal);
        while (reader.Read())
        {
            long? Number(int i) => reader.GetValue(i) is long n && n >= 0 ? n : null;
            var read = reader.IsDBNull(7) ? 0 : Number(7);
            var write = reader.IsDBNull(8) ? 0 : Number(8);
            if (read is null || write is null || Number(9) is not { } output) continue;
            var context = Number(5);
            try
            {
                if (reader.IsDBNull(5) && Number(6) is { } input) context = checked(input + read.Value + write.Value);
                if (context is null || checked(context.Value + output) == 0) continue;
            }
            catch (OverflowException) { continue; }
            var timestamp = reader.GetValue(3) is string iso ? Formatting.ParseDate(iso) : null;
            if (timestamp is null && Number(4) is { } sec && sec <= 253402300799L)
                timestamp = DateTimeOffset.FromUnixTimeSeconds(sec);
            if (timestamp is null) continue;
            var session = reader.GetString(0);
            var id = $"{session.Length}:{session}:{reader.GetString(1)}";
            var model = reader.GetValue(2) is string { Length: > 0 } m ? m : SourceName;
            records[id] = new(id, SourceName, model, timestamp.Value, context.Value, read.Value, write.Value, output, 0);
        }
        return records.Values.ToArray();
    }
}
