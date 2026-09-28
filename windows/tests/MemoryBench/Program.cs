using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeGauge.Core;

if (args is ["--serialization"])
{
    SerializationProbe.Run();
    return;
}

if (args.Length != 3) throw new ArgumentException("Usage: MemoryBench <synthetic-home> <label> <report.json>");
var home = Path.GetFullPath(args[0]);
var label = args[1];
Directory.CreateDirectory(home);
var ready = Path.Combine(home, "fixture-ready");
if (!File.Exists(ready))
{
    var now = DateTimeOffset.Now;
    foreach (var source in new[] { "claude", "codex" })
    {
        var folder = Path.Combine(home, "." + source, source == "claude" ? "projects" : "sessions", "benchmark");
        Directory.CreateDirectory(folder);
        for (var file = 0; file < 10; file++)
        {
            using var writer = new StreamWriter(Path.Combine(folder, file + ".jsonl"));
            writer.WriteLine(JsonSerializer.Serialize(new { type = "message", text = new string('x', 160_000) }));
            writer.WriteLine(JsonSerializer.Serialize(new { type = "session_meta", payload = new { model = "model-" + file, cwd = "C:\\Synthetic\\Benchmark" } }));
            for (var row = 0; row < 1000; row++)
            {
                var stamp = now.AddDays(-(row % 30)).AddSeconds(-row).ToString("O");
                if (source == "claude")
                    writer.WriteLine(JsonSerializer.Serialize(new { type = "assistant", timestamp = stamp,
                        requestId = file + "-" + row, message = new { model = "model-" + file,
                            usage = new { input_tokens = 1000, output_tokens = 200, cache_read_input_tokens = 8000, cache_creation_input_tokens = 500 } } }));
                else writer.WriteLine(JsonSerializer.Serialize(new { type = "event_msg", timestamp = stamp,
                    payload = new { type = "token_count", info = new { model_context_window = 200000,
                        last_token_usage = new { input_tokens = 9500, output_tokens = 200, cached_input_tokens = 8000, reasoning_output_tokens = 50, total_tokens = 9700 } } } }));
            }
        }
    }
    File.WriteAllText(ready, now.ToString("O"));
}
var paths = new AppPaths(home, Path.Combine(home, "cache-" + label));
var reports = new List<object>();
void Measure(string name, int runs, Action action)
{
    action();
    var before = GC.GetTotalAllocatedBytes(true);
    var clock = Stopwatch.StartNew();
    for (var i = 0; i < runs; i++) action();
    clock.Stop();
    reports.Add(new { Name = name, Runs = runs, AllocatedBytesPerRun = (GC.GetTotalAllocatedBytes(true) - before) / runs,
        MillisecondsPerRun = Math.Round(clock.Elapsed.TotalMilliseconds / runs, 3) });
}
using var empty = new MemoryStream([1, 2, 3]);
Measure("unchanged-json-read", 100, () => JsonLineReader.Read(empty, empty.Length, empty.Length, (_, _) => { }));
var scanner = new UsageScanner(paths);
UsageScanResult snapshot = scanner.Scan();
Measure("unchanged-usage-scan", 12, () => snapshot = scanner.Scan());
var sessions = new SessionMonitor(paths);
Measure("unchanged-session-scan", 12, () => sessions.Scan(DateTimeOffset.Now));
var quotas = new QuotaScanner(paths);
Measure("unchanged-quota-scan", 12, () => quotas.Scan(ProcessReport.Empty));
var nowForStats = DateTimeOffset.Parse(File.ReadAllText(ready));
var records = Enumerable.Range(0, 20000).Select(i => new InteractionRecord(i.ToString(), "Codex", "model-" + i % 10,
    nowForStats.AddDays(-(i % 30)).AddSeconds(-i), 9500, 8000, 500, 200, 50)).ToArray();
Measure("statistics-20000-records", 12, () => UsageStatistics.Build(records, nowForStats));
var totals = new { snapshot.Cli.Turns, snapshot.Cli.ContextTokens, snapshot.Cli.OutputTokens,
    snapshot.Cli.TotalTokens, snapshot.Statistics, snapshot.Api, snapshot.PiDesktopTotal };
var checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(totals)));
using var process = Process.GetCurrentProcess();
var report = new { Label = label, ResultsChecksum = checksum, Benchmarks = reports,
    ManagedBytes = GC.GetTotalMemory(false), PeakWorkingSetBytes = process.PeakWorkingSet64 };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(args[2], json);
Console.WriteLine(json);
