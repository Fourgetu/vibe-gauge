using Microsoft.Data.Sqlite;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ZCodeUsageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibegauge-zcode-" + Guid.NewGuid().ToString("N"));
    private AppPaths Paths => new(root, Path.Combine(root, "local"));

    private SqliteConnection Database()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ZCodeDatabase)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.ZCodeDatabase, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS model_usage (
                id TEXT PRIMARY KEY, model_id TEXT, status TEXT, started_at INTEGER, completed_at INTEGER,
                input_tokens INTEGER, output_tokens INTEGER, reasoning_tokens INTEGER,
                cache_read_input_tokens INTEGER, cache_creation_input_tokens INTEGER
            );
            """);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, string id, DateTimeOffset? at = null, string status = "completed",
        long input = 100, long output = 20)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO model_usage VALUES ($id, 'zcode-test-model', $status, $at, $at, $input, $output, 5, 80, 10)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", (at ?? DateTimeOffset.Now.AddMinutes(-1)).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$input", input);
        command.Parameters.AddWithValue("$output", output);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void NormalizedInputIncludesCacheAndOutputIncludesReasoning()
    {
        using (var db = Database()) Insert(db, "call-1");
        var record = Assert.Single(ZCodeUsage.Read(Paths.ZCodeDatabase));
        Assert.Equal("ZCode", record.Source);
        Assert.Equal(100, record.ContextTokens);
        Assert.Equal(20, record.OutputTokens);
        Assert.Equal(120, record.TotalTokens);
        Assert.Equal(80, record.CacheReadTokens);
        Assert.Equal(10, record.CacheWriteTokens);
        Assert.Equal(5, record.ThinkingTokens);
    }

    [Fact]
    public void DailyHistoryAndLocalLifetimeIncludeZCodeButApiDoesNot()
    {
        using (var db = Database())
        {
            Insert(db, "today");
            Insert(db, "past", DateTimeOffset.Now.AddDays(-2));
            Insert(db, "future", DateTimeOffset.Now.AddDays(1));
        }
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(120, result.Cli.TotalTokens);
        Assert.Equal("ZCode", Assert.Single(result.Cli.Recent).Source);
        Assert.Equal(240, result.ZCodeTotal!.TotalTokens);
        Assert.Equal(2, result.Statistics!.Days.Sum(x => x.Calls));
        Assert.Contains(result.Statistics.Models, x => x.Source == "ZCode");
        Assert.Equal(0, result.Api.Calls);
        var cards = new QuotaScanner(Paths).Scan(ProcessReport.Empty with { ZCodeProcesses = 1 }, result.Cli, result.PiDesktopTotal, result.ZCodeTotal);
        Assert.Equal(new[] { "Claude", "Codex", "Gemini", "ZCode", "PI-Desktop", "Ollama" }, cards.Select(x => x.Name));
        var card = cards.Single(x => x.Name == "ZCode");
        Assert.True(card.IsRunning);
        Assert.Contains("今日 1 次", card.Detail);
        Assert.Contains("本地累计 2 次", card.Detail);
        Assert.Equal(2, card.CompactDetail.Split('\n').Length);
        Assert.Null(card.Weekly);
    }

    [Fact]
    public void DeletedRowsAndDatabaseKeepObservedConsumptionAfterRestart()
    {
        using (var db = Database()) { Insert(db, "a"); Insert(db, "b"); }
        var scanner = new UsageScanner(Paths);
        Assert.Equal(240, scanner.Scan().ZCodeTotal!.TotalTokens);
        using (var db = Database()) Execute(db, "DELETE FROM model_usage");
        Assert.Equal(240, scanner.Scan().ZCodeTotal!.TotalTokens);
        Assert.Equal(240, new UsageScanner(Paths).Scan().ZCodeTotal!.TotalTokens);
        File.Delete(Paths.ZCodeDatabase);
        Assert.Equal(240, new UsageScanner(Paths).Scan().ZCodeTotal!.TotalTokens);
        Assert.False(File.Exists(Paths.ZCodeDatabase));
    }

    [Fact]
    public void RestoredDatabaseAndRepeatedIdsDoNotDuplicateConsumption()
    {
        using (var db = Database()) Insert(db, "stable");
        Assert.Equal(120, new UsageScanner(Paths).Scan().ZCodeTotal!.TotalTokens);
        File.Delete(Paths.ZCodeDatabase);
        using (var db = Database()) { Insert(db, "stable"); Insert(db, "new"); }
        Assert.Equal(240, new UsageScanner(Paths).Scan().ZCodeTotal!.TotalTokens);
    }

    [Fact]
    public void WalUpdatesAreObservedAndUnchangedDatabaseIsSkipped()
    {
        using var db = Database();
        Execute(db, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        Insert(db, "first");
        var scanner = new UsageScanner(Paths);
        Assert.Equal(120, scanner.Scan().ZCodeTotal!.TotalTokens);
        scanner.Scan();
        Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
        Insert(db, "second");
        Assert.Equal(240, scanner.Scan().ZCodeTotal!.TotalTokens);
        Assert.Equal(1, scanner.LastDiagnostics.FilesRead);
        Assert.Equal(240, new UsageScanner(Paths).Scan().ZCodeTotal!.TotalTokens);
    }

    [Fact]
    public void CorrectedUsageReplacesAnExistingRequestWithoutAddingACall()
    {
        using (var db = Database()) Insert(db, "corrected");
        Assert.Equal(120, new UsageScanner(Paths).Scan().ZCodeTotal!.TotalTokens);
        using (var db = Database()) Execute(db, "UPDATE model_usage SET input_tokens=150");
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(170, result.ZCodeTotal!.TotalTokens);
        Assert.Equal(1, result.ZCodeTotal.Turns);
    }

    [Fact]
    public void RunningRequestsWaitUntilFinalizedButFailedBillableCallsCount()
    {
        using (var db = Database())
        {
            Insert(db, "in-progress", status: "running");
            Insert(db, "error", status: "error");
            Insert(db, "cancelled", status: "cancelled");
            Insert(db, "empty", input: 0, output: 0);
        }
        var scanner = new UsageScanner(Paths);
        Assert.Equal(2, scanner.Scan().ZCodeTotal!.Turns);
        using (var db = Database()) Execute(db, "UPDATE model_usage SET status='completed' WHERE id='in-progress'");
        Assert.Equal(3, scanner.Scan().ZCodeTotal!.Turns);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(long.MaxValue, 20)]
    [InlineData(100, -1)]
    public void InvalidOrOverflowingCountersAreIgnored(long input, long output)
    {
        using (var db = Database()) Insert(db, "bad", input: input, output: output);
        Assert.Empty(ZCodeUsage.Read(Paths.ZCodeDatabase));
    }

    [Fact]
    public void ReadFailureKeepsLastValuesAndReportsAnError()
    {
        using (var db = Database()) Insert(db, "saved");
        var scanner = new UsageScanner(Paths);
        scanner.Scan();
        using (var db = Database()) Execute(db, "DROP TABLE model_usage");
        var result = scanner.Scan();
        Assert.Equal(120, result.ZCodeTotal!.TotalTokens);
        Assert.Contains("无法读取", result.Cli.Error);
        var card = new QuotaScanner(Paths).Scan(ProcessReport.Empty, result.Cli, result.PiDesktopTotal, result.ZCodeTotal)
            .Single(x => x.Name == "ZCode");
        Assert.Equal(ProviderDataState.ReadFailed, card.DataState);
        Assert.Contains("读取失败", card.CompactDetail);
        Assert.Contains("120", card.Detail);
    }

    [Fact]
    public void MissingDatabaseDoesNotCreateSourceFilesOrInventUsage()
    {
        var result = new UsageScanner(Paths).Scan();
        Assert.Equal(UsageDataState.NotDetected, result.ZCodeTotal!.State);
        Assert.Equal(0, result.ZCodeTotal.TotalTokens);
        Assert.False(Directory.Exists(Paths.ZCodeRoot));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
