using Microsoft.Data.Sqlite;

namespace VibeGauge.Core;

public static class ZCodeUsage
{
    public const string SourceName = "ZCode";

    // Read only the normalized usage table, never prompts, credentials or response bodies.
    public static IReadOnlyList<InteractionRecord> Read(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, model_id, COALESCE(completed_at, started_at),
                   input_tokens, output_tokens, reasoning_tokens,
                   cache_read_input_tokens, cache_creation_input_tokens
            FROM model_usage
            WHERE status IN ('completed', 'error', 'cancelled')
              AND (input_tokens > 0 OR output_tokens > 0)
            """;
        using var reader = command.ExecuteReader();
        var result = new List<InteractionRecord>();
        while (reader.Read())
        {
            if (Enumerable.Range(0, 8).Any(reader.IsDBNull)) continue;
            var id = reader.GetString(0);
            var model = reader.GetString(1);
            var at = reader.GetInt64(2);
            var input = reader.GetInt64(3);
            var output = reader.GetInt64(4);
            var thinking = reader.GetInt64(5);
            var read = reader.GetInt64(6);
            var write = reader.GetInt64(7);
            if (id.Length == 0 || input < 0 || output < 0 || thinking < 0 || read < 0 || write < 0 ||
                input > long.MaxValue - output || at < -62135596800000 || at > 253402300799999) continue;
            // ZCode's model_usage input includes cache, and output includes reasoning.
            result.Add(new(id, SourceName, model.Length > 0 ? model : SourceName,
                DateTimeOffset.FromUnixTimeMilliseconds(at), input, read, write, output, thinking));
        }
        return result;
    }

    internal static string Signature(string path) => Stamp(path) + ":" + Stamp(path + "-wal");

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}" : "missing";
    }
}
