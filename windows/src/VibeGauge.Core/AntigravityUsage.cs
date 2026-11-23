using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VibeGauge.Core;

public static class AntigravityUsage
{
    public const string SourceName = "Antigravity";
    // ModelUsageStats reports fresh input, cache reads and cache writes separately.
    internal static long ContextTokens(long input, long cached, long write) => checked(input + cached + write);

    // Scan protobuf field boundaries in the SQLite blob. Large prompts and tool payloads
    // are skipped by seeking, not decoded or retained.
    private readonly record struct Field(int Tag, long Start, long End, long Value, int Wire);
    private static ulong Varint(Stream stream, long end)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64 && stream.Position < end; shift += 7)
        {
            int b = stream.ReadByte();
            if (b < 0 || shift == 63 && b > 1) break;
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
        }
        throw new InvalidDataException("Invalid protobuf integer");
    }

    private static IEnumerable<Field> Fields(Stream stream, long start, long end)
    {
        while (start < end)
        {
            stream.Position = start;
            var key = checked((int)Varint(stream, end));
            int wire = key & 7;
            long value = wire == 0 ? unchecked((long)Varint(stream, end)) : 0;
            long size = wire switch { 0 => 0, 1 => 8, 2 => checked((long)Varint(stream, end)), 5 => 4,
                _ => throw new InvalidDataException("Invalid protobuf wire type") };
            start = stream.Position;
            if (key >> 3 == 0 || size > end - start) throw new InvalidDataException("Invalid protobuf field");
            yield return new(key >> 3, start, start + size, value, wire);
            start += size;
        }
    }

    private static string SmallString(Stream stream, Field f)
    {
        if (f.End - f.Start is <= 0 or > 512) return "";
        stream.Position = f.Start;
        var bytes = new byte[(int)(f.End - f.Start)];
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    public static IReadOnlyList<InteractionRecord> Read(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        db.Open();
        using var times = db.CreateCommand();
        times.CommandText = "SELECT metadata FROM steps WHERE idx=$idx AND length(metadata)<=1048576";
        var index = times.Parameters.Add("$idx", SqliteType.Integer);
        using var query = db.CreateCommand();
        query.CommandText = "SELECT idx,data FROM gen_metadata ORDER BY idx";
        using var reader = query.ExecuteReader();
        var records = new List<InteractionRecord>();
        while (reader.Read())
        {
            using var blob = reader.GetStream(1);
            long step = -1, input = 0, output = 0, cached = 0, write = 0, thinking = 0;
            string model = "", message = "", response = "";
            foreach (var field in Fields(blob, 0, blob.Length))
            {
                if (field.Tag == 2 && step < 0)
                {
                    if (field.Wire == 0) step = field.Value;
                    if (field.Wire == 2 && field.Start < field.End)
                    { blob.Position = field.Start; step = checked((long)Varint(blob, field.End)); }
                }
                if (field.Tag != 1 || field.Wire != 2) continue;
                foreach (var chat in Fields(blob, field.Start, field.End).Where(f => f.Wire == 2))
                {
                    if (chat.Tag == 19 || chat.Tag == 21 && model.Length == 0) model = SmallString(blob, chat);
                    if (chat.Tag != 4) continue;
                    foreach (var u in Fields(blob, chat.Start, chat.End))
                        if (u.Wire == 0 && u.Value >= 0)
                        {
                            switch (u.Tag) { case 2: input = u.Value; break; case 3: output = u.Value; break;
                                case 4: write = u.Value; break; case 5: cached = u.Value; break; case 9: thinking = u.Value; break; }
                        }
                        else if (u.Wire == 2 && u.Tag == 7) message = SmallString(blob, u);
                        else if (u.Wire == 2 && u.Tag == 11) response = SmallString(blob, u);
                }
            }
            var context = ContextTokens(input, cached, write);
            if (step < 0 || checked(context + output) == 0) continue;
            index.Value = step;
            if (times.ExecuteScalar() is not byte[] bytes) continue;
            using var meta = new MemoryStream(bytes);
            var timestamp = Fields(meta, 0, meta.Length).FirstOrDefault(f => f.Tag == 1 && f.Wire == 2);
            var sec = Fields(meta, timestamp.Start, timestamp.End).FirstOrDefault(f => f.Tag == 1 && f.Wire == 0).Value;
            if (sec is <= 0 or > 253402300799L) continue;
            var id = response.Length > 0 ? "r:" + response : message.Length > 0 ? "m:" + message :
                $"g:{Path.GetFileNameWithoutExtension(path)}:{reader.GetInt64(0)}";
            records.Add(new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))), SourceName,
                model.Length > 0 ? model : SourceName, DateTimeOffset.FromUnixTimeSeconds(sec), context, cached, write, output, thinking));
        }
        return records;
    }
}
