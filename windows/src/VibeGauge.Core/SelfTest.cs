namespace VibeGauge.Core;

public static class SelfTest
{
    public static void Run()
    {
        Check(Formatting.ModelDisplayName("claude-fable-5-1") == "Fable 5.1", "model fable");
        Check(Formatting.ModelDisplayName("claude-sonnet-4-5-20250929") == "Sonnet 4.5", "model sonnet");
        Check(Formatting.CodexPlanLabel("prolite") == "Pro Lite", "codex plan");
        var now = DateTimeOffset.Parse("2026-09-22T10:00:00Z");
        Check(new QuotaWindow(66, now.AddSeconds(-1), null, TimeSpan.FromHours(5)).EffectivePercent(now) == 0, "expired quota");
        Check(new QuotaWindow(66, now.AddHours(1), null, TimeSpan.FromHours(5)).EffectivePercent(now) == 66, "active quota");
        Check(Formatting.Countdown(now.AddSeconds(90), now) == "1m", "countdown minute");
        Check(Formatting.Countdown(now.AddMinutes(109), now) == "1h49m", "countdown hour");
        Check(Formatting.ParseDate("2026-09-19T10:34:27.346461+00:00") is not null, "six digit timestamp");
        Check(Formatting.Tokens(10_000) == "1.0 万", "wan tokens");
        Check(Formatting.Tokens(100_000_000) == "1.00 亿", "yi tokens");

        var temp = Path.Combine(Path.GetTempPath(), "vibegauge-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(temp, ".claude", "projects", "fixture");
            Directory.CreateDirectory(project);
            var timestamp = DateTimeOffset.Now.ToString("O");
            var line = $"{{\"type\":\"assistant\",\"timestamp\":\"{timestamp}\",\"requestId\":\"fixture-1\",\"message\":{{\"model\":\"claude-sonnet-4-5\",\"usage\":{{\"input_tokens\":10,\"cache_read_input_tokens\":20,\"cache_creation_input_tokens\":5,\"output_tokens\":4}}}}}}";
            File.WriteAllText(Path.Combine(project, "a.jsonl"), line + Environment.NewLine + line + Environment.NewLine);
            var usage = new UsageScanner(new AppPaths(temp, Path.Combine(temp, "local"))).ScanToday();
            Check(usage.Turns == 1, "request id dedupe");
            Check(usage.ContextTokens == 35 && usage.CacheReadTokens == 20 && usage.OutputTokens == 4, "usage totals");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
    }
}
