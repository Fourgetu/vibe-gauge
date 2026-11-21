using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class DevinWeeklyQuotaTests
{
    [Fact]
    public void QuotaFollowsActiveAccountAndWalUpdatesNotPreviousPlansOrTokenCounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibegauge-devin-quota-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DevinStateDatabase)!);
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DevinStateDatabase, Pooling = false }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT)";
            cmd.ExecuteNonQuery();
            void Store(string key, object value)
            {
                cmd.CommandText = "INSERT OR REPLACE INTO ItemTable VALUES ($key,$value)";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value));
                cmd.ExecuteNonQuery();
            }
            void Login(string user) => Store("windsurfAuthStatus", new { userStatusProtoBinaryBase64 =
                Convert.ToBase64String([0x08, 1, 0xa2, 0x02, (byte)user.Length, ..Encoding.UTF8.GetBytes(user)]) });
            var reset = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(19).ToUnixTimeSeconds());
            void Plan(string user, int remaining, bool hide = false, long? end = null) =>
                Store("windsurf.reactSettings.cachedPlanInfoData:" + user, new { isDevinUser = true, billingStrategy = "quota",
                    quota = new { weeklyRemainingPercent = remaining, weeklyResetAtUnix = end ?? reset.ToUnixTimeSeconds(), hideWeeklyQuota = hide } });
            using var scanner = new QuotaScanner(paths);
            var total = new UsageSourceSummary("Devin", UsageDataState.Available, 1, 900, 0, 0, 100, 0, "");
            PlatformStatus Scan() => scanner.Scan(ProcessReport.Empty, devinTotal: total).Single(p => p.Name == "Devin");
            Login("first"); Plan("first", 95); Plan("second", 32);
            var first = Scan();
            Assert.Equal(5, first.Weekly!.UsedPercent);
            Assert.Equal(reset, first.Weekly.ResetsAt);
            Assert.NotEmpty(first.QuotaScope);
            Login("second");
            var second = Scan();
            Assert.Equal(68, second.Weekly!.UsedPercent);
            Assert.NotEqual(first.QuotaScope, second.QuotaScope);
            Plan("second", 100); Assert.Equal(0, Scan().Weekly!.UsedPercent);
            Plan("second", 0); Assert.Equal(100, Scan().Weekly!.UsedPercent);
            Plan("second", 95, hide: true); Assert.Null(Scan().Weekly);
            Plan("second", 95, end: 1); Assert.Null(Scan().Weekly);
            Plan("second", 101); Assert.Null(Scan().Weekly);
            Login("no-plan"); Assert.Null(Scan().Weekly);
            Store("windsurfAuthStatus", new { userStatusProtoBinaryBase64 = "not-base64" });
            Assert.Null(Scan().Weekly);
            Store("windsurfAuthStatus", new { });
            var signedOut = Scan();
            Assert.Null(signedOut.Weekly);
            Assert.Equal(1000, signedOut.DesktopTokens!.Total!.TotalTokens);
        }
        finally { Directory.Delete(root, true); }
    }
}
