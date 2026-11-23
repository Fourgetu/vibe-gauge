using Microsoft.Data.Sqlite;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class AntigravityUsageTests
{
    [Theory]
    [InlineData(13784, 0, 0, 36)]
    [InlineData(2183, 11735, 0, 608)]
    [InlineData(0, 80, 20, 0)]
    public void RecordedGenerationSurvivesWalRescanAndDeletion(long fresh, long cached, long write, long output)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        Directory.CreateDirectory(paths.AntigravityConversations);
        try
        {
            var file = Path.Combine(paths.AntigravityConversations, "conversation.db");
            var scanner = new UsageScanner(paths);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE steps(idx INTEGER PRIMARY KEY, metadata BLOB); CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY, data BLOB)";
                cmd.ExecuteNonQuery();
                static byte[] Num(long n)
                {
                    var result = new List<byte>();
                    while (n >= 128) { result.Add((byte)((n & 127) | 128)); n >>= 7; }
                    result.Add((byte)n);
                    return result.ToArray();
                }
                static byte[] Bytes(int n, byte[] v) => [.. Num(n * 8 + 2), .. Num(v.Length), .. v];
                byte[] usage = [16, .. Num(fresh), 24, .. Num(output), 32, .. Num(write), 40, .. Num(cached),
                    72, .. Num(Math.Min(24, output)), .. Bytes(11, "response-id"u8.ToArray())];
                byte[] chat = [.. Bytes(4, usage), .. Bytes(19, "gemini-4-carbon"u8.ToArray())];
                byte[] gen = [.. Bytes(1, chat), .. Bytes(2, [1])];
                var yesterday = DateTimeOffset.Now.AddDays(-1);
                cmd.CommandText = "INSERT INTO steps VALUES(1,$time); INSERT INTO gen_metadata VALUES(0,$data)";
                cmd.Parameters.AddWithValue("$time", Bytes(1, [8, .. Num(yesterday.ToUnixTimeSeconds())]));
                cmd.Parameters.AddWithValue("$data", gen);
                cmd.ExecuteNonQuery();
                var record = Assert.Single(AntigravityUsage.Read(file));
                Assert.Equal(yesterday.ToUnixTimeSeconds(), record.Timestamp.ToUnixTimeSeconds());
                Assert.Equal("gemini-4-carbon", record.Model);
                Assert.Equal(Math.Min(24, output), record.ThinkingTokens);
                Assert.Equal(fresh + cached + write, record.ContextTokens);
                Assert.Equal(cached * 100.0 / record.ContextTokens, record.CacheHitRate!.Value, 8);
                Assert.Equal(record.ContextTokens + output, record.TotalTokens);
                var result = scanner.Scan();
                Assert.Equal("conversation", Assert.Single(result.AntigravityRecords!).SessionId);
                Assert.Equal(record.TotalTokens, result.AntigravityTotal!.TotalTokens);
                Assert.Equal(0, result.Cli.Sources.Single(x => x.Name == "Antigravity").TotalTokens);
                cmd.CommandText = "INSERT INTO gen_metadata VALUES(1,$data)";
                cmd.ExecuteNonQuery();
                Assert.Equal(1, scanner.Scan().AntigravityTotal!.Turns);
            }
            File.Delete(file);
            var ledger = Path.Combine(paths.LocalDataRoot, UsageScanner.CacheFileName);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ledger))!;
            var state = json["Files"]![file]!.AsObject();
            state.Remove("AntigravityContextIncludesCache");
            Assert.Single(state["Records"]!.AsObject()).Value!["ContextTokens"] = fresh;
            File.WriteAllText(ledger, json.ToJsonString());
            // Migrate a deleted conversation, then start again to verify idempotence.
            for (int i = 0; i < 2; i++)
                Assert.Equal(fresh + cached + write + output, new UsageScanner(paths).Scan().AntigravityTotal!.TotalTokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RecentConversationsUseTitlesAndRecordedUsageNotContextPercent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "local"));
        Directory.CreateDirectory(paths.AntigravityConversations);
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource =
                Path.Combine(paths.GeminiRoot, "antigravity", "conversation_summaries.db"), Pooling = false }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE conversation_summaries(conversation_id,title,last_modified_time);" +
                "INSERT INTO conversation_summaries VALUES('active','Topic / title',$recent),('empty','New',$recent),('old','Old',$old)";
            var now = DateTimeOffset.UtcNow;
            cmd.Parameters.AddWithValue("$recent", now.AddMinutes(-15).ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz"));
            cmd.Parameters.AddWithValue("$old", now.AddHours(-3).ToString("O"));
            cmd.ExecuteNonQuery();
            var row = new InteractionRecord("one", "Antigravity", "model-a", now.AddDays(-1), 100, 0, 0, 10, 0, SessionId: "active");
            var summary = new SessionMonitor(paths).Scan(now, antigravityRecords:
                [row, row with { Id = "two", Model = "model-b", Timestamp = now.AddMinutes(-12) }]);
            Assert.Equal(2, summary.Active.Count);
            var active = summary.Active[0];
            Assert.Equal("Antigravity", active.Tool);
            Assert.Equal("Topic / title", active.Directory);
            Assert.Equal("model-b", active.Model);
            Assert.Equal(220, active.RecordedTokens);
            Assert.Null(active.UsedPercent);
            Assert.Null(active.Window);
            Assert.Equal(0, summary.Active[1].RecordedEntries);
        }
        finally { Directory.Delete(root, true); }
    }
}
