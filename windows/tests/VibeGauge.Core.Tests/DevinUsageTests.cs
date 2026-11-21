using Microsoft.Data.Sqlite;
using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class DevinUsageTests
{
    [Fact]
    public void DuplicateNodesWalWritesAndDeletedSessionsKeepCorrectDatedTotals()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-devin-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, Path.Combine(root, "ledger"));
        Directory.CreateDirectory(paths.DevinRoot);
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DevinDatabase, Pooling = false }.ToString());
            db.Open();
            void Execute(string sql)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql; command.ExecuteNonQuery();
            }
            Execute("PRAGMA journal_mode=WAL; CREATE TABLE sessions(id TEXT PRIMARY KEY,model TEXT); CREATE TABLE message_nodes(row_id INTEGER PRIMARY KEY, session_id TEXT, chat_message TEXT, created_at INTEGER); INSERT INTO sessions VALUES ('s','fallback'),('other','fallback')");
            var now = DateTimeOffset.Now;
            void Insert(string session, DateTimeOffset at, int? context)
            {
                using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO message_nodes(session_id,chat_message,created_at) VALUES ($s,$msg,$t)";
                var metrics = new { input_tokens = 20, cache_read_tokens = 80, cache_creation_tokens = 10, output_tokens = 13, context_input_tokens = context };
                var msg = new { role = "assistant", message_id = "same", content = "SENSITIVE-BODY", metadata = new { metrics, generation_model = "per-reply-model", created_at = at.ToString("O") } };
                command.Parameters.AddWithValue("$s", session);
                command.Parameters.AddWithValue("$msg", JsonSerializer.Serialize(msg));
                command.Parameters.AddWithValue("$t", now.ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }
            Insert("s", now, 110); Insert("s", now, 110);
            Execute("""INSERT INTO message_nodes(session_id,chat_message,created_at) VALUES ('s','{"role":"user","message_id":"ignore"}',0),('s','{broken',0)""");
            var scanner = new UsageScanner(paths);
            var first = scanner.Scan();
            Assert.Equal(123, first.DevinTotal!.TotalTokens);
            Assert.Equal(1, first.DevinTotal.Turns);
            Assert.Equal(80, first.DevinTotal.CacheReadTokens);
            Assert.Equal(10, first.DevinTotal.CacheWriteTokens);
            Assert.Equal("per-reply-model", Assert.Single(first.Cli.Recent).Model);
            scanner.Scan();
            Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
            Insert("other", now.AddDays(-95), null);
            var second = scanner.Scan();
            Assert.Equal(123, second.Cli.TotalTokens);
            Assert.Equal(246, second.DevinTotal!.TotalTokens);
            Assert.Equal(2, second.Statistics!.Days.Sum(x => x.Calls));
            Assert.Equal(0, second.Api.Calls);
            var card = new QuotaScanner(paths).Scan(ProcessReport.Empty with { DevinProcesses = 1 }, second.Cli, devinTotal: second.DevinTotal).Single(x => x.Name == "Devin");
            Assert.True(card.IsRunning);
            Assert.Equal(246, card.DesktopTokens!.Total!.TotalTokens);
            Execute("DELETE FROM message_nodes; DELETE FROM sessions;");
            Assert.Equal(246, scanner.Scan().DevinTotal!.TotalTokens);
            db.Close();
            File.Delete(paths.DevinDatabase);
            var restarted = new UsageScanner(paths).Scan();
            Assert.Equal(246, restarted.DevinTotal!.TotalTokens);
            Assert.DoesNotContain("SENSITIVE-BODY", File.ReadAllText(Path.Combine(paths.LocalDataRoot, UsageScanner.CacheFileName)));
        }
        finally { Directory.Delete(root, true); }
    }
}
