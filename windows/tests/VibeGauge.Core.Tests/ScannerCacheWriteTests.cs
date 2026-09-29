using System.Text.Json;
using VibeGauge.Core;
using Xunit;

namespace VibeGauge.Core.Tests;

public sealed class ScannerCacheWriteTests
{
    [Fact]
    public void UnchangedScanDoesNotRewriteCacheAndNewRowsStillPersist()
    {
        var home = Path.Combine(Path.GetTempPath(), "VibeGauge-cache-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(home, ".claude", "projects", "test");
            Directory.CreateDirectory(folder);
            var log = Path.Combine(folder, "usage.jsonl");
            string Line(string id) => JsonSerializer.Serialize(new { type = "assistant", requestId = id,
                timestamp = DateTimeOffset.Now.AddMinutes(-1), message = new { model = "test-model",
                    usage = new { input_tokens = 10, output_tokens = 2 } } });
            File.WriteAllText(log, Line("first") + "\n");
            var paths = new AppPaths(home, Path.Combine(home, "local"));
            var scanner = new UsageScanner(paths);
            Assert.Equal(1, scanner.ScanToday().Turns);
            var cache = Path.Combine(paths.LocalDataRoot, UsageScanner.CacheFileName);
            var sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(cache, sentinel);
            Assert.Equal(1, scanner.ScanToday().Turns);
            Assert.Equal(sentinel, File.GetLastWriteTimeUtc(cache));
            Assert.Equal(0, scanner.LastDiagnostics.FilesRead);
            File.AppendAllText(log, Line("second") + "\n");
            Assert.Equal(2, scanner.ScanToday().Turns);
            Assert.NotEqual(sentinel, File.GetLastWriteTimeUtc(cache));
            Assert.Equal(2, new UsageScanner(paths).ScanToday().Turns);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }
}
