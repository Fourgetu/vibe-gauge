using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VibeGauge.Core;

internal sealed class DevinWeeklyQuota
{
    private sealed record Snapshot(string Scope, int Used, DateTimeOffset Reset);
    private string? signature;
    private Snapshot? cached;

    public PlatformStatus Apply(AppPaths paths, PlatformStatus tokens, DateTimeOffset now)
    {
        var path = paths.DevinStateDatabase;
        if (!File.Exists(path))
        {
            signature = null;
            cached = null;
            return tokens;
        }
        try
        {
            var current = ZCodeUsage.Signature(path);
            if (current != signature)
            {
                cached = Read(path);
                signature = ZCodeUsage.Signature(path) == current ? current : null;
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or FormatException or UnauthorizedAccessException)
        {
            signature = null;
            cached = null;
        }

        var quota = cached;
        var available = quota is not null && quota.Reset > now;
        return tokens with
        {
            DataState = available && tokens.DataState == ProviderDataState.NoQuota ? ProviderDataState.Available : tokens.DataState,
            Weekly = available ? new(quota!.Used, quota.Reset, null, TimeSpan.FromDays(7)) : null,
            QuotaScope = quota?.Scope ?? "",
            Metadata = new([new("周额度", available ? "来自 Devin 当前账号的客户端缓存" : "暂无有效的账号额度回报")],
                ProviderDetails.Sources(paths, tokens))
        };
    }

    private static Snapshot? Read(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
        }.ToString());
        db.Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        // Never select the API key or pick a plan by recency: old account rows survive sign-out.
        command.CommandText = "SELECT json_extract(value,'$.userStatusProtoBinaryBase64') FROM ItemTable WHERE key='windsurfAuthStatus'";
        if (command.ExecuteScalar() is not string { Length: > 0 and <= 8388608 } status ||
            UserId(Convert.FromBase64String(status)) is not { } user) return null;
        command.CommandText = """
            SELECT value ->> '$.quota.weeklyRemainingPercent', value ->> '$.quota.weeklyResetAtUnix'
            FROM ItemTable WHERE key=$key AND value ->> '$.isDevinUser'=1 AND value ->> '$.billingStrategy'='quota'
              AND json_type(value,'$.quota.weeklyRemainingPercent')='integer'
              AND json_type(value,'$.quota.weeklyResetAtUnix')='integer'
              AND coalesce(value ->> '$.quota.hideWeeklyQuota',0)=0
            """;
        command.Parameters.AddWithValue("$key", "windsurf.reactSettings.cachedPlanInfoData:" + user);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) is not (>= 0 and <= 100) ||
            reader.GetInt64(1) is not (> 0 and <= 253402300799L)) return null;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)));
        return new(scope, 100 - reader.GetInt32(0), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)));
    }

    // Devin's protobuf UserStatus.user_id is length-delimited field 36. Skip all other fields.
    private static string? UserId(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        while (stream.Position < stream.Length)
        {
            int tag = reader.Read7BitEncodedInt();
            if (tag <= 0) return null;
            var wire = tag & 7;
            if (wire == 0) { reader.Read7BitEncodedInt64(); continue; }
            long size = wire switch { 1 => 8, 2 => reader.Read7BitEncodedInt(), 5 => 4, _ => -1 };
            if (size < 0 || size > stream.Length - stream.Position) return null;
            if (tag == ((36 << 3) | 2))
                return size is > 0 and <= 512 ? Encoding.UTF8.GetString(bytes, (int)stream.Position, (int)size) : null;
            stream.Position += size;
        }
        return null;
    }
}
